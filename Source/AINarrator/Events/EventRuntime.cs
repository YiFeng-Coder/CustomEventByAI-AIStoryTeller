using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Stellaris.AI
{
    /// <summary>
    /// 事件运行时：驱动"定义 → 实例 → 阶段执行 → 动作落地 → 定时器/信号"这条链。
    ///
    /// 不变量：
    ///  * 每阶段独立执行，状态保存在可序列化的 EventInstanceRecord 里，绝不持有 Lua 协程；
    ///  * 全量校验通过才执行，任何一条非法 → 整个阶段作废，不留半截副作用；
    ///  * 同一 instanceId + opId 只执行一次；
    ///  * 所有调用都在主线程。
    /// </summary>
    public static class EventRuntime
    {
        public const int MaxActiveInstances = 6;
        public const int MaxDefinitions = 16;
        public const int InstanceTtlTicks = 60000 * 5;

        public static string LastStatus { get; private set; } = "(还没有任何剧情事件)";

        /// <summary>最近一次阶段到底产出了什么（调试窗用）。</summary>
        public static string LastPlanSummary { get; private set; } = "(尚未执行任何阶段)";

        /// <summary>最近一次执行过的脚本片段（调试窗用，截断）。</summary>
        public static string LastScriptPreview { get; private set; } = "";

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace('\r', ' ').Replace('\n', ' ');
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        /// <summary>这一阶段是否要等玩家做出选择（有 show_choices 动作）。</summary>
        private static bool WaitsForChoice(IEnumerable<PlannedAction> actions)
        {
            foreach (PlannedAction action in actions)
            {
                if (action?.type == "story.show_choices" || action?.type == "story.choice_letter") return true;
            }

            return false;
        }

        public static List<EventInstanceRecord> ActiveInstances =>
            NarratorWorldComponent.Instance != null
                ? NarratorWorldComponent.Instance.State.instances
                : new List<EventInstanceRecord>();

        /// <summary>校验脚本并准备事件定义。成功启动的定义才写入存档。</summary>
        public static bool RegisterDefinition(string title, string script,
            out EventDefinitionRecord definition, out string error)
        {
            definition = null;
            error = null;

            NarratorWorldComponent comp = NarratorWorldComponent.Instance;
            if (comp == null) { error = "世界状态未就绪"; return false; }

            // 先做静态体检：只查确定性错误，避免误杀。
            bool allowLong = AINarratorMod.Settings != null && AINarratorMod.Settings.enableLongEvents;
            System.Collections.Generic.List<string> issues =
                LuaLint.Check(script, ActionRegistry.All.Keys, allowLong);

            if (issues.Count > 0)
            {
                error = "脚本体检未通过：" + string.Join("；", issues.ToArray());
                return false;
            }

            if (!LuaStageRunner.Validate(script, out error)) return false;

            NarratorState state = comp.State;
            string hash = LuaStageRunner.HashOf(script);

            for (int i = 0; i < state.defs.Count; i++)
            {
                if (state.defs[i]?.hash == hash)
                {
                    definition = state.defs[i];
                    return true;
                }
            }

            definition = new EventDefinitionRecord
            {
                id = "evtdef_" + (++state.eventSeq),
                title = string.IsNullOrEmpty(title) ? "未命名剧情" : title,
                script = script,
                hash = hash,
                createdTick = Find.TickManager.TicksGame
            };

            return true;
        }

        private static void RememberDefinition(NarratorState state, EventDefinitionRecord definition)
        {
            if (FindDefinition(state, definition.id) == null) state.defs.Add(definition);
            while (state.defs.Count > MaxDefinitions)
            {
                int unused = state.defs.FindIndex(d => d == null ||
                    !state.instances.Exists(i => i != null && !i.done && i.defId == d.id));
                if (unused < 0) break;
                state.defs.RemoveAt(unused);
            }
        }

        /// <summary>
        /// 正式开场前预演 start、每个首批选项，以及第一个可能到期的定时阶段。
        ///
        /// 为什么需要：静态体检只查语法与契约错误，`attempt to get length of a number value`
        /// 这类运行时错误只有当那个分支真被执行时才暴露。start 分支失败还能回喂给模型重写，
        /// 但 choice_selected / timer 分支要等玩家点了才跑——那时事件已经开场，只能当场崩给玩家看。
        /// 空跑只执行 Lua、解析计划，动作一律不落地，所以可以安全地提前跑。
        ///
        /// 返回 null 表示全部分支通过；否则返回第一个失败的描述。
        /// </summary>
        public static string PreviewBranches(EventDefinitionRecord definition)
        {
            if (definition == null) return "事件定义为空";

            string error = PreviewStage(definition, "start", null, "{}", new List<object>(),
                null, "", false, out StagePlan startPlan, out Dictionary<string, string> startHandles);
            if (error != null) return "start 分支空跑失败：" + error;
            if (startPlan.Done) return null;

            List<object> receipts = PlannedReceipts(startPlan);

            foreach (string choice in ChoiceIds(startPlan))
            {
                var payload = new Dictionary<string, object>
                {
                    { "choice", choice },
                    { "instance_id", "preview" }
                };

                error = PreviewStage(definition, "choice_selected", payload, startPlan.NextStateJson,
                    receipts, startHandles, startPlan.Stage, startPlan.Timers.Count > 0, out _, out _);
                if (error != null)
                {
                    return "choice_selected 分支（choice=\"" + choice + "\"）空跑失败：" + error;
                }
            }

            // 后面的定时阶段会读取前一个阶段更新后的 state，不能拿开场 state 直接预演。
            TimerPlan firstTimer = null;
            foreach (TimerPlan timer in startPlan.Timers)
            {
                if (firstTimer == null || timer.delayTicks < firstTimer.delayTicks) firstTimer = timer;
            }

            if (firstTimer != null)
            {
                bool canContinueAfterTimer = startPlan.Timers.Count > 1 || WaitsForChoice(startPlan.Actions);
                error = PreviewStage(definition, firstTimer.signal, null, startPlan.NextStateJson,
                    receipts, startHandles, startPlan.Stage, canContinueAfterTimer, out _, out _);
                if (error != null)
                {
                    return "定时分支（signal=\"" + firstTimer.signal + "\"）空跑失败：" + error;
                }
            }

            return null;
        }

        /// <summary>
        /// 空跑一个阶段：执行 Lua、解析计划、把动作参数按真实校验器过一遍；动作绝不执行。
        /// </summary>
        private static string PreviewStage(EventDefinitionRecord definition, string trigger,
            Dictionary<string, object> payload, string stateJson, List<object> receipts,
            Dictionary<string, string> priorHandles, string stageName, bool canContinue, out StagePlan plan,
            out Dictionary<string, string> validatedHandles)
        {
            plan = null;
            validatedHandles = null;

            Map map = Find.AnyPlayerHomeMap;
            if (map == null) return "没有玩家基地地图";

            var context = new ActionContext
            {
                Map = map,
                State = NarratorWorldComponent.Instance != null ? NarratorWorldComponent.Instance.State : null,
                Definition = definition,

                // 用一个假的实例跑校验：ActionBudget 以"还没花过预算"为起点检查（真实 start 也是这样），
                // ctx.stage 也能和真实阶段对上。这个实例不进 state，也不会被存档。
                Instance = new EventInstanceRecord
                {
                    id = "preview",
                    defId = definition.id,
                    stageName = stageName ?? ""
                },
                Now = Find.TickManager.TicksGame
            };

            StageOutcome outcome;
            try
            {
                outcome = LuaStageRunner.Run(
                    definition.script, BuildCtx(context, trigger, payload, receipts), stateJson);
            }
            catch (Exception e)
            {
                // 空跑本身不许把主循环带崩：任何意外都折成"这条脚本不能用"。
                return "空跑异常：" + e.GetType().Name + ": " + e.Message;
            }

            if (!outcome.Ok) return outcome.Error;

            plan = outcome.Plan;
            Rand.PushState();
            try
            {
                return ValidatePreviewPlan(plan, context, priorHandles, canContinue, out validatedHandles);
            }
            catch (Exception e)
            {
                return "空跑校验异常：" + e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                Rand.PopState();
            }
        }

        /// <summary>
        /// 用真实校验器检查预演计划，并带入前置阶段通过全量校验的句柄声明。
        /// </summary>
        private static string ValidatePreviewPlan(StagePlan plan, ActionContext context,
            Dictionary<string, string> priorHandles, bool canContinue, out Dictionary<string, string> validatedHandles)
        {
            validatedHandles = null;
            if (plan == null) return null;

            // 与 RunStage 同样的"空阶段 / 悬空"判定：这两类错误现在也会在开演前被拦下。
            if (plan.Actions.Count == 0 && plan.Timers.Count == 0)
            {
                return "该阶段没有任何动作或定时器"
                       + (plan.Done ? "（且声明了 done，直接结束）" : "")
                       + "；通常是 ctx.trigger 判断没匹配上，或 actions 没写成数组";
            }

            if (!plan.Done && !canContinue && plan.Timers.Count == 0 && !WaitsForChoice(plan.Actions))
            {
                return "该阶段既不结束也不等待：没有 done、没有 timer、也没有让玩家选择";
            }

            if (plan.Actions.Count == 0)
            {
                validatedHandles = priorHandles;
                return null;
            }

            if (!DefArgResolver.Resolve(plan, context, out string resolveError)) return resolveError;

            return ActionRegistry.ValidateAll(plan.Actions, context, priorHandles, out validatedHandles,
                out string invalidError) ? null : invalidError;
        }

        /// <summary>从计划里收集玩家可能点到的选项 id（show_choices / choice_letter）。</summary>
        private static List<string> ChoiceIds(StagePlan plan)
        {
            var ids = new List<string>();
            if (plan == null) return ids;

            foreach (PlannedAction action in plan.Actions)
            {
                if (action == null) continue;
                if (action.type != "story.show_choices" && action.type != "story.choice_letter") continue;

                List<object> options = action.ArgList("options");
                if (options == null) continue;

                foreach (object item in options)
                {
                    Dictionary<string, object> dict = PlannedAction.AsDict(item);
                    if (dict == null) continue;
                    if (!dict.TryGetValue("id", out object id)) continue;

                    string text = id as string;
                    if (!string.IsNullOrEmpty(text)) ids.Add(text);
                }
            }

            return ids;
        }

        /// <summary>伪造 start 阶段的回执（全部成功），让后续分支的 ctx.last 有东西可读。</summary>
        private static List<object> PlannedReceipts(StagePlan plan)
        {
            var receipts = new List<object>();
            if (plan == null) return receipts;

            foreach (PlannedAction action in plan.Actions)
            {
                if (action == null) continue;

                receipts.Add(new Dictionary<string, object>
                {
                    { "type", action.type ?? "" },
                    { "op_id", action.opId ?? "" },
                    { "ok", true },
                    { "error", "" }
                });
            }

            return receipts;
        }

        /// <summary>新建一个实例并立即执行首阶段。</summary>
        public static bool Start(EventDefinitionRecord definition, string trigger,
            Dictionary<string, object> payload, out string error) =>
            Start(definition, trigger, payload, out error, out _, forced: true);

        public static bool Start(EventDefinitionRecord definition, string trigger,
            Dictionary<string, object> payload, out string error, out bool executionStarted, bool forced = false)
        {
            error = null;
            executionStarted = false;

            NarratorWorldComponent comp = NarratorWorldComponent.Instance;
            if (comp == null) { error = "世界状态未就绪"; return false; }
            if (definition == null) { error = "事件定义为空"; return false; }

            NarratorState state = comp.State;

            // 同时活跃的剧情数上限，避免 AI 一口气开十条线。
            int active = 0;
            for (int i = 0; i < state.instances.Count; i++)
            {
                if (state.instances[i] is { done: false }) active++;
            }

            if (active >= MaxActiveInstances)
            {
                error = "同时活跃的剧情已达上限（" + MaxActiveInstances + "）";
                return false;
            }

            int now = Find.TickManager.TicksGame;
            var instance = new EventInstanceRecord
            {
                id = "evt_" + (++state.eventSeq),
                defId = definition.id,
                createdTick = now,
                lastTick = now,
                forced = forced
            };

            state.instances.Add(instance);
            if (!RunStage(state, instance, definition, string.IsNullOrEmpty(trigger) ? "start" : trigger,
                    payload, out error, out executionStarted))
            {
                if (!executionStarted) state.instances.Remove(instance);
                return false;
            }

            RememberDefinition(state, definition);
            return true;
        }

        /// <summary>每 60 tick 由 NarratorWorldComponent 调用：推进到点的定时器、回收超时实例。</summary>
        public static void Tick()
        {
            NarratorWorldComponent comp = NarratorWorldComponent.Instance;
            if (comp == null) return;

            NarratorState state = comp.State;
            if (state == null) return;

            if (Find.AnyPlayerHomeMap == null) return;
            int now = Find.TickManager.TicksGame;
            if (now % 60 != 0) return;

            // 叙事目标的每日提醒与到期：即使当前没有任何活跃事件也要跑。
            ObjectiveTracker.Tick(state, now);

            bool activeStoryteller = DirectorLoop.IsActiveStoryteller;

            for (int i = state.instances.Count - 1; i >= 0; i--)
            {
                EventInstanceRecord instance = state.instances[i];

                if (instance == null || instance.done)
                {
                    state.instances.RemoveAt(i);
                    continue;
                }

                if (!activeStoryteller && !instance.forced) continue;

                // 在 60 tick 轮询带来的误差内，仍允许正好等满 5 天的定时器到期。
                if (now - instance.lastTick > InstanceTtlTicks + 59)
                {
                    instance.done = true;
                    instance.lastError = "超过 5 游戏日未推进，自动回收";
                    state.instances.RemoveAt(i);
                    continue;
                }

                EventTimerRecord timer = TakeDueTimer(instance, now);
                if (timer != null)
                {
                    EventDefinitionRecord def = FindDefinition(state, instance.defId);
                    if (def == null)
                    {
                        FailStage(instance, "事件定义丢失", out _);
                    }
                    else if (!RunStage(state, instance, def, timer.signal, null, out string error, out _))
                    {
                        Verse.Log.Warning(AINarratorMod.LogPrefix + "stage failed (" + instance.id + "): " + error);
                    }
                }
            }
        }

        private static EventTimerRecord TakeDueTimer(EventInstanceRecord instance, int now)
        {
            int earliest = -1;
            for (int i = 0; i < instance.timers.Count; i++)
            {
                EventTimerRecord timer = instance.timers[i];
                if (timer == null || timer.atTick > now) continue;
                if (earliest < 0 || timer.atTick < instance.timers[earliest].atTick) earliest = i;
            }
            if (earliest < 0) return null;

            EventTimerRecord due = instance.timers[earliest];
            instance.timers.RemoveAt(earliest);
            return due;
        }

        /// <summary>向指定实例发信号（玩家选择、Pawn 死亡等）。</summary>
        public static void FireSignalTo(string instanceId, string signal, Dictionary<string, object> payload)
        {
            NarratorWorldComponent comp = NarratorWorldComponent.Instance;
            if (comp == null || string.IsNullOrEmpty(instanceId)) return;

            NarratorState state = comp.State;
            EventInstanceRecord instance = null;
            for (int i = 0; i < state.instances.Count; i++)
            {
                if (state.instances[i]?.id == instanceId)
                {
                    instance = state.instances[i];
                    break;
                }
            }

            if (instance == null || instance.done) return;

            EventDefinitionRecord def = FindDefinition(state, instance.defId);
            if (def == null) return;

            RunStage(state, instance, def, signal, payload, out string error, out _);
            if (error != null)
            {
                Verse.Log.Warning(AINarratorMod.LogPrefix + "signal stage failed (" + instanceId + "): " + error);
            }
        }

        private static bool RunStage(NarratorState state, EventInstanceRecord instance,
            EventDefinitionRecord definition, string trigger, Dictionary<string, object> payload,
            out string error, out bool executionStarted)
        {
            error = null;
            executionStarted = false;

            Map map = Find.AnyPlayerHomeMap;
            if (map == null)
            {
                return FailStage(instance, "没有玩家基地地图", out error);
            }

            int now = Find.TickManager.TicksGame;
            var context = new ActionContext
            {
                Map = map,
                State = state,
                Definition = definition,
                Instance = instance,
                Now = now
            };

            // 上一阶段的回执：脚本用 ctx.last 判断"我刚才那一步成没成"。
            List<object> last = BuildReceipts(instance);

            StageOutcome outcome = LuaStageRunner.Run(
                definition.script, BuildCtx(context, trigger, payload, last), instance.stateJson);
            LastScriptPreview = Trim(definition.script, 800);

            if (!outcome.Ok)
            {
                // 运行时错误没有行号，脚本本身也不落盘：把现场（触发名、状态、完整源码）写全，
                // 否则事后只能靠猜。只在真失败时输出，不会污染正常日志。
                string source = definition.script ?? "";
                if (source.Length > 8000) source = source.Substring(0, 8000) + "\n...[脚本过长，已截断]";

                Verse.Log.Error(AINarratorMod.LogPrefix + "stage runtime error (" + instance.id
                                + " def=" + definition.id + " trigger=" + trigger + "): " + outcome.Error
                                + "\n  state=" + Trim(instance.stateJson, 400)
                                + "\n  script:\n" + source);
                return FailStage(instance, outcome.Error, out error);
            }

            StagePlan plan = outcome.Plan;

            LastPlanSummary = "[" + trigger + "] 动作 " + plan.Actions.Count
                              + " · 定时器 " + plan.Timers.Count
                              + " · done=" + plan.Done
                              + " · stage=" + (plan.Stage ?? "")
                              + " · next_state " + Trim(plan.NextStateJson, 120);

            // 空阶段：没有任何动作、也没有定时器。
            // 这几乎总是脚本里的 ctx.trigger 判断与我们的触发名对不上（模型常见手误），
            // 或者它把 done = true 直接写在开头、什么都没做就结束。
            // 以前这种情况被当成"成功但什么都没做"，玩家看到的就是"事件开了，毫无效果"。
            if (plan.Actions.Count == 0 && plan.Timers.Count == 0)
            {
                string why = "trigger=\"" + trigger + "\" 阶段是空的：没有动作也没有定时器"
                             + (plan.Done ? "（且声明了 done，直接结束）" : "")
                             + "。通常是 ctx.trigger 判断与触发名不符（只认 start / game_timer_due / choice_selected），"
                             + "或 actions 没写成数组（用 actions[#actions+1] 追加）。";
                Verse.Log.Warning(AINarratorMod.LogPrefix + "empty stage for " + instance.id + ": " + why);
                return FailStage(instance, why, out error);
            }

            if (plan.Actions.Count > 32)
            {
                return FailStage(instance, "单阶段动作数超过 32 条上限", out error);
            }

            string scope = string.IsNullOrEmpty(instance.stageName) ? trigger : instance.stageName;
            var plannedIds = new HashSet<string>(StringComparer.Ordinal);
            plan.Actions.RemoveAll(action => action == null ||
                (!string.IsNullOrEmpty(action.opId) &&
                    (instance.HasOp(scope, action.opId) || !plannedIds.Add(action.opId))));

            bool awaitingChoice = (instance.awaitingChoice && trigger != "choice_selected") || WaitsForChoice(plan.Actions);
            if (!plan.Done && plan.Timers.Count == 0 && instance.timers.Count == 0 && !awaitingChoice)
            {
                string why = "trigger=\"" + trigger + "\" 阶段既不结束，也没有等待定时器或玩家选择。";
                Verse.Log.Warning(AINarratorMod.LogPrefix + "dangling stage for " + instance.id);
                return FailStage(instance, why, out error);
            }

            // 玩家关掉长线事件时，直接拒绝带 timer 的脚本（prompt 里已经要求，这里是硬兜底）。
            AINarratorSettings settings = AINarratorMod.Settings;
            if (settings != null && !settings.enableLongEvents && plan.Timers.Count > 0)
            {
                string why = "玩家已关闭长线事件，但脚本注册了 timer。请改成在 start 阶段内完成的短线事件。";
                Verse.Log.Warning(AINarratorMod.LogPrefix + "long event rejected (disabled by player): " + instance.id);
                return FailStage(instance, why, out error);
            }

            // 把 @kind/关键词 解析成真实 defName。必须在 ValidateAll 之前，
            // 这样所有 handler 仍然只看到精确名字，查询能力不会变成权限后门。
            if (!DefArgResolver.Resolve(plan, context, out string defError))
            {
                Verse.Log.Warning(AINarratorMod.LogPrefix + "def resolve failed for " + instance.id + ": " + defError);
                return FailStage(instance, defError, out error);
            }

            // 全量校验：一条非法就整阶段作废，绝不执行一半。
            if (!ActionRegistry.ValidateAll(plan.Actions, context, out string invalid))
            {
                return FailStage(instance, "trigger=\"" + trigger + "\"：" + invalid, out error);
            }

            return ApplyStage(plan, context, scope, trigger, awaitingChoice, out error, out executionStarted);
        }

        private static bool ApplyStage(StagePlan plan, ActionContext context, string scope, string trigger,
            bool awaitingChoice, out string error, out bool executionStarted)
        {
            error = null;
            executionStarted = false;
            EventInstanceRecord instance = context.Instance;
            instance.lastActions.Clear();
            int executed = 0;
            foreach (PlannedAction action in plan.Actions)
            {
                if (!ActionRegistry.TryGet(action.type, out IGameActionHandler handler))
                {
                    return FailStage(instance, "未注册的动作类型：" + action.type, out error);
                }

                executionStarted = true;
                try
                {
                    if (!handler.TryExecute(action, context, out string actionError))
                    {
                        string reason = action.type + " 执行失败：" + actionError;
                        AddReceipt(instance, action, false, actionError);
                        return FailStage(instance, reason, out error);
                    }
                    instance.MarkOp(scope, action.opId);
                    executed++;
                    AddReceipt(instance, action, true, "");
                }
                catch (Exception e)
                {
                    AddReceipt(instance, action, false, e.Message);
                    Verse.Log.Error(AINarratorMod.LogPrefix + "action " + action.type + " failed: " + e);
                    return FailStage(instance, action.type + " 执行异常：" + e.Message, out error);
                }
            }

            CommitStage(instance, plan, context.Now, awaitingChoice);

            LastStatus = (!string.IsNullOrEmpty(context.Definition.title) ? context.Definition.title : instance.id)
                         + " · 阶段 " + (instance.stageName.Length > 0 ? instance.stageName : "?")
                         + " · 执行 " + executed + " 个动作"
                         + (instance.done ? " · 已结束" : "");

            if (AINarratorMod.Settings != null && AINarratorMod.Settings.debugLogging)
            {
                Verse.Log.Message(AINarratorMod.LogPrefix + "stage [" + trigger + "] " + LastStatus);
            }

            return true;
        }

        private static void CommitStage(EventInstanceRecord instance, StagePlan plan, int now, bool awaitingChoice)
        {
            instance.stateJson = plan.NextStateJson;
            if (!string.IsNullOrEmpty(plan.Stage)) instance.stageName = plan.Stage;
            instance.done = plan.Done;
            instance.lastTick = now;
            instance.awaitingChoice = !plan.Done && awaitingChoice;

            if (plan.Done)
            {
                instance.timers.Clear();
                return;
            }

            foreach (TimerPlan timer in plan.Timers)
            {
                instance.timers.Add(new EventTimerRecord
                {
                    atTick = now + timer.delayTicks,
                    signal = timer.signal
                });
            }
        }

        private static bool FailStage(EventInstanceRecord instance, string reason, out string error)
        {
            error = reason;
            instance.lastError = reason;
            instance.done = true;
            instance.awaitingChoice = false;
            instance.timers.Clear();
            LastStatus = "阶段失败：" + reason;
            return false;
        }

        private static Dictionary<string, object> BuildCtx(ActionContext context, string trigger,
            Dictionary<string, object> payload, List<object> last)
        {
            Map map = context.Map;

            var ctx = new Dictionary<string, object>
            {
                { "trigger", trigger ?? "" },
                { "payload", payload ?? new Dictionary<string, object>() },
                { "instance_id", context.Instance != null ? context.Instance.id : "" },
                { "event_title", context.Definition != null ? context.Definition.title : "" },
                { "day", context.Now / 60000 },
                { "colonists", map != null && map.mapPawns != null ? map.mapPawns.FreeColonistsSpawnedCount : 0 },
                { "last", last ?? new List<object>() }
            };

            if (map != null)
            {
                // 地图尺寸与中心：让模型知道"地图有多大"，这是坐标类动作不再瞎猜的前提。
                ctx["map_w"] = map.Size.x;
                ctx["map_h"] = map.Size.z;
                ctx["center"] = new Dictionary<string, object>
                {
                    { "x", map.Center.x },
                    { "y", 0 },
                    { "z", map.Center.z }
                };
            }

            if (context.Instance != null)
            {
                ctx["stage"] = context.Instance.stageName ?? "";
            }

            return ctx;
        }

        /// <summary>把上一阶段的回执翻成 Lua 能直接读的表（最多 8 条）。</summary>
        private static List<object> BuildReceipts(EventInstanceRecord instance)
        {
            var list = new List<object>();
            if (instance == null || instance.lastActions == null) return list;

            for (int i = 0; i < instance.lastActions.Count; i++)
            {
                ActionOutcomeRecord record = instance.lastActions[i];
                if (record == null) continue;

                list.Add(new Dictionary<string, object>
                {
                    { "type", record.type ?? "" },
                    { "op_id", record.opId ?? "" },
                    { "ok", record.ok },
                    { "error", record.ok ? "" : (record.error ?? "") }
                });
            }

            return list;
        }

        private static void AddReceipt(EventInstanceRecord instance, PlannedAction action, bool ok, string error)
        {
            if (instance == null) return;

            instance.lastActions.Add(new ActionOutcomeRecord
            {
                type = action.type ?? "",
                opId = action.opId ?? "",
                ok = ok,
                error = error ?? ""
            });

            while (instance.lastActions.Count > 8) instance.lastActions.RemoveAt(0);
        }

        private static EventDefinitionRecord FindDefinition(NarratorState state, string defId)
        {
            if (state == null || string.IsNullOrEmpty(defId)) return null;
            for (int i = 0; i < state.defs.Count; i++)
            {
                if (state.defs[i]?.id == defId) return state.defs[i];
            }

            return null;
        }
    }
}
