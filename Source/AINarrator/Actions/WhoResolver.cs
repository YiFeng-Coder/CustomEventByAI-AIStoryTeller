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

        public static bool TryResolve(PlannedAction action, ActionContext context, TargetPolicy policy,
            bool socialCategory, out Pawn pawn, out string error)
        {
            pawn = null;
            error = null;

            string who = action.ArgString("who").Trim();
            if (who.Length == 0) who = action.ArgString("handle").Trim();
            if (who.Length == 0)
            {
                error = "缺少 who（写法：" + Help + "）";
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
                error = "缺少目标";
                return false;
            }

            int colon = who.IndexOf(':');
            string kind = colon > 0 ? who.Substring(0, colon).Trim().ToLowerInvariant() : "";
            string value = colon > 0 ? who.Substring(colon + 1).Trim() : who;

            if (policy == TargetPolicy.HandleOnly && kind != "handle")
            {
                error = "这是危险动作，只能用 who=\"handle:名字\" 指向本事件自己生成的角色，"
                        + "不能指向地图上已有的角色";
                return false;
            }

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
                    error = "找不到 \"" + who + "\"：既不是句柄，也不是你的殖民者名字。写法：" + Help;
                    return false;

                default:
                    error = "未知的 who 前缀 \"" + kind + "\"；写法：" + Help;
                    return false;
            }

            // 校验已通过的生成动作后，可以先确认句柄的类型，实际生成后再检查角色状态和派系。
            if (pawn == null) return true;
            if (pawn.Dead) { error = "目标已经死亡"; return false; }
            if (!pawn.Spawned) { error = "目标不在任何地图上"; return false; }

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
                case TargetPolicy.HandleOnly:
                    error = "危险动作不能指向你的殖民者";
                    return false;

                case TargetPolicy.NonPlayerOnly:
                    error = "该动作不能改变你的殖民者的身份 / 特质 / 关系（只对 AI 生成的角色生效）";
                    return false;

                case TargetPolicy.Neutral:
                    if (socialCategory && !PlayerSocialAllowed)
                    {
                        error = "玩家在设置里关闭了「让心理层影响殖民者」，这一项不能落到你的殖民者身上";
                        return false;
                    }

                    return true;

                default:
                    return true;
            }
        }

        private static bool PlayerSocialAllowed =>
            AINarratorMod.Settings != null && AINarratorMod.Settings.affectPlayerColonistsSocial;

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

            error = "没有叫 \"" + name + "\" 的殖民者；当前是：" + string.Join("、", names.ToArray());
            return false;
        }

        private static bool TryExtreme(ActionContext context, bool best, string what, out Pawn pawn, out string error)
        {
            pawn = null;
            error = null;

            List<Pawn> colonists = Colonists(context);
            if (colonists.Count == 0)
            {
                error = "地图上没有殖民者";
                return false;
            }

            bool byMood = what.Equals("mood", StringComparison.OrdinalIgnoreCase);
            SkillDef skill = byMood ? null : FindSkill(what);
            if (!byMood && skill == null)
            {
                error = "best/worst 后面只能是 Mood 或某个技能 DefName（例如 Shooting）";
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
                error = "找不到可比较的殖民者（可能都被禁用或没有对应技能）";
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
