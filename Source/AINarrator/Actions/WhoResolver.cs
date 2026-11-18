using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Stellaris.AI
{
    /// <summary>
    /// 目标选择器。
    ///
    /// 以前角色类动作只认 handle（本事件自己生成的角色），"你的射手受伤了"这类
    /// 最有代入感的剧情根本写不出来。现在多了一种写法：
    ///   who = "handle:p1"        本事件生成的角色（老写法，兼容）
    ///   who = "colonist:米娅"     你的殖民者（按名字）
    ///   who = "best:Shooting"    最擅长某技能的殖民者
    ///   who = "worst:Mood"       心情最差的殖民者
    ///
    /// 能不能落到玩家殖民者身上，由 <see cref="TargetPolicy"/> 决定，并且写在校验里
    /// （提示词只是建议，这里才是墙）。
    /// </summary>
    public static class WhoResolver
    {
        public const string Help = "handle:p1 / colonist:名字 / best:技能名 / worst:Mood";

        private const string HelpEn = "handle:p1 / colonist:name / best:skillDefName / worst:Mood";

        public static bool TryResolve(PlannedAction action, ActionContext context, TargetPolicy policy,
            bool socialCategory, out Pawn pawn, out string error)
        {
            pawn = null;
            error = null;

            string who = action.ArgString("who").Trim();
            if (who.Length == 0) who = action.ArgString("handle").Trim();
            if (who.Length == 0)
            {
                error = Loc.Pick("缺少 who（写法：" + Help + "）", "Missing who (use: " + HelpEn + ")");
                return false;
            }

            return TryResolveToken(context, who, policy, socialCategory, out pawn, out error);
        }

        /// <summary>按单个选择器字符串解析（"other" 这类第二目标复用同一条路径）。</summary>
        public static bool TryResolveToken(ActionContext context, string who, TargetPolicy policy,
            bool socialCategory, out Pawn pawn, out string error)
        {
            pawn = null;
            error = null;

            if (string.IsNullOrEmpty(who))
            {
                error = Loc.Pick("缺少目标", "Missing target");
                return false;
            }

            int colon = who.IndexOf(':');
            string kind = colon > 0 ? who.Substring(0, colon).Trim().ToLowerInvariant() : "";
            string value = colon > 0 ? who.Substring(colon + 1).Trim() : who;

            switch (kind)
            {
                case "handle":
                    if (!TryHandle(context, value, out pawn, out error)) return false;
                    break;

                case "colonist":
                case "pawn":
                case "colonists":
                    if (!TryByName(context, value, out pawn, out error)) return false;
                    break;

                case "best":
                case "worst":
                    if (!TryExtreme(context, kind == "best", value, out pawn, out error)) return false;
                    break;

                case "":
                    // 裸名字：先当句柄，再当殖民者名字。
                    if (TryHandle(context, who, out pawn, out _)) break;
                    if (TryByName(context, who, out pawn, out error)) break;
                    error = Loc.Pick("找不到 \"" + who + "\"：既不是句柄，也不是你的殖民者名字。写法：" + Help,
                        "Cannot find \"" + who + "\": it is neither a handle nor the name of one of your "
                        + "colonists. Use: " + HelpEn);
                    return false;

                default:
                    error = Loc.Pick("未知的 who 前缀 \"" + kind + "\"；写法：" + Help,
                        "Unknown who prefix \"" + kind + "\"; use: " + HelpEn);
                    return false;
            }

            // 校验已通过的生成动作后，可以先确认句柄的类型，实际生成后再检查角色状态和派系。
            if (pawn == null) return true;
            if (pawn.Dead) { error = Loc.Pick("目标已经死亡", "The target is already dead"); return false; }
            if (!pawn.Spawned)
            {
                error = Loc.Pick("目标不在任何地图上", "The target is not on any map");
                return false;
            }

            if (!CheckPolicy(pawn, policy, socialCategory, out error)) return false;
            return true;
        }

        private static bool CheckPolicy(Pawn pawn, TargetPolicy policy, bool socialCategory, out string error)
        {
            error = null;

            bool isPlayerPawn = pawn.IsColonist || pawn.Faction == Faction.OfPlayer;
            if (!isPlayerPawn) return true;

            switch (policy)
            {
                case TargetPolicy.Dangerous:
                    return RequireInfluence(AINarratorSettings.InfluenceEverything,
                        "伤害 / 击杀 / 精神崩溃", "harm / kill / mental break", out error);

                case TargetPolicy.NonPlayerOnly:
                    return RequireInfluence(AINarratorSettings.InfluencePsychology,
                        "特质 / 关系 / 技能 / 派系", "trait / relation / skill / faction", out error);

                case TargetPolicy.Neutral:
                    if (!socialCategory) return true;
                    return RequireInfluence(AINarratorSettings.InfluencePsychology,
                        "想法 / 心情", "thought / mood", out error);

                default:
                    return true;
            }
        }

        /// <summary>
        /// 玩家在设置里给"AI 对我的殖民者的影响"划的线。
        /// 这里是硬校验：设置不够就报一条能照着改的错误，而不是静默跳过。
        /// </summary>
        private static bool RequireInfluence(int needed, string whatZh, string whatEn, out string error)
        {
            error = null;

            int current = AINarratorMod.Settings != null
                ? AINarratorMod.Settings.colonistInfluence
                : AINarratorSettings.InfluenceEverything;

            if (current >= needed) return true;

            error = Loc.Pick(
                "玩家把「AI 对你的殖民者的影响」设成了「"
                + AINarratorSettings.InfluenceLabel(current)
                + "」，所以不能用「" + whatZh + "」类动作指向你的殖民者。"
                + "允许的话，请在 设置 → 动作库 里改成「"
                + AINarratorSettings.InfluenceLabel(needed) + "」或更高。",
                "The player set \"AI influence on your colonists\" to \""
                + AINarratorSettings.InfluenceLabel(current)
                + "\", so " + whatEn + " actions cannot target your colonists. "
                + "If that should be allowed, change it to \""
                + AINarratorSettings.InfluenceLabel(needed) + "\" or higher in Settings → Action library.");
            return false;
        }

        private static bool TryHandle(ActionContext context, string name, out Pawn pawn, out string error)
        {
            pawn = null;
            if (!HandleRegistry.TryResolveReference(context, name, true, out Thing thing, out error)) return false;
            pawn = thing as Pawn;
            return true;
        }

        private static bool TryByName(ActionContext context, string name, out Pawn pawn, out string error)
        {
            pawn = null;
            error = null;

            List<Pawn> colonists = Colonists(context);
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn candidate = colonists[i];
                if (candidate == null) continue;
                if (MatchesName(candidate, name))
                {
                    pawn = candidate;
                    return true;
                }
            }

            var names = new List<string>();
            for (int i = 0; i < colonists.Count && names.Count < 8; i++)
            {
                if (colonists[i] != null) names.Add(colonists[i].LabelShort);
            }

            error = Loc.Pick("没有叫 \"" + name + "\" 的殖民者；当前是：" + string.Join("、", names.ToArray()),
                "No colonist named \"" + name + "\"; current colonists: " + string.Join(", ", names.ToArray()));
            return false;
        }

        private static bool TryExtreme(ActionContext context, bool best, string what, out Pawn pawn, out string error)
        {
            pawn = null;
            error = null;

            List<Pawn> colonists = Colonists(context);
            if (colonists.Count == 0)
            {
                error = Loc.Pick("地图上没有殖民者", "There are no colonists on this map");
                return false;
            }

            bool byMood = what.Equals("mood", StringComparison.OrdinalIgnoreCase);
            SkillDef skill = byMood ? null : FindSkill(what);
            if (!byMood && skill == null)
            {
                error = Loc.Pick("best/worst 后面只能是 Mood 或某个技能 DefName（例如 Shooting）",
                    "best/worst only accept Mood or a skill DefName (for example Shooting)");
                return false;
            }

            Pawn picked = null;
            float pickedValue = 0f;

            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn candidate = colonists[i];
                if (candidate == null || candidate.Dead) continue;

                float value;
                if (byMood)
                {
                    if (candidate.needs == null || candidate.needs.mood == null) continue;
                    value = candidate.needs.mood.CurLevel;
                }
                else
                {
                    if (candidate.skills == null) continue;
                    SkillRecord record = candidate.skills.GetSkill(skill);
                    if (record == null || record.TotallyDisabled) continue;
                    value = record.Level;
                }

                if (picked == null || (best ? value > pickedValue : value < pickedValue))
                {
                    picked = candidate;
                    pickedValue = value;
                }
            }

            if (picked == null)
            {
                error = Loc.Pick("找不到可比较的殖民者（可能都被禁用或没有对应技能）",
                    "No comparable colonist found (they may all be disabled or lack that skill)");
                return false;
            }

            pawn = picked;
            return true;
        }

        private static SkillDef FindSkill(string name)
        {
            foreach (SkillDef def in DefDatabase<SkillDef>.AllDefs)
            {
                if (def != null && string.Equals(def.defName, name, StringComparison.OrdinalIgnoreCase)) return def;
            }

            return null;
        }

        private static bool MatchesName(Pawn pawn, string name)
        {
            if (string.Equals(pawn.LabelShort, name, StringComparison.OrdinalIgnoreCase)) return true;
            if (pawn.Name != null)
            {
                if (string.Equals(pawn.Name.ToStringShort, name, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(pawn.Name.ToStringFull, name, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return string.Equals(pawn.Label, name, StringComparison.OrdinalIgnoreCase);
        }

        private static List<Pawn> Colonists(ActionContext context)
        {
            var list = new List<Pawn>();
            if (context == null || context.Map == null || context.Map.mapPawns == null) return list;

            foreach (Pawn pawn in context.Map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn != null) list.Add(pawn);
            }

            return list;
        }
    }
}