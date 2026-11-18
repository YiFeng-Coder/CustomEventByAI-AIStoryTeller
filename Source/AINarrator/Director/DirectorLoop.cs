using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;

namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>
    /// 导演主循环。额度和事件状态只在主线程读写。
    ///
    /// 两阶段：
    ///   1) Directing：问模型"休整 / 原版事件 / 写剧情"；
    ///   2) Writing  ：仅当选择"写剧情"时，再问一次，让它交出 Lua 脚本。
    ///
    /// 硬约束：发起请求立刻返回，绝不等待网络；结果通过 Inbox 回到主线程处理。
    /// </summary>
    public static class DirectorLoop
    {
        private enum Phase
        {
            Idle,
            Directing,
            Writing
        }

        private const int FailBackoffTicks = 2500;
        private const int MaxInboxPerUpdate = 8;
        private const int WriterMinTokens = 2400;

        private static readonly ConcurrentQueue<Action> Inbox = new ConcurrentQueue<Action>();

        private sealed class RequestContext
        {
            public Map Map;
            public Phase Phase;
            public bool Forced;
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
        }

        private sealed class WriterJob
        {
            public Map Map;
            public Proposal Proposal;
            public bool Forced;
        }

        private static Game _game;
        private static NarratorState _state;
        // 编剧和导演各有一个在途槽位。编剧忙时最多排一个概念。
        private static RequestContext _directorRequest;
        private static RequestContext _writerRequest;
        private static WriterJob _queuedJob;
        private static bool _warnedNotConfigured;

        // 编剧阶段的修复重试：次数由设置 maxScriptRetries 决定（默认 1），把校验失败原因回喂给模型。
        private static int _writerRetries;
        private static string _lastWriterScript;
        private static string _lastWriterError;
        private static WriterJob _writerJob;

        // 一次导演/编剧请求内的 Def 查询状态（最多 maxLookupRounds 轮）。
        private static LookupSession _directorLookup;
        private static LookupSession _writerLookup;

        public static bool IsActiveStoryteller =>
            Current.Game != null && Find.Storyteller?.def?.defName == "CustomEventByAI_AIStoryTeller";
        public static bool InFlight => DirectorBusy || WriterBusy;
        public static bool WriterBusy => _writerRequest != null;
        public static bool DirectorBusy => _directorRequest != null;
        public static bool HasQueuedConcept => _queuedJob != null;
        public static string PhaseName => (DirectorBusy ? Phase.Directing : WriterBusy ? Phase.Writing : Phase.Idle).ToString();
        public static long LastRealtimeMs { get; private set; }
        public static string LastStatus { get; private set; } = Loc.Pick("(尚未请求)", "(no request yet)");
        public static string LastPrompt { get; private set; } = "";
        public static string LastWriterPrompt { get; private set; } = "";
        public static string LastResponse { get; private set; } = "";
        public static string DirectorLookupStatus =>
            _directorLookup != null ? _directorLookup.Status : Loc.Pick("查询轮 0/0", "lookup round 0/0");
        public static string WriterLookupStatus =>
            _writerLookup != null ? _writerLookup.Status : Loc.Pick("查询轮 0/0", "lookup round 0/0");

        public static void Update()
        {
            RefreshSession(AINarratorMod.Settings);
            DrainInbox();
        }

        public static void Tick()
        {
            if (!IsActiveStoryteller) return;

            // 每 60 tick 才判断一次要不要请求；MTB 间隔就是按 60 tick 换算的。
            int now = Find.TickManager.TicksGame;
            if (now % 60 != 0) return;

            AINarratorSettings settings = AINarratorMod.Settings;
            if (settings == null) return;

            Map map = RefreshSession(settings);
            NarratorState state = _state;
            if (map == null || state == null || DirectorBusy) return;

            state.RemoveExpiredPending(now);

            if (!settings.IsConfigured)
            {
                if (!_warnedNotConfigured)
                {
                    _warnedNotConfigured = true;
                    Verse.Log.Message(AINarratorMod.LogPrefix +
                                      "not configured yet (endpoint/model/key empty); director stays idle.");
                }

                return;
            }

            _warnedNotConfigured = false;

            if (!settings.enableAutoTrigger) return;
            if (state.lastFailTick > 0 && now - state.lastFailTick < FailBackoffTicks) return;

            if (state.lastRequestRealtimeMs > 0 && settings.minIntervalMinutes > 0)
            {
                long elapsedMs = Environment.TickCount64 - state.lastRequestRealtimeMs;
                if (elapsedMs < (long)settings.minIntervalMinutes * 60000L) return;
            }

            if (!CanCallApi(state, settings)) return;

            if (!Rand.MTBEventOccurs(settings.aiMtbDays, 60000f, 60f)) return;

            DispatchDirector(map, state, settings, forced: false);
        }

        /// <summary>调试窗的「立即请求一次」：跳过自动触发条件，保留配置、配额和并发检查。</summary>
        public static bool ForceRequest()
        {
            AINarratorSettings settings = AINarratorMod.Settings;
            Map map = RefreshSession(settings);
            if (!CanRequestManually(map, settings)) return false;
            if (!IsActiveStoryteller)
                return RefuseRequest(Loc.Pick("当前未选择 AI 叙述者", "the AI storyteller is not selected right now"));
            if (DirectorBusy)
                return RefuseRequest(Loc.Pick("导演请求已在进行中", "a director request is already in flight"));

            return DispatchDirector(map, _state, settings, forced: true);
        }

        /// <summary>
        /// 调试：跳过导演决策，直接用给定概念让模型写一段 Lua 剧情。
        /// 用来单独验证"编剧 + 运行时"这条链，不用等 MTB，也不受导演选什么的影响。
        /// </summary>
        public static bool ForceWriter(string concept)
        {
            AINarratorSettings settings = AINarratorMod.Settings;
            Map map = RefreshSession(settings);
            if (!CanRequestManually(map, settings)) return false;
            if (settings == null || !settings.enableLuaEvents)
                return RefuseRequest(Loc.Pick("玩家已在 Mod 设置里关闭 Lua 剧情事件",
                    "the player disabled Lua story events in the mod settings"));
            if (InFlight) return RefuseRequest(Loc.Pick("当前有请求正在进行", "a request is already in progress"));

            if (string.IsNullOrWhiteSpace(concept))
            {
                concept = Loc.Pick("一支商队在附近失踪，玩家要决定是否派人搜寻，以及是否相信幸存者的说法。",
                    "A trade caravan has vanished nearby. The player must decide whether to send a search party, and whether to believe what the survivors say.");
            }

            var proposal = new Proposal
            {
                Kind = ProposalKind.LuaEvent,
                Title = Loc.Pick("调试剧情", "Debug story"),
                Concept = concept.Trim(),
                Reason = Loc.Pick("debug: 强制编剧", "debug: forced writer")
            };

            _queuedJob = null;
            return BeginWriter(_state, settings, new WriterJob
            {
                Map = map,
                Proposal = proposal,
                Forced = true
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // 第一次调用：导演
        // ─────────────────────────────────────────────────────────────────────
        private static bool DispatchDirector(Map map, NarratorState state, AINarratorSettings settings,
            bool forced, LookupSession lookup = null)
        {
            if (!CanCallApi(state, settings))
            {
                LastStatus = Loc.Pick("拒绝：今日 API 调用已达上限", "refused: daily API call limit reached");
                return false;
            }

            int now = Find.TickManager.TicksGame;
            Snapshot snapshot = SnapshotBuilder.Build(map, state);
            if (snapshot == null)
            {
                LastStatus = Loc.Pick("快照构建失败", "snapshot build failed");
                return false;
            }

            List<IncidentCandidate> candidates = CandidateTable.SelectForPrompt(map, snapshot.ThreatPoints,
                state, settings, now, 16);
            if (candidates.Count == 0)
            {
                LastStatus = Loc.Pick("没有可用候选（当前局势下没有能触发的事件；威胁点数 "
                             + snapshot.ThreatPoints.ToString("F0") + "）",
                    "no usable candidates (nothing can fire in the current situation; threat points "
                             + snapshot.ThreatPoints.ToString("F0") + ")");
                return false;
            }

            _directorLookup = lookup ?? new LookupSession(settings.maxLookupRounds);

            PromptBuilder.Build(snapshot, candidates, state, now, settings,
                out string system, out string user, out _, _directorLookup);

            var request = new LlmRequest
            {
                SystemPrompt = system,
                UserPrompt = user,
                Endpoint = settings.endpoint,
                Model = settings.model,
                ApiKey = settings.apiKey,
                Temperature = settings.temperature,
                MaxTokens = settings.maxOutputTokens,
                TimeoutSeconds = settings.timeoutSeconds,
                WantJsonObject = true
            };

            LastPrompt = user;
            LastResponse = "";
            LastWriterPrompt = "";

            Send(map, forced, Phase.Directing, request,
                Loc.Pick("导演请求中…" + (lookup != null ? " · 查询回合" : ""),
                    "director request in flight…" + (lookup != null ? " · lookup round" : "")));
            return true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 第二次调用：编剧（写 Lua）
        // ─────────────────────────────────────────────────────────────────────
        private static bool BeginWriter(NarratorState state, AINarratorSettings settings, WriterJob job)
        {
            _writerRetries = 0;
            _lastWriterScript = null;
            _lastWriterError = null;
            _writerLookup = new LookupSession(settings.maxLookupRounds);
            _writerJob = job;
            return DispatchWriter(state, settings);
        }

        private static bool DispatchWriter(NarratorState state, AINarratorSettings settings)
        {
            if (!CanStartLua(state, settings)) return false;
            if (!CanCallApi(state, settings))
            {
                LastStatus = Loc.Pick("拒绝：今日 API 调用已达上限", "refused: daily API call limit reached");
                return false;
            }

            WriterJob job = _writerJob;
            Snapshot snapshot = SnapshotBuilder.Build(job.Map, state);
            if (snapshot == null)
            {
                LastStatus = Loc.Pick("编剧阶段：快照构建失败", "writer stage: snapshot build failed");
                return false;
            }

            var candidates = CandidateTable.SelectForPrompt(job.Map, snapshot.ThreatPoints, state, settings,
                Find.TickManager.TicksGame, 16);
            PromptBuilder.BuildWriter(snapshot, job.Proposal.Title, job.Proposal.Concept, candidates, settings,
                out string system, out string user, _lastWriterScript, _lastWriterError, _writerLookup);

            int tokens = settings.maxOutputTokens > 0
                ? Math.Max(settings.maxOutputTokens, WriterMinTokens)
                : 0;

            var request = new LlmRequest
            {
                SystemPrompt = system,
                UserPrompt = user,
                Endpoint = settings.endpoint,
                Model = settings.model,
                ApiKey = settings.apiKey,
                Temperature = Math.Min(1f, settings.temperature + 0.1f),
                MaxTokens = tokens,
                TimeoutSeconds = settings.writerTimeoutSeconds,

                // 编剧要的是「TITLE 行 + ```lua 代码块」，不是 JSON。
                // 这里一旦下发 response_format=json_object，服务端就会强制模型把整段脚本
                // 塞进一个字符串字段（换行还得自己转义），与提示词里的输出格式正面打架：
                // 模型通常照提示词写成 {"lua":"```lua\n...\n```"}，而解析器先按代码块提取、
                // 单行 JSON 里没有真换行 → 提取失败 → 退回 JSON 解析时又被剥围栏逻辑吃掉内容，
                // 最后报「JSON 里缺少 lua 字段」。所以编剧调用必须关掉 JSON 模式。
                WantJsonObject = false
            };

            LastWriterPrompt = user;
            if (settings.debugLogging)
            {
                Verse.Log.Message(AINarratorMod.LogPrefix + "writer prompt chars="
                                  + (system.Length + user.Length) + " (system=" + system.Length
                                  + ", user=" + user.Length + ")");
            }

            Send(job.Map, job.Forced, Phase.Writing, request,
                _writerRetries > 0
                    ? Loc.Pick("编剧请求中…（第 " + (_writerRetries + 1) + " 轮，修正上一版错误）",
                        "writer request in flight… (round " + (_writerRetries + 1) + ", fixing the previous version)")
                    : Loc.Pick("编剧请求中…（写 Lua）", "writer request in flight… (writing Lua)"));
            return true;
        }

        private static void Send(Map map, bool forced, Phase phase, LlmRequest request, string status)
        {
            ILlmGateway gateway = LlmGatewayFactory.Create(request.Endpoint);
            var context = new RequestContext { Map = map, Forced = forced, Phase = phase };
            context.Cancellation.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, request.TimeoutSeconds) + 5));

            if (phase == Phase.Writing) _writerRequest = context;
            else _directorRequest = context;

            LastStatus = (forced ? Loc.Pick("[手动] ", "[manual] ") : "") + status + " (" + gateway.Name + ")";

            request.AuthorizeAttemptAsync = ct => AuthorizeAttempt(context, ct);
            _ = Task.Run(async () =>
            {
                LlmResult result;
                try
                {
                    result = await gateway.CompleteAsync(request, context.Cancellation.Token).ConfigureAwait(false)
                        ?? LlmResult.Failure(LlmErrorKind.Unknown, "null result");
                }
                catch (OperationCanceledException)
                {
                    result = LlmResult.Failure(LlmErrorKind.Cancelled, "cancelled");
                }
                catch (Exception e)
                {
                    result = LlmResult.Failure(LlmErrorKind.Unknown, e.Message);
                }

                Inbox.Enqueue(() => OnCompleted(context, result));
            });
        }

        private static Task<bool> AuthorizeAttempt(RequestContext context, CancellationToken ct)
        {
            var permission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Inbox.Enqueue(() =>
            {
                if (ct.IsCancellationRequested || !IsCurrent(context))
                {
                    permission.TrySetCanceled(ct);
                    return;
                }

                NarratorState state = _state;
                AINarratorSettings settings = AINarratorMod.Settings;
                int now = Find.TickManager.TicksGame;
                ResetDailyCounters(state, now);
                if (!CanCallApi(state, settings))
                {
                    permission.TrySetResult(false);
                    return;
                }

                state.callsToday++;
                state.lastRequestTick = now;
                state.lastRequestRealtimeMs = Environment.TickCount64;
                LastRealtimeMs = state.lastRequestRealtimeMs;
                permission.TrySetResult(true);
            });
            return permission.Task.WaitAsync(ct);
        }

        private static Map RefreshSession(AINarratorSettings settings)
        {
            Game game = Current.Game;
            NarratorState state = game != null ? NarratorWorldComponent.Instance?.State : null;
            if (!ReferenceEquals(game, _game) || !ReferenceEquals(state, _state))
            {
                CancelRequest(ref _directorRequest);
                CancelRequest(ref _writerRequest);
                _queuedJob = null;
                _writerJob = null;
                _directorLookup = null;
                _writerLookup = null;
                _writerRetries = 0;
                _lastWriterScript = null;
                _lastWriterError = null;
                _warnedNotConfigured = false;
                LastStatus = Loc.Pick("(尚未请求)", "(no request yet)");
                LastRealtimeMs = 0;
                LastPrompt = "";
                LastWriterPrompt = "";
                LastResponse = "";
                _game = game;
                _state = state;
            }

            if (state == null) return null;
            ResetDailyCounters(state, Find.TickManager.TicksGame);

            Map map = Find.AnyPlayerHomeMap;
            bool activeStoryteller = IsActiveStoryteller;
            bool allowAutomatic = activeStoryteller && (settings?.enableAutoTrigger ?? false);
            if (_queuedJob != null && !CanContinue(_queuedJob.Map, _queuedJob.Forced, map, allowAutomatic))
                _queuedJob = null;
            if (_directorRequest != null && (!activeStoryteller ||
                !CanContinue(_directorRequest.Map, _directorRequest.Forced, map, allowAutomatic)))
                CancelRequest(ref _directorRequest);
            if (_writerRequest != null &&
                !CanContinue(_writerRequest.Map, _writerRequest.Forced, map, allowAutomatic))
            {
                CancelRequest(ref _writerRequest);
                _writerJob = null;
                if (_queuedJob != null && settings != null)
                    OnWriterSlotFreed(state, settings);
            }

            return map;
        }

        private static bool CanContinue(Map requestMap, bool forced, Map map, bool allowAutomatic) =>
            ReferenceEquals(requestMap, map) && (forced || allowAutomatic);

        private static void CancelRequest(ref RequestContext context)
        {
            RequestContext request = context;
            if (request == null) return;

            context = null;
            request.Cancellation.Cancel();
            LastStatus = Loc.Pick("请求已取消（存档、地图或自动触发条件变化）",
                "request cancelled (save, map, or auto-trigger condition changed)");
        }

        private static void ResetDailyCounters(NarratorState state, int now)
        {
            int day = now / 60000;
            if (state.callDay == day) return;

            state.callDay = day;
            state.callsToday = 0;
            state.luaEventsToday = 0;
        }

        private static bool CanCallApi(NarratorState state, AINarratorSettings settings) =>
            settings != null && (settings.maxApiCallsPerDay <= 0 || state.callsToday < settings.maxApiCallsPerDay);

        private static bool CanRequestManually(Map map, AINarratorSettings settings)
        {
            if (_state == null || map == null)
                return RefuseRequest(Loc.Pick("当前没有可用的殖民地地图", "no colony map is available right now"));
            if (settings == null || !settings.IsConfigured)
                return RefuseRequest(Loc.Pick("请先完成 API 配置", "configure the API first"));
            return true;
        }

        private static bool RefuseRequest(string reason)
        {
            LastStatus = Loc.Pick("拒绝：" + reason, "refused: " + reason);
            return false;
        }

        private static bool CanStartLua(NarratorState state, AINarratorSettings settings)
        {
            // 总开关优先：玩家关掉后，导演的 lua_event、调试窗的强制编剧、
            // 甚至模型无视提示词硬写出来的脚本，都在这里被拦下，一次编剧调用都不发。
            if (settings == null || !settings.enableLuaEvents)
            {
                LastStatus = Loc.Pick("拒绝：玩家已在 Mod 设置里关闭 Lua 剧情事件",
                    "refused: the player disabled Lua story events in the mod settings");
                return false;
            }

            if (settings.maxLuaEventsPerDay <= 0 || state.luaEventsToday < settings.maxLuaEventsPerDay)
                return true;

            LastStatus = Loc.Pick("拒绝：今日剧情事件已达上限（" + settings.maxLuaEventsPerDay + "）",
                "refused: daily story event limit reached (" + settings.maxLuaEventsPerDay + ")");
            return false;
        }

        private static void DrainInbox()
        {
            for (int i = 0; i < MaxInboxPerUpdate; i++)
            {
                if (!Inbox.TryDequeue(out Action action)) return;

                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Verse.Log.Error(AINarratorMod.LogPrefix + "inbox action failed: " + e);
                }
            }
        }

        private static bool IsCurrent(RequestContext context) =>
            ReferenceEquals(context, context.Phase == Phase.Writing ? _writerRequest : _directorRequest);

        private static void OnCompleted(RequestContext context, LlmResult result)
        {
            context.Cancellation.Dispose();
            if (!IsCurrent(context)) return;

            bool writing = context.Phase == Phase.Writing;
            if (writing) _writerRequest = null;
            else _directorRequest = null;

            NarratorState state = _state;
            AINarratorSettings settings = AINarratorMod.Settings;
            try
            {
                if (settings == null) return;
                int now = Find.TickManager.TicksGame;
                ResetDailyCounters(state, now);

                if (!result.Ok)
                {
                    LastResponse = "";
                    if (result.Kind == LlmErrorKind.QuotaExceeded)
                    {
                        LastStatus = Loc.Pick("拒绝：" + result.Error, "refused: " + result.Error);
                        return;
                    }

                    state.failStreak++;
                    state.lastFailTick = now;
                    LastStatus = Loc.Pick("失败(" + result.Kind + "): " + result.Error,
                        "failed (" + result.Kind + "): " + result.Error);
                    Verse.Log.Warning(AINarratorMod.LogPrefix + "request failed: " + result);
                    return;
                }

                LastResponse = result.Text;
                if (writing)
                    HandleWriter(state, settings, result.Text, result.FinishReason, context.Forced);
                else
                    HandleDirector(context.Map, state, settings, result.Text, context.Forced);
            }
            finally
            {
                if (writing && !WriterBusy)
                    OnWriterSlotFreed(state, settings);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void HandleDirector(Map map, NarratorState state, AINarratorSettings settings, string text,
            bool forced)
        {
            Proposal proposal = ProposalParser.Parse(text);
            if (proposal.Kind == ProposalKind.Invalid)
            {
                state.failStreak++;
                state.lastFailTick = Find.TickManager.TicksGame;
                LastStatus = Loc.Pick("提案非法：" + proposal.Error, "invalid proposal: " + proposal.Error);
                Verse.Log.Warning(AINarratorMod.LogPrefix + "invalid proposal: " + proposal.Error
                                  + " | raw=" + Trim(proposal.Raw, 200));
                return;
            }

            state.failStreak = 0;
            int now = Find.TickManager.TicksGame;

            if (proposal.Kind == ProposalKind.Lookup)
            {
                if (!settings.enableDefLookup || _directorLookup == null ||
                    !_directorLookup.CanLookup(settings))
                {
                    LastStatus = Loc.Pick("拒绝：查询轮不可用或已用尽",
                        "refused: lookup rounds are unavailable or exhausted");
                    return;
                }

                if (!CanCallApi(state, settings))
                {
                    LastStatus = Loc.Pick("拒绝：今日 API 调用已达上限，无法再查询",
                        "refused: daily API call limit reached, cannot look up again");
                    return;
                }

                proposal.Lookup.Limit = Math.Min(proposal.Lookup.Limit, settings.lookupMaxResults);
                DefLookupResult result = DefQueryService.Search(proposal.Lookup, map, state, settings, now);
                _directorLookup.Add(proposal.Lookup, result);
                LastStatus = Loc.Pick("导演查询：" + _directorLookup.Status,
                    "director lookup: " + _directorLookup.Status);
                DispatchDirector(map, state, settings, forced, _directorLookup);
                return;
            }

            if (proposal.Kind == ProposalKind.Rest)
            {
                LastStatus = Loc.Pick("休整：" + proposal.Reason, "rest: " + proposal.Reason);
                if (settings.debugLogging) Verse.Log.Message(AINarratorMod.LogPrefix + "rest: " + proposal.Reason);
                return;
            }

            if (proposal.Kind == ProposalKind.LuaEvent)
            {
                if (!CanStartLua(state, settings)) return;

                // 第二次调用：拿概念去写脚本。
                var job = new WriterJob { Map = map, Proposal = proposal, Forced = forced };
                if (WriterBusy)
                {
                    // 编剧还在写上一段：只排队一个，新的覆盖旧的。
                    // 这里不阻塞导演——导演下一轮照样可以决策，只是概念先挂在队列里。
                    _queuedJob = job;
                    LastStatus = Loc.Pick("编剧正忙，已排队概念：" + proposal.Title,
                        "writer is busy; concept queued: " + proposal.Title);
                    return;
                }

                BeginWriter(state, settings, job);
                return;
            }

            IncidentCandidate candidate = CandidateTable.Find(proposal.DefName);
            if (candidate == null)
            {
                state.failStreak++;
                state.lastFailTick = now;
                LastStatus = Loc.Pick("拒绝：候选表里没有 " + proposal.DefName,
                    "refused: the candidate table has no " + proposal.DefName);
                Verse.Log.Warning(AINarratorMod.LogPrefix + "rejected hallucinated defName: " + proposal.DefName);
                return;
            }

            if (!BudgetPolicy.CanUse(candidate, state, now, settings, out string denyReason))
            {
                LastStatus = Loc.Pick("拒绝：" + denyReason, "refused: " + denyReason);
                if (settings.debugLogging)
                {
                    Verse.Log.Message(AINarratorMod.LogPrefix + "budget denied " + candidate.DefName + ": " + denyReason);
                }

                return;
            }

            int delay = Rand.Range(600, 2500);
            state.pending.Clear();
            state.pending.Add(new PendingEventRecord
            {
                defName = candidate.DefName,
                category = candidate.Category,
                pointsMultiplier = proposal.PointsMultiplier,
                fireAtTick = now + delay,
                createdTick = now,
                reason = proposal.Reason,
                announce = (proposal.Announce ?? "").Trim(),
                mapId = map.uniqueID
            });

            LastStatus = Loc.Pick("已批准：" + candidate.DefName + "（" + delay + " tick 后发射）",
                "approved: " + candidate.DefName + " (fires in " + delay + " ticks)");
            if (settings.debugLogging)
            {
                Verse.Log.Message(AINarratorMod.LogPrefix + "approved " + candidate.DefName
                                  + " in " + delay + " ticks: " + proposal.Reason);
            }
        }

        private static void HandleWriter(NarratorState state, AINarratorSettings settings,
            string text, string finishReason, bool forced)
        {
            if (!CanStartLua(state, settings)) return;

            // finish_reason=length 表示输出被上限截断：内容是"成功"返回的半截脚本，
            // 光看解析/编译错误会以为是模型写错了格式。这个信息要写进日志，也要回喂给模型。
            bool truncated = string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase);
            string truncationNote = truncated
                ? Loc.Pick("（这次输出被 token 上限截断，脚本没写完）",
                    " (this output was cut off by the token limit, so the script is incomplete)")
                : "";

            if (settings.debugLogging && finishReason.Length > 0)
            {
                Verse.Log.Message(AINarratorMod.LogPrefix + "writer finish_reason=" + finishReason
                                  + ", chars=" + (text ?? "").Length);
            }

            if (LookupProtocol.TryParseWriter(text, out DefLookupRequest lookup,
                    out string lookupError))
            {
                if (!settings.enableDefLookup || _writerLookup == null ||
                    !_writerLookup.CanLookup(settings))
                {
                    RejectScript(state, settings,
                        Loc.Pick("查询轮不可用或已用尽" + (lookupError != null ? "：" + lookupError : ""),
                            "lookup rounds are unavailable or exhausted" + (lookupError != null ? ": " + lookupError : "")),
                        null);
                    return;
                }

                if (lookupError != null || lookup == null)
                {
                    RejectScript(state, settings,
                        Loc.Pick("LOOKUP 格式错误：" + (lookupError ?? "缺少查询参数"),
                            "malformed LOOKUP: " + (lookupError ?? "missing query arguments")), null);
                    return;
                }

                int lookupNow = Find.TickManager.TicksGame;
                if (!CanCallApi(state, settings))
                {
                    LastStatus = Loc.Pick("拒绝：今日 API 调用已达上限，无法再查询",
                        "refused: daily API call limit reached, cannot look up again");
                    return;
                }

                lookup.Limit = Math.Min(lookup.Limit, settings.lookupMaxResults);
                DefLookupResult result = DefQueryService.Search(lookup, _writerJob.Map, state, settings, lookupNow);
                _writerLookup.Add(lookup, result);
                LastStatus = Loc.Pick("编剧查询：" + _writerLookup.Status,
                    "writer lookup: " + _writerLookup.Status);
                if (settings.debugLogging)
                {
                    Verse.Log.Message(AINarratorMod.LogPrefix + "writer lookup " + _writerLookup.LastQuery
                                      + " total=" + _writerLookup.LastTotal);
                }

                DispatchWriter(state, settings);
                return;
            }

            if (!ProposalParser.TryParseWriter(text, out string title, out string lua, out string parseError))
            {
                // 只报「缺少 lua 字段」这种话没法查问题：把模型原文的头一段一起写进日志。
                if (settings.debugLogging)
                {
                    Verse.Log.Warning(AINarratorMod.LogPrefix + "writer reply unparsable: " + parseError
                                      + " | raw=" + Trim(text, 400));
                }

                RejectScript(state, settings, parseError + truncationNote, null);
                return;
            }

            if (!EventRuntime.RegisterDefinition(title, lua, out EventDefinitionRecord definition, out string defError))
            {
                RejectScript(state, settings, defError + truncationNote, lua);
                return;
            }

            // 空跑每个能预知的分支（start / 每个选项 / 定时信号）。有 Lua 运行时错误就在这里拦下，
            // 让编剧带着具体错误重写（重写轮数由 maxScriptRetries 决定），而不是等玩家点完选项才看到「阶段失败」。
            string previewError = EventRuntime.PreviewBranches(definition);
            if (previewError != null)
            {
                if (settings.debugLogging)
                {
                    Verse.Log.Warning(AINarratorMod.LogPrefix + "script preview failed: " + previewError);
                }

                RejectScript(state, settings, previewError + truncationNote, lua);
                return;
            }

            if (!EventRuntime.Start(definition, "start", null, out string startError, out bool executionStarted, forced))
            {
                if (executionStarted)
                {
                    state.luaEventsToday++;
                    state.failStreak++;
                    state.lastFailTick = Find.TickManager.TicksGame;
                    LastStatus = Loc.Pick("剧情执行失败：" + startError, "story event failed: " + startError);
                    Verse.Log.Warning(AINarratorMod.LogPrefix + "lua event failed after applying actions: " + startError);
                }
                else
                {
                    RejectScript(state, settings, startError + truncationNote, lua);
                }
                return;
            }

            state.failStreak = 0;
            state.luaEventsToday++;
            _writerRetries = 0;
            _lastWriterScript = null;
            _lastWriterError = null;
            LastStatus = Loc.Pick("剧情已开启：" + title + "（" + definition.id + "）",
                "story event started: " + title + " (" + definition.id + ")");

            if (settings.debugLogging)
            {
                Verse.Log.Message(AINarratorMod.LogPrefix + "lua event started: " + title
                                  + " [" + definition.id + "] hash=" + definition.hash);
            }
        }

        /// <summary>
        /// 脚本被拒：把原因回喂给模型重写，最多重写"玩家在设置里指定的轮数"
        /// （默认 1 = 只给一次机会，与旧行为一致），再不行才放弃。
        /// 这一步是"模型能不能写对"的关键——不给反馈它会重复同样的错误。
        /// </summary>
        private static void RejectScript(NarratorState state, AINarratorSettings settings, string reason, string lua)
        {
            if (!string.IsNullOrEmpty(lua)) _lastWriterScript = lua;
            _lastWriterError = reason;
            string details = reason;
            if (settings.debugLogging && !string.IsNullOrEmpty(lua))
            {
                const int maxLoggedLength = 8000;
                details += "\n  Lua source:\n" + (lua.Length <= maxLoggedLength
                    ? lua
                    : lua.Substring(0, maxLoggedLength) + "\n...[脚本过长，已截断]");
            }

            int maxRetries = Math.Max(0, settings.maxScriptRetries);

            if (_writerRetries < maxRetries && _writerJob != null && CanCallApi(state, settings))
            {
                // 先记轮次，让 DispatchWriter 的状态行能显示"第 N 轮"；没发出去再退回去，不算一轮。
                _writerRetries++;
                if (DispatchWriter(state, settings))
                {
                    Verse.Log.Warning(AINarratorMod.LogPrefix + "script rejected (rewrite round "
                                       + _writerRetries + "/" + maxRetries + "): " + details);
                    return;
                }

                _writerRetries--;
            }

            state.failStreak++;
            state.lastFailTick = Find.TickManager.TicksGame;

            // 轮数 = 首写 + 实际发出的重写；被拒次数为 0 时也要说清楚是"没重写"。
            int rounds = 1 + _writerRetries;
            LastStatus = Loc.Pick("脚本最终被拒（共 " + rounds + " 轮）：" + reason,
                "script rejected for good after " + rounds + " round(s): " + reason);
            Verse.Log.Warning(AINarratorMod.LogPrefix + "script rejected permanently after "
                               + rounds + " round(s), retries allowed=" + maxRetries + ": " + details);
        }

        /// <summary>编剧槽位空出来了：如果有排队的概念，立刻接上。</summary>
        private static void OnWriterSlotFreed(NarratorState state, AINarratorSettings settings)
        {
            _writerJob = null;
            WriterJob job = _queuedJob;
            _queuedJob = null;
            if (job != null && settings != null)
                BeginWriter(state, settings, job);
        }

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace('\r', ' ').Replace('\n', ' ');
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }
    }
}
