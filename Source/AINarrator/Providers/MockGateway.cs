using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Stellaris.AI
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
                    return LlmResult.Failure(LlmErrorKind.QuotaExceeded, "今日 API 调用已达上限");
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
            bool isWriterCall = prompt.Contains("这次要写的剧情");

            switch (Mode)
            {
                case "rest":
                case "no_event":
                    return Done("{\"decision\":\"no_event\",\"reason\":\"mock: 殖民地刚经历过动荡，休整一天。\"}");

                case "garbage":
                    return Done("抱歉，我无法以 JSON 回答这个问题。");

                case "hallucinate":
                    return Done("{\"decision\":\"vanilla_incident\",\"defName\":\"RaidEnemyFromTheFuture\","
                                + "\"points_multiplier\":1.0,\"reason\":\"mock: 故意返回不存在的事件\"}");

                case "story":
                    return Done(isWriterCall ? BuildMockScript(prompt) : BuildStoryDecision());

                case "story_bad_lua":
                    return Done(isWriterCall
                        ? JsonSerializer.Serialize(new Dictionary<string, string>
                        {
                            { "title", "坏脚本" },
                            { "lua", "function run(ctx, state) this is not lua end" }
                        })
                        : BuildStoryDecision());

                case "lookup_story":
                    if (!isWriterCall) return Done(BuildStoryDecision());
                    if (!prompt.Contains("【查询结果")) return Done(LookupItem("药"));
                    return Done(BuildLookupScript(FirstResultDef(prompt)));

                case "lookup_empty":
                    if (!isWriterCall) return Done(BuildStoryDecision());
                    if (!prompt.Contains("【查询结果")) return Done(LookupItem("不存在的物品"));
                    return Done(BuildLookupScript(null));

                case "lookup_loop":
                    return Done(isWriterCall ? LookupItem("药") : BuildStoryDecision());

                case "lookup_bad":
                    if (isWriterCall) return Done("LOOKUP: 这不是 JSON");
                    return Done(BuildStoryDecision());

                case "lookup_incident":
                    if (isWriterCall) return Done(BuildMockScript(prompt));
                    if (!prompt.Contains("【查询结果")) return Done(LookupIncident(""));
                    {
                        string def = FirstResultDef(prompt);
                        return Done(string.IsNullOrEmpty(def)
                            ? "{\"decision\":\"no_event\",\"reason\":\"mock: 查询没有结果\"}"
                            : "{\"decision\":\"vanilla_incident\",\"defName\":\"" + def
                              + "\",\"points_multiplier\":1.0,\"reason\":\"mock: 查询后选择事件\","
                              + "\"announce\":\"mock 事件已安排\"}");
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
                return Done("{\"decision\":\"no_event\",\"reason\":\"mock: prompt 里没有候选表，只能休整。\"}");
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
                          + "\"reason\":\"mock: 选择一个 " + pick.cat + " 事件。\","
                          + "\"announce\":\"mock 事件已安排\"}";

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
            "{\"decision\":\"lua_event\",\"title\":\"山谷里的光\","
            + "\"concept\":\"营地西侧的山谷夜里出现异常光芒，玩家要决定是否派人查看。\","
            + "\"reason\":\"mock: 给一段原创剧情\"}";

        /// <summary>
        /// 生成一段真能跑的两阶段脚本（与调试窗「注入示例事件」共用同一份源码）。
        /// </summary>
        private static string LookupItem(string query) =>
            "LOOKUP: {\"kind\":\"item\",\"q\":\"" + query + "\",\"limit\":5}";

        private static string LookupIncident(string query) =>
            "{\"decision\":\"lookup\",\"lookup\":{\"kind\":\"incident\",\"q\":\"" + query
            + "\",\"limit\":5},\"reason\":\"mock: 先看看有哪些事件\"}";

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
            lua.Append("      args = { title = \"查询测试\", text = \"这是一段用查询结果写出的测试剧情。\" } }\n");
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
                { "title", "查询测试" },
                { "lua", lua.ToString() }
            });
        }


        /// <summary>
        /// 生成一段真能跑的两阶段脚本（与调试窗「注入示例事件」共用同一份源码）。
        /// </summary>
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
                { "lua", SampleEventScript.Build(threatDef, !prompt.Contains("本次必须是短线事件")) }
            });
        }

    }
}
