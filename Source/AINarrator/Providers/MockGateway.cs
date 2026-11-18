using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>
    /// 离线假网关，用来在没有 API Key、不花钱的情况下验收整条决策链路。
    ///
    /// 用法：把「接口地址」填成 `mock:rest` / `mock:raid` / `mock:hallucinate` / `mock:garbage`。
    /// 查询链路的离线模式：`mock:lookup_story` / `mock:lookup_incident` /
    /// `mock:lookup_empty` / `mock:lookup_loop` / `mock:lookup_bad`。
    /// 它从 prompt 里候选表那一段解析出真实存在的 defName，所以产出的提案一定是合法的，
    /// 唯一例外是 mock:hallucinate（故意返回不存在的名字，用来验证白名单能拦住）。
    /// </summary>
    public sealed class MockGateway : ILlmGateway
    {
        private static readonly Regex CandidateLine =
            new Regex(@"^-\s+(?<def>[A-Za-z0-9_]+)\s*\|\s*(?<cat>[A-Za-z0-9_]+)\s*$",
                RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex ResultLine =
            new Regex(@"^(?:★\s*|\d+\.\s*)(?<def>[A-Za-z0-9_]+)\s*\|",
                RegexOptions.Multiline | RegexOptions.Compiled);

        private int _counter;

        public string Mode { get; }

        public string Name => "mock:" + Mode;

        public MockGateway(string mode)
        {
            Mode = (mode ?? "rest").Trim().ToLowerInvariant();
        }

        public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            try
            {
                if (request != null && !await request.TryAuthorizeAttemptAsync(ct).ConfigureAwait(false))
                    return LlmResult.Failure(LlmErrorKind.QuotaExceeded,
                        Loc.Pick("今日 API 调用已达上限", "daily API call limit reached"));
            }
            catch (OperationCanceledException)
            {
                return LlmResult.Failure(LlmErrorKind.Cancelled, "cancelled");
            }

            return await BuildReply(request).ConfigureAwait(false);
        }

        private Task<LlmResult> BuildReply(LlmRequest request)
        {
            string prompt = (request?.UserPrompt ?? "") + "\n" + (request?.SystemPrompt ?? "");

            // 同一个 mock: 模式下要区分"导演"与"编剧"两次调用：看 prompt 里有没有编剧段。
            bool isWriterCall = prompt.Contains("这次要写的剧情")
                                || prompt.Contains("The story to write this time");

            switch (Mode)
            {
                case "rest":
                case "no_event":
                    return Done("{\"decision\":\"no_event\",\"reason\":\""
                                + Loc.Pick("mock: 殖民地刚经历过动荡，休整一天。",
                                    "mock: the colony just went through unrest; take a day to rest.")
                                + "\"}");

                case "garbage":
                    return Done(Loc.Pick("抱歉，我无法以 JSON 回答这个问题。",
                        "Sorry, I cannot answer that as JSON."));

                case "hallucinate":
                    return Done("{\"decision\":\"vanilla_incident\",\"defName\":\"RaidEnemyFromTheFuture\","
                                + "\"points_multiplier\":1.0,\"reason\":\""
                                + Loc.Pick("mock: 故意返回不存在的事件", "mock: deliberately returns a nonexistent incident")
                                + "\"}");

                case "story":
                    return Done(isWriterCall ? BuildMockScript(prompt) : BuildStoryDecision());

                case "story_bad_lua":
                    return Done(isWriterCall
                        ? JsonSerializer.Serialize(new Dictionary<string, string>
                        {
                            { "title", Loc.Pick("坏脚本", "Bad script") },
                            { "lua", "function run(ctx, state) this is not lua end" }
                        })
                        : BuildStoryDecision());

                case "lookup_story":
                    if (!isWriterCall) return Done(BuildStoryDecision());
                    if (!HasLookupResult(prompt)) return Done(LookupItem(Loc.Pick("药", "medicine")));
                    return Done(BuildLookupScript(FirstResultDef(prompt)));

                case "lookup_empty":
                    if (!isWriterCall) return Done(BuildStoryDecision());
                    if (!HasLookupResult(prompt))
                        return Done(LookupItem(Loc.Pick("不存在的物品", "nonexistent item")));
                    return Done(BuildLookupScript(null));

                case "lookup_loop":
                    return Done(isWriterCall ? LookupItem(Loc.Pick("药", "medicine")) : BuildStoryDecision());

                case "lookup_bad":
                    if (isWriterCall)
                        return Done(Loc.Pick("LOOKUP: 这不是 JSON", "LOOKUP: this is not JSON"));
                    return Done(BuildStoryDecision());

                case "lookup_incident":
                    if (isWriterCall) return Done(BuildMockScript(prompt));
                    if (!HasLookupResult(prompt)) return Done(LookupIncident(""));
                    {
                        string def = FirstResultDef(prompt);
                        return Done(string.IsNullOrEmpty(def)
                            ? "{\"decision\":\"no_event\",\"reason\":\""
                              + Loc.Pick("mock: 查询没有结果", "mock: the lookup returned nothing") + "\"}"
                            : "{\"decision\":\"vanilla_incident\",\"defName\":\"" + def
                              + "\",\"points_multiplier\":1.0,\"reason\":\""
                              + Loc.Pick("mock: 查询后选择事件", "mock: chose an incident after the lookup") + "\","
                              + "\"announce\":\""
                              + Loc.Pick("mock 事件已安排", "A mock incident has been scheduled.") + "\"}");
                    }

                case "raid":
                case "mix":
                    break;

                default:
                    break;
            }

            List<(string def, string cat)> candidates = ParseCandidates(prompt);
            if (candidates.Count == 0)
            {
                return Done("{\"decision\":\"no_event\",\"reason\":\""
                            + Loc.Pick("mock: prompt 里没有候选表，只能休整。",
                                "mock: the prompt has no candidate table, so the colony can only rest.")
                            + "\"}");
            }

            int index = Math.Abs(Interlocked.Increment(ref _counter));
            (string def, string cat) pick;

            if (Mode == "mix")
            {
                // 威胁与非威胁交替，用来验证类别轮换与配额是否生效。
                pick = PickByPreference(candidates, index, preferThreat: index % 2 == 0);
            }
            else
            {
                pick = PickByPreference(candidates, index, preferThreat: true);
            }

            string json = "{\"decision\":\"vanilla_incident\","
                          + "\"defName\":\"" + pick.def + "\","
                          + "\"points_multiplier\":1.0,"
                          + "\"reason\":\""
                          + Loc.Pick("mock: 选择一个 " + pick.cat + " 事件。",
                              "mock: chose a " + pick.cat + " incident.")
                          + "\","
                          + "\"announce\":\""
                          + Loc.Pick("mock 事件已安排", "A mock incident has been scheduled.") + "\"}";

            return Done(json);
        }

        private static (string def, string cat) PickByPreference(
            List<(string def, string cat)> candidates, int index, bool preferThreat)
        {
            List<(string def, string cat)> pool = candidates.FindAll(c =>
                IsThreat(c.cat) == preferThreat);

            if (pool.Count == 0) pool = candidates;
            return pool[index % pool.Count];
        }

        private static bool IsThreat(string category) =>
            category != null && category.StartsWith("Threat", StringComparison.OrdinalIgnoreCase);

        private static List<(string def, string cat)> ParseCandidates(string prompt)
        {
            var list = new List<(string, string)>();
            foreach (Match m in CandidateLine.Matches(prompt ?? ""))
            {
                list.Add((m.Groups["def"].Value, m.Groups["cat"].Value));
            }

            return list;
        }

        private static Task<LlmResult> Done(string text) =>
            Task.FromResult(LlmResult.Success(text));

        private static string BuildStoryDecision() =>
            "{\"decision\":\"lua_event\",\"title\":\""
            + Loc.Pick("山谷里的光", "A Light in the Valley")
            + "\","
            + "\"concept\":\""
            + Loc.Pick("营地西侧的山谷夜里出现异常光芒，玩家要决定是否派人查看。",
                "An unnatural light appears at night in the valley west of camp; the player must decide whether to send someone to look.")
            + "\","
            + "\"reason\":\""
            + Loc.Pick("mock: 给一段原创剧情", "mock: provide an original story event")
            + "\"}";

        /// <summary>
        /// 生成一段真能跑的两阶段脚本（与调试窗「注入示例事件」共用同一份源码）。
        /// </summary>
        private static string LookupItem(string query) =>
            "LOOKUP: {\"kind\":\"item\",\"q\":\"" + query + "\",\"limit\":5}";

        private static string LookupIncident(string query) =>
            "{\"decision\":\"lookup\",\"lookup\":{\"kind\":\"incident\",\"q\":\"" + query
            + "\",\"limit\":5},\"reason\":\""
            + Loc.Pick("mock: 先看看有哪些事件", "mock: look at the available incidents first")
            + "\"}";

        private static string FirstResultDef(string prompt)
        {
            if (string.IsNullOrEmpty(prompt)) return null;
            Match match = ResultLine.Match(prompt);
            return match.Success ? match.Groups["def"].Value : null;
        }

        private static string BuildLookupScript(string defName)
        {
            var lua = new StringBuilder();
            lua.Append("function run(ctx, state)\n");
            lua.Append("  local actions = {}\n");
            lua.Append("  if ctx.trigger == \"start\" then\n");
            lua.Append("    actions[#actions+1] = { opId = \"a1\", type = \"story.send_letter\",\n");
            lua.Append("      args = { title = \"" + Loc.Pick("查询测试", "Lookup test")
                       + "\", text = \"" + Loc.Pick("这是一段用查询结果写出的测试剧情。",
                           "This is a test story written from the lookup results.") + "\" } }\n");
            if (!string.IsNullOrEmpty(defName))
            {
                lua.Append("    actions[#actions+1] = { opId = \"a2\", type = \"thing.spawn\",\n");
                lua.Append("      args = { thingDef = \"").Append(defName).Append("\", count = 1 } }\n");
            }

            lua.Append("    return rim.aist.json_encode({ actions = actions, next_state = state, done = true })\n");
            lua.Append("  end\n");
            lua.Append("  return rim.aist.json_encode({ actions = actions, next_state = state })\n");
            lua.Append("end\n");

            return JsonSerializer.Serialize(new Dictionary<string, string>
            {
                { "title", Loc.Pick("查询测试", "Lookup test") },
                { "lua", lua.ToString() }
            });
        }


        /// <summary>
        /// 生成一段真能跑的两阶段脚本（与调试窗「注入示例事件」共用同一份源码）。
        /// </summary>
        /// <summary>查询结果块的中英文标题都可能出现（提示词跟着游戏语言走）。</summary>
        private static bool HasLookupResult(string prompt) =>
            prompt.Contains("【查询结果") || prompt.Contains("[Lookup result");

        /// <summary>短线模式标记。两个语言的 system prompt 各有一句，缺一个就会误判成长线。</summary>
        private static bool IsShortModePrompt(string prompt) =>
            prompt.Contains("【本次是短线事件】") || prompt.Contains("【This is a short story】");

        private static string BuildMockScript(string prompt)
        {
            string threatDef = null;
            foreach (Match m in CandidateLine.Matches(prompt))
            {
                if (m.Groups["cat"].Value.StartsWith("Threat", StringComparison.OrdinalIgnoreCase))
                {
                    threatDef = m.Groups["def"].Value;
                    break;
                }
            }

            return JsonSerializer.Serialize(new Dictionary<string, string>
            {
                { "title", SampleEventScript.Title },
                { "lua", SampleEventScript.Build(threatDef, !IsShortModePrompt(prompt)) }
            });
        }

    }
}
