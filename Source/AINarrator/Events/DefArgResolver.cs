using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Stellaris.AI
{
    /// <summary>
    /// 把动作参数里的 Def 名字统一"对齐"到真实 Def。
    ///
    /// 位置：EventRuntime.RunStage 里、ActionRegistry.ValidateAll 之前统一做一次。
    /// 这样所有 handler 的 Validate 仍然只看到精确 defName，不需要知道"查询"这回事。
    ///
    /// 处理三种情况：
    ///  1. <c>@kind/关键词</c>：走 DefQueryService 搜索，取最优结果；
    ///  2. 精确 defName：原样放过（不碰）；
    ///  3. **像但不是**的 defName（模型编的 "Fracture" / "Infection"）：
    ///     先按 defName / 中文标签做一次"最近的唯一候选"匹配，唯一命中就替换；
    ///     否则直接报一条带候选示例的错误——比让 handler 说"不存在的 HediffDef：X"有用得多。
    ///
    /// 第 3 条是 0.6.2 加的：0.6.1 的实测日志里，模型连续两次写
    /// "Fracture" / "Infection" 这种不存在的 HediffDef，整段脚本被拒，
    /// 而原来的报错没有告诉它"到底有哪些名字可用"。
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
                    if (!(action.args[key] is string raw) || raw.Length == 0) continue;

                    if (raw.StartsWith("@", StringComparison.Ordinal))
                    {
                        if (!ResolvePlaceholder(action, key, raw, context, settings, out error)) return false;
                        continue;
                    }

                    if (!ResolvePlain(action, i, key, raw, out string replacement, out error)) return false;

                    if (!string.Equals(replacement, raw, StringComparison.Ordinal))
                    {
                        action.args[key] = replacement;

                        if (settings != null && settings.debugLogging)
                        {
                            Verse.Log.Message(AINarratorMod.LogPrefix + "def near-miss " + key
                                              + ": \"" + raw + "\" -> " + replacement);
                        }
                    }
                }
            }

            return true;
        }

        private static bool ResolvePlaceholder(PlannedAction action, string key, string raw,
            ActionContext context, AINarratorSettings settings, out string error)
        {
            error = null;

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

            return true;
        }

        /// <summary>普通 defName：先查精确，再找"像但不是"的唯一候选，最后才报错。</summary>
        private static bool ResolvePlain(PlannedAction action, int actionIndex, string key,
            string raw, out string replacement, out string error)
        {
            replacement = raw;
            error = null;

            if (key == "defName" && !string.Equals(action.type, "incident.fire", StringComparison.Ordinal))
            {
                return true;
            }

            if (!ArgFamilies.TryGetValue(key, out DefFamily family)) return true;

            // map.roof 允许写 "none" 表示"拆掉屋顶"，它不是 Def。
            if (key == "roofDef" &&
                (raw.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                 raw.Equals("null", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (family.Find(raw) != null) return true;

            // 高影响类别（原版事件 / 派系 / 任务模板）不猜：猜错会触发一段完全无关的内容。
            // 它们交给 handler 的精确校验去报错。
            if (!family.FailEarly) return true;

            Def near = family.FindNear(raw, out int matches);
            if (near != null)
            {
                replacement = near.defName;
                return true;
            }

            string op = string.IsNullOrEmpty(action.opId) ? "" : "（opId=\"" + action.opId + "\"）";
            error = "第 " + (actionIndex + 1) + " 条动作" + op + " " + action.type + " 的参数 "
                    + key + "=\"" + raw + "\" 不是有效的" + family.Label + "名字"
                    + (matches > 0 ? "（有 " + matches + " 个相似项，但没有唯一匹配）" : "")
                    + "。\n" + family.ExampleHint();
            return false;
        }

        private static bool IsDefArg(PlannedAction action, string key)
        {
            if (DefArgs.Contains(key)) return true;
            return key == "defName" && string.Equals(action.type, "incident.fire", StringComparison.Ordinal);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Def 家族：精确查找 + 近义查找 + 示例
        // ─────────────────────────────────────────────────────────────────────

        private sealed class DefFamily
        {
            public string KindId = "";
            public string Label = "";
            public bool FailEarly = true;
            public Func<string, Def> Find;
            public Func<List<Def>> All;

            /// <summary>
            /// 在 defName / 中文标签里找"最像"的一个。
            /// 只在名字里有连续片段相同（≥3 字符）时才算命中，避免 "a" 这种一字符误配。
            /// 多个命中时取"被包含"的那个，再取更短的名字：
            /// Infection → WoundInfection（而不是 ScariaInfection）。
            /// </summary>
            public Def FindNear(string raw, out int matches)
            {
                matches = 0;
                string norm = DefEntry.NormalizeText(raw);
                if (norm.Length == 0) return null;

                string compact = norm.Replace(" ", "");
                if (compact.Length < 3) return null;

                Def best = null;
                int bestScore = 0;
                int bestLength = int.MaxValue;

                foreach (Def def in All())
                {
                    if (def == null || string.IsNullOrEmpty(def.defName)) continue;

                    string name = DefEntry.NormalizeText(def.defName).Replace(" ", "");
                    string label = DefEntry.NormalizeText(def.label);

                    bool nameHit = name.Length >= 3 &&
                                   (name.Contains(compact) || compact.Contains(name));
                    bool labelHit = label.Length >= 2 &&
                                    (label.Contains(norm) || norm.Contains(label));
                    if (!nameHit && !labelHit) continue;

                    matches++;

                    // 名字"包含"输入 比 "被输入包含" 更像；同等时取短名。
                    int score = nameHit && name.Contains(compact) ? 3 : (labelHit ? 2 : 1);
                    int length = def.defName.Length;
                    if (score > bestScore || (score == bestScore && length < bestLength))
                    {
                        best = def;
                        bestScore = score;
                        bestLength = length;
                    }
                }

                return best;
            }

            public string ExampleHint()
            {
                var defs = new List<Def>(All());
                defs.RemoveAll(d => d == null || string.IsNullOrEmpty(d.defName));
                defs.Sort((a, b) => a.defName.Length.CompareTo(b.defName.Length));

                var parts = new List<string>();
                for (int i = 0; i < defs.Count && parts.Count < 6; i++)
                {
                    Def def = defs[i];
                    string label = string.IsNullOrEmpty(def.label) ? "" : "（" + def.label + "）";
                    parts.Add(def.defName + label);
                }

                return "可用的名字例如：" + string.Join("、", parts.ToArray())
                       + "。\n也可以写 @" + KindId + "/关键词（例如 @" + KindId + "/" + SampleKeyword() + "），"
                       + "让游戏按 defName / 标签 / 描述自动挑一个。";
            }

            private string SampleKeyword()
            {
                foreach (Def def in All())
                {
                    if (def != null && !string.IsNullOrEmpty(def.label)) return def.label;
                }

                return "关键词";
            }
        }

        private static DefFamily Family<T>(string kindId, string label, bool failEarly = true)
            where T : Def
        {
            return new DefFamily
            {
                KindId = kindId,
                Label = label,
                FailEarly = failEarly,
                Find = name => DefDatabase<T>.GetNamedSilentFail(name),
                All = () =>
                {
                    var list = new List<Def>();
                    foreach (T def in DefDatabase<T>.AllDefs)
                    {
                        if (def != null) list.Add(def);
                    }

                    return list;
                }
            };
        }

        private static readonly Dictionary<string, DefFamily> ArgFamilies =
            new Dictionary<string, DefFamily>(StringComparer.Ordinal)
            {
                { "thingDef", Family<ThingDef>("item", "物品/建筑/植物") },
                { "stuff", Family<ThingDef>("item", "材质") },
                { "plantDef", Family<ThingDef>("plant", "植物") },
                { "filthDef", Family<ThingDef>("filth", "污渍") },
                { "pawnKind", Family<PawnKindDef>("pawnKind", "角色/动物") },
                { "hediffDef", Family<HediffDef>("hediff", "负面状态") },
                { "skillDef", Family<SkillDef>("skill", "技能") },
                { "weatherDef", Family<WeatherDef>("weather", "天气") },
                { "conditionDef", Family<GameConditionDef>("condition", "地图状况") },
                { "damageDef", Family<DamageDef>("damage", "伤害类型") },
                { "researchDef", Family<ResearchProjectDef>("research", "研究") },
                { "questScriptDef", Family<QuestScriptDef>("quest", "任务模板", false) },
                { "factionDef", Family<FactionDef>("faction", "派系", false) },
                { "thoughtDef", Family<ThoughtDef>("thought", "想法") },
                { "traitDef", Family<TraitDef>("trait", "特质") },
                { "relationDef", Family<PawnRelationDef>("relation", "关系") },
                { "inspirationDef", Family<InspirationDef>("inspiration", "灵感") },
                { "mentalStateDef", Family<MentalStateDef>("mentalState", "精神状态") },
                { "terrainDef", Family<TerrainDef>("terrain", "地形") },
                { "roofDef", Family<RoofDef>("roof", "屋顶") },
                { "defName", Family<IncidentDef>("incident", "原版事件", false) }
            };

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