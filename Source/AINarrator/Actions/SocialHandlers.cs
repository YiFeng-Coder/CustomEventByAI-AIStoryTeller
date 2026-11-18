using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Stellaris.AI
{
    // ─────────────────────────────────────────────────────────────────────────
    // pawn.thought —— 给角色一条情绪记忆（走原版心情系统，玩家能在"想法"里看到）
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class PawnThoughtHandler : IGameActionHandler
    {
        public string OpType => "pawn.thought";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Social;

        // 负面想法也算"心理层"，所以是 Neutral：玩家殖民者受设置开关约束。
        public TargetPolicy Policy => TargetPolicy.Neutral;

        public string Describe() =>
            "pawn.thought{who=handle:p1|colonist:名字, thoughtDef=想法 DefName, other=可选第二人} " +
            "—— 给角色一条情绪记忆（时长由 ThoughtDef 决定）";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, true, out _, out error)) return false;

            string name = a.ArgString("thoughtDef");
            if (name.Length == 0) { error = "缺少 thoughtDef"; return false; }

            ThoughtDef def = DefDatabase<ThoughtDef>.GetNamedSilentFail(name);
            if (def == null) { error = "不存在的 ThoughtDef：" + name; return false; }
            if (!def.IsMemory)
            {
                error = name + " 不是记忆类想法（需要 ThoughtDef.isMemory=true）";
                return false;
            }

            string other = a.ArgString("other");
            if (other.Length == 0 && IsSocialThought(def))
            {
                error = name + " 是针对某个人的想法，需要补 other=（第二个人）";
                return false;
            }

            if (other.Length > 0 &&
                !WhoResolver.TryResolveToken(ctx, other, TargetPolicy.Positive, true, out _, out error))
            {
                return false;
            }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Social, 1, out error);
        }

        /// <summary>Thought_MemorySocial 需要"另一个人"，否则原版计算观点差时会拿到 null。</summary>
        private static bool IsSocialThought(ThoughtDef def) =>
            def != null && def.thoughtClass != null && def.thoughtClass.Name.Contains("Social");

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, true, out Pawn pawn, out error)) return false;

            ThoughtDef def = DefDatabase<ThoughtDef>.GetNamedSilentFail(a.ArgString("thoughtDef"));
            if (def == null || pawn.needs == null || pawn.needs.mood == null) { error = "想法定义或目标心情系统不可用"; return false; }

            Pawn other = null;
            string otherToken = a.ArgString("other");
            if (otherToken.Length > 0 &&
                !WhoResolver.TryResolveToken(ctx, otherToken, TargetPolicy.Positive, true, out other, out error))
                return false;

            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Social, 1, out error)) return false;
            pawn.needs.mood.thoughts.memories.TryGainMemory(def, other, null);
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // pawn.mood —— 小幅推动心情（不替代想法系统）
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class PawnMoodHandler : IGameActionHandler
    {
        public string OpType => "pawn.mood";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Social;
        public TargetPolicy Policy => TargetPolicy.Neutral;

        public string Describe() =>
            "pawn.mood{who=..., delta=-0.2~0.2} —— 直接推一点心情（幅度刻意很小，主要用 pawn.thought）";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, true, out _, out error)) return false;

            float delta = a.ArgFloat("delta", 0f);
            if (delta == 0f) { error = "delta 不能为 0"; return false; }
            if (delta < -0.2f || delta > 0.2f) { error = "delta 必须在 -0.2..0.2"; return false; }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Social, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, true, out Pawn pawn, out error)) return false;
            if (pawn.needs == null || pawn.needs.mood == null) { error = "目标没有心情系统"; return false; }

            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Social, 1, out error)) return false;

            float delta = Math.Max(-0.2f, Math.Min(0.2f, a.ArgFloat("delta", 0f)));
            pawn.needs.mood.CurLevel = Math.Max(0.05f, Math.Min(0.95f, pawn.needs.mood.CurLevel + delta));
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // pawn.inspiration —— 危机之后的灵感
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class PawnInspirationHandler : IGameActionHandler
    {
        public string OpType => "pawn.inspiration";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Social;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            "pawn.inspiration{who=..., inspirationDef=灵感 DefName} —— 让角色进入某种灵感状态";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, false, out _, out error)) return false;

            string name = a.ArgString("inspirationDef");
            if (name.Length == 0) { error = "缺少 inspirationDef"; return false; }
            if (DefDatabase<InspirationDef>.GetNamedSilentFail(name) == null)
            {
                error = "不存在的 InspirationDef：" + name;
                return false;
            }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Social, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, false, out Pawn pawn, out error)) return false;

            InspirationDef def = DefDatabase<InspirationDef>.GetNamedSilentFail(a.ArgString("inspirationDef"));
            if (def == null || pawn.mindState == null || pawn.mindState.inspirationHandler == null) { error = "灵感定义或目标灵感系统不可用"; return false; }

            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Social, 1, out error)) return false;
            if (!pawn.mindState.inspirationHandler.TryStartInspiration(def, "剧情事件", true))
            {
                error = "目标未能获得指定灵感";
                return false;
            }
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // pawn.trait —— 创伤改变一个人（不碰你的殖民者）
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class PawnTraitHandler : IGameActionHandler
    {
        public string OpType => "pawn.trait";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Social;
        public TargetPolicy Policy => TargetPolicy.NonPlayerOnly;

        public string Describe() =>
            "pawn.trait{who=handle:p1 | colonist:名字, traitDef=特质 DefName, remove=可选 true} —— 给角色加/去特质；能否指向你的殖民者由设置决定";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, true, out Pawn pawn, out error)) return false;

            string name = a.ArgString("traitDef");
            if (name.Length == 0) { error = "缺少 traitDef"; return false; }

            TraitDef def = DefDatabase<TraitDef>.GetNamedSilentFail(name);
            if (def == null) { error = "不存在的 TraitDef：" + name; return false; }

            // 重复加/删不报错：这不是"非法参数"，只是无事可做。
            // 让整段脚本因为"他早就有这个特质"而作废，代价远大于静默跳过。
            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.PawnEdit, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, true, out Pawn pawn, out error)) return false;

            TraitDef def = DefDatabase<TraitDef>.GetNamedSilentFail(a.ArgString("traitDef"));
            if (def == null || pawn.story == null || pawn.story.traits == null) { error = "特质定义或目标特质系统不可用"; return false; }

            Trait existing = pawn.story.traits.GetTrait(def);
            bool remove = a.ArgBool("remove", false);
            if (remove && existing == null)
            {
                error = "目标没有指定特质";
                return false;
            }
            if (!remove && existing != null)
            {
                error = "目标已经有指定特质";
                return false;
            }

            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.PawnEdit, 1, out error)) return false;
            if (remove) pawn.story.traits.RemoveTrait(existing);
            else pawn.story.traits.GainTrait(new Trait(def, 0), true);
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // pawn.relation —— 建立关系（双向）
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class PawnRelationHandler : IGameActionHandler
    {
        public string OpType => "pawn.relation";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Social;
        public TargetPolicy Policy => TargetPolicy.NonPlayerOnly;

        public string Describe() =>
            "pawn.relation{who=handle:p1 | colonist:名字, other=handle:p2|colonist:名字, relationDef=关系 DefName, both=可选 默认 true} " +
            "—— 在两个角色之间建立关系";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, true, out _, out error)) return false;

            string otherToken = a.ArgString("other");
            if (otherToken.Length == 0) { error = "缺少 other（第二个角色）"; return false; }

            // other 走同一个 TargetPolicy：另一端如果是你的殖民者，同样要过"影响范围"设置。
            if (!WhoResolver.TryResolveToken(ctx, otherToken, TargetPolicy.NonPlayerOnly, true, out _, out error))
            {
                return false;
            }

            string name = a.ArgString("relationDef");
            if (name.Length == 0) { error = "缺少 relationDef"; return false; }
            if (DefDatabase<PawnRelationDef>.GetNamedSilentFail(name) == null)
            {
                error = "不存在的 PawnRelationDef：" + name;
                return false;
            }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Social, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, true, out Pawn pawn, out error)) return false;
            if (!WhoResolver.TryResolveToken(ctx, a.ArgString("other"), TargetPolicy.NonPlayerOnly, true,
                    out Pawn other, out error)) return false;

            PawnRelationDef def = DefDatabase<PawnRelationDef>.GetNamedSilentFail(a.ArgString("relationDef"));
            if (def == null || pawn.relations == null || other.relations == null || pawn == other) { error = "关系定义不可用，或目标无法建立该关系"; return false; }

            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Social, 1, out error)) return false;

            pawn.relations.AddDirectRelation(def, other);
            if (a.ArgBool("both", true) && other.relations != null)
            {
                other.relations.AddDirectRelation(def, pawn);
            }
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // pawn.mental —— 精神崩溃（危险预算；能否指向你的殖民者由设置决定）
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class PawnMentalHandler : IGameActionHandler
    {
        public string OpType => "pawn.mental";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Social;
        public TargetPolicy Policy => TargetPolicy.Dangerous;

        public string Describe() =>
            "pawn.mental{who=handle:p1 | colonist:名字, mentalStateDef=精神状态的 DefName} —— 让该角色进入精神崩溃（危险预算）；能否指向你的殖民者由设置决定";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, true, out _, out error)) return false;

            string name = a.ArgString("mentalStateDef");
            if (name.Length == 0) { error = "缺少 mentalStateDef"; return false; }
            if (DefDatabase<MentalStateDef>.GetNamedSilentFail(name) == null)
            {
                error = "不存在的 MentalStateDef：" + name;
                return false;
            }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Danger, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, true, out Pawn pawn, out error)) return false;

            MentalStateDef def = DefDatabase<MentalStateDef>.GetNamedSilentFail(a.ArgString("mentalStateDef"));
            if (def == null || pawn.mindState == null || pawn.mindState.mentalStateHandler == null) { error = "精神状态定义或目标精神状态系统不可用"; return false; }

            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Danger, 1, out error)) return false;
            if (!pawn.mindState.mentalStateHandler.TryStartMentalState(def, "剧情事件", true))
            {
                error = "目标未能进入指定精神状态";
                return false;
            }
            return true;
        }
    }
}