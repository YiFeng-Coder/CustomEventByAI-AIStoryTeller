using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Stellaris.AI
{
    /// <summary>
    /// 角色类动作。全部通过**句柄**定位目标，句柄由脚本在生成时声明
    /// （例如 pawn.spawn 的 args.handle = "p1"），不会误伤玩家原有殖民者。
    /// </summary>
    public sealed class PawnHurtHandler : IGameActionHandler
    {
        public string OpType => "pawn.hurt";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Pawn;
        public TargetPolicy Policy => TargetPolicy.HandleOnly;

        public string Describe() =>
            "pawn.hurt{who=handle:p1, hediffDef=状态 DefName, severity=0.1~0.8} —— 给该角色加一个状态（受伤/中毒/感染；只能指向本事件生成的角色）";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!TryPawn(a, ctx, Policy, out _, out error)) return false;

            string name = a.ArgString("hediffDef");
            if (name.Length == 0) { error = "缺少 hediffDef"; return false; }
            if (DefDatabase<HediffDef>.GetNamedSilentFail(name) == null)
            {
                error = "不存在的 HediffDef：" + name;
                return false;
            }

            float severity = a.ArgFloat("severity", 0.3f);
            if (severity < 0.1f || severity > 0.8f) { error = "severity 必须在 0.1..0.8"; return false; }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.PawnEdit, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!TryPawn(a, ctx, Policy, out Pawn pawn, out error)) return false;

            HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail(a.ArgString("hediffDef"));
            if (def == null) { error = "动作所需定义已不可用"; return false; }

            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.PawnEdit, 1, out error)) return false;

            Hediff hediff = HediffMaker.MakeHediff(def, pawn);
            if (hediff == null) { error = "未能创建健康状态"; return false; }

            hediff.Severity = System.Math.Max(0.1f, System.Math.Min(0.8f, a.ArgFloat("severity", 0.3f)));
            pawn.health.AddHediff(hediff);
            return true;
        }

        /// <summary>
        /// 目标解析交给 <see cref="WhoResolver"/>：句柄 / 名字 / best / worst 都支持，
        /// 但"能不能落到玩家殖民者身上"由 policy 决定（这里是危险动作）。
        /// </summary>
        internal static bool TryPawn(PlannedAction a, ActionContext ctx, TargetPolicy policy,
            out Pawn pawn, out string error) =>
            WhoResolver.TryResolve(a, ctx, policy, false, out pawn, out error);
    }

    // ─────────────────────────────────────────────────────────────────────────
    public sealed class PawnHealHandler : IGameActionHandler
    {
        public string OpType => "pawn.heal";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Pawn;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            "pawn.heal{who=handle:p1 | colonist:名字 | best:技能名, hediffDef=可选} —— 指定状态就移除它；不指定就移除该角色所有负面状态";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out _, out error)) return false;

            string name = a.ArgString("hediffDef");
            if (name.Length > 0 && DefDatabase<HediffDef>.GetNamedSilentFail(name) == null)
            {
                error = "不存在的 HediffDef：" + name;
                return false;
            }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.PawnEdit, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out Pawn pawn, out error)) return false;

            string name = a.ArgString("hediffDef");
            if (name.Length > 0)
            {
                HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail(name);
                Hediff found = def == null ? null : pawn.health.hediffSet.GetFirstHediffOfDef(def);
                if (found == null) { error = "目标没有指定的健康状态"; return false; }
                if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.PawnEdit, 1, out error)) return false;
                pawn.health.RemoveHediff(found);
                return true;
            }

            var toRemove = new List<Hediff>();
            foreach (Hediff hediff in pawn.health.hediffSet.hediffs)
            {
                if (hediff != null && hediff.def != null && hediff.def.isBad) toRemove.Add(hediff);
            }

            if (toRemove.Count == 0) { error = "目标没有可移除的负面状态"; return false; }
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.PawnEdit, 1, out error)) return false;
            for (int i = 0; i < toRemove.Count; i++) pawn.health.RemoveHediff(toRemove[i]);
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    public sealed class PawnKillHandler : IGameActionHandler
    {
        public string OpType => "pawn.kill";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Pawn;
        public TargetPolicy Policy => TargetPolicy.HandleOnly;

        public string Describe() =>
            "pawn.kill{who=handle:p1} —— 让该角色死亡（真实死亡流程，会产生尸体，计入危险预算；只能指向本事件生成的角色）";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out _, out error)) return false;
            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Danger, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out Pawn pawn, out error)) return false;
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Danger, 1, out error)) return false;
            pawn.Kill(null, null);
            if (!pawn.Dead) { error = "目标未死亡"; return false; }
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    public sealed class PawnGearHandler : IGameActionHandler
    {
        public string OpType => "pawn.gear";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Pawn;
        public TargetPolicy Policy => TargetPolicy.Neutral;

        public string Describe() =>
            "pawn.gear{who=..., thingDef=装备 DefName, slot=apparel|weapon} —— 给该角色穿戴/装备一件物品";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out _, out error)) return false;

            string name = a.ArgString("thingDef");
            if (name.Length == 0) { error = "缺少 thingDef"; return false; }

            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
            if (def == null) { error = "不存在的 ThingDef：" + name; return false; }

            string slot = a.ArgString("slot", "apparel").ToLowerInvariant();
            if (slot != "apparel" && slot != "weapon")
            {
                error = "slot 只能是 apparel 或 weapon";
                return false;
            }

            if (slot == "apparel" && !typeof(Apparel).IsAssignableFrom(def.thingClass))
            {
                error = name + " 不是可穿的衣物";
                return false;
            }

            if (slot == "weapon" && !typeof(ThingWithComps).IsAssignableFrom(def.thingClass))
            {
                error = name + " 不能当武器";
                return false;
            }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.PawnEdit, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out Pawn pawn, out error)) return false;

            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(a.ArgString("thingDef"));
            if (def == null) { error = "动作所需定义已不可用"; return false; }

            string slot = a.ArgString("slot", "apparel").ToLowerInvariant();
            if ((slot == "apparel" && pawn.apparel == null) || (slot == "weapon" && pawn.equipment == null))
            {
                error = "目标无法穿戴或装备指定物品";
                return false;
            }

            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.PawnEdit, 1, out error)) return false;
            Thing thing = ThingMaker.MakeThing(def);

            if (slot == "apparel")
            {
                if (!(thing is Apparel apparel)) { error = "无法制作指定衣物"; return false; }
                pawn.apparel.Wear(apparel);
            }
            else
            {
                if (!(thing is ThingWithComps equipment)) { error = "无法制作指定武器"; return false; }
                pawn.equipment.AddEquipment(equipment);
            }
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    public sealed class PawnSetSkillHandler : IGameActionHandler
    {
        public string OpType => "pawn.set_skill";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Pawn;
        public TargetPolicy Policy => TargetPolicy.NonPlayerOnly;

        public string Describe() =>
            "pawn.set_skill{who=handle:p1, skillDef=技能 DefName, level=0~20} —— 改该角色的技能等级（只对 AI 生成的角色生效）";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out _, out error)) return false;

            string name = a.ArgString("skillDef");
            if (name.Length == 0) { error = "缺少 skillDef"; return false; }
            if (DefDatabase<SkillDef>.GetNamedSilentFail(name) == null)
            {
                error = "不存在的 SkillDef：" + name;
                return false;
            }

            int level = a.ArgInt("level", 8);
            if (level < 0 || level > 20) { error = "level 必须在 0..20"; return false; }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.PawnEdit, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out Pawn pawn, out error)) return false;

            SkillDef def = DefDatabase<SkillDef>.GetNamedSilentFail(a.ArgString("skillDef"));
            if (def == null || pawn.skills == null) { error = "技能定义或目标技能系统不可用"; return false; }

            SkillRecord skill = pawn.skills.GetSkill(def);
            if (skill == null) { error = "目标没有指定技能"; return false; }
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.PawnEdit, 1, out error)) return false;
            skill.Level = System.Math.Max(0, System.Math.Min(20, a.ArgInt("level", 8)));
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    public sealed class PawnJoinHandler : IGameActionHandler
    {
        public string OpType => "pawn.join";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Pawn;
        public TargetPolicy Policy => TargetPolicy.NonPlayerOnly;

        public string Describe() =>
            "pawn.join{who=handle:p1} —— 让该角色加入玩家派系（重大后果，计入角色编辑预算）";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out Pawn pawn, out error)) return false;

            if (pawn != null && pawn.Faction == Faction.OfPlayer)
            {
                error = "该角色已经属于玩家派系";
                return false;
            }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.PawnEdit, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out Pawn pawn, out error)) return false;
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.PawnEdit, 1, out error)) return false;
            pawn.SetFaction(Faction.OfPlayer);
            return true;
        }
    }
}
