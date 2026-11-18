using System;
using System.Collections.Generic;
using Verse;

namespace Stellaris.AI
{
    /// <summary>
    /// 把动作参数里的 <c>@kind/关键词</c> 解析成真实 defName。
    ///
    /// 位置：EventRuntime.RunStage 里、ActionRegistry.ValidateAll 之前统一做一次。
    /// 这样所有 handler 的 Validate 仍然只看到精确 defName，不需要知道"查询"这回事。
    ///
    /// 默认取搜索结果的第一名，不因为"歧义"失败：后续阶段（choice_selected / game_timer_due）
    /// 没有模型重试，歧义报错会直接终结事件。想精确挑选时用 LOOKUP + 精确名。
    /// </summary>
    public static class DefArgResolver
    {
        private static readonly HashSet<string> DefArgs = new HashSet<string>(StringComparer.Ordinal)
        {
            "thingDef", "stuff", "pawnKind", "hediffDef", "skillDef",
            "weatherDef", "conditionDef", "damageDef", "plantDef",
            "researchDef", "questScriptDef", "factionDef",
            "thoughtDef", "traitDef", "relationDef", "inspirationDef", "mentalStateDef",
            "terrainDef", "roofDef", "filthDef"
        };

        public static bool Resolve(StagePlan plan, ActionContext context, out string error)
        {
            error = null;
            if (plan == null || plan.Actions == null || context == null) return true;

            AINarratorSettings settings = AINarratorMod.Settings;

            for (int i = 0; i < plan.Actions.Count; i++)
            {
                PlannedAction action = plan.Actions[i];
                if (action == null || action.args == null || action.args.Count == 0) continue;

                var keys = new List<string>(action.args.Keys);
                for (int k = 0; k < keys.Count; k++)
                {
                    string key = keys[k];
                    if (!IsDefArg(action, key)) continue;

                    if (!(action.args[key] is string raw) || !raw.StartsWith("@", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!TrySplit(raw, out string kindId, out string query, out string parseError))
                    {
                        error = "参数 " + key + " 的占位符 " + raw + " 格式不对：" + parseError;
                        return false;
                    }

                    DefResolution resolution = DefQueryService.ResolveOne(
                        kindId, query, context.Map, context.State, settings, context.Now);

                    if (!resolution.Ok)
                    {
                        error = "参数 " + key + " 的占位符 " + raw + " 无法解析：" + resolution.Error;
                        return false;
                    }

                    if (settings != null && settings.strictDefPlaceholders && resolution.Total > 1)
                    {
                        error = "参数 " + key + " 的占位符 " + raw + " 匹配到 "
                                + resolution.Total + " 个（strict 模式，未自动选择）。\n"
                                + resolution.Candidates;
                        return false;
                    }

                    action.args[key] = resolution.DefName;

                    if (settings != null && settings.debugLogging)
                    {
                        Verse.Log.Message(AINarratorMod.LogPrefix + "resolve " + raw + " -> "
                                          + resolution.DefName + " (" + resolution.Total + " matches)");
                    }
                }
            }

            return true;
        }

        private static bool IsDefArg(PlannedAction action, string key)
        {
            if (DefArgs.Contains(key)) return true;
            return key == "defName" && string.Equals(action.type, "incident.fire", StringComparison.Ordinal);
        }

        private static bool TrySplit(string raw, out string kindId, out string query, out string error)
        {
            kindId = "";
            query = "";
            error = null;

            int slash = raw.IndexOf('/');
            if (slash <= 1 || slash >= raw.Length - 1)
            {
                error = "应写成 @类别/关键词，例如 @item/药";
                return false;
            }

            kindId = raw.Substring(1, slash - 1).Trim().ToLowerInvariant();
            query = raw.Substring(slash + 1).Trim();
            if (kindId.Length == 0 || query.Length == 0)
            {
                error = "类别和关键词都不能为空";
                return false;
            }

            if (DefKindRegistry.Get(kindId) == null)
            {
                error = "未知类别 " + kindId + "；可用类别：" + DefKindRegistry.ValidIds();
                return false;
            }

            return true;
        }
    }
}
