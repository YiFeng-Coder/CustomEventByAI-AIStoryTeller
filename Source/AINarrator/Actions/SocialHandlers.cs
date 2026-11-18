using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace CustomEventByAI.AIStoryTeller
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
            Loc.Pick(
                "pawn.thought{who=handle:p1|colonist:名字, thoughtDef=想法 DefName, other=可选第二人} " +
                "—— 给角色一条情绪记忆（时长由 ThoughtDef 决定）",
                "pawn.thought{who=handle:p1|colonist:name, thoughtDef=thought DefName, other=optional second person} " +
                "— give the pawn one mood memory (the ThoughtDef decides how long it lasts)");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, true, out _, out error)) return false;

            string name = a.ArgString("thoughtDef");
            if (name.Length == 0) { error = Loc.Pick("缺少 thoughtDef", "missing thoughtDef"); return false; }

            ThoughtDef def = DefDatabase<ThoughtDef>.GetNamedSilentFail(name);
            if (def == null) { error = Loc.Pick("不存在的 ThoughtDef：" + name, "unknown ThoughtDef: " + name); return false; }
            if (!def.IsMemory)
            {
                error = Loc.Pick(name + " 不是记忆类想法（需要 ThoughtDef.isMemory=true）", name + " is not a memory thought (ThoughtDef.isMemory=true is required)");
                return false;
            }

            string other = a.ArgString("other");
            if (other.Length == 0 && IsSocialThought(def))
            {
                error = Loc.Pick(name + " 是针对某个人的想法，需要补 other=（第二个人）", name + " is a thought about a specific person; add other= (the second person)");
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
            if (def == null || pawn.needs == null || pawn.needs.mood == null) { error = Loc.Pick("想法定义或目标心情系统不可用", "the thought def or the target's mood system is unavailable"); return false; }

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
            Loc.Pick(
                "pawn.mood{who=..., delta=-0.2~0.2} —— 直接推一点心情（幅度刻意很小，主要用 pawn.thought）",
                "pawn.mood{who=..., delta=-0.2~0.2} — nudge mood directly (the range is deliberately small; prefer pawn.thought)");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, true, out _, out error)) return false;

            float delta = a.ArgFloat("delta", 0f);
            if (delta == 0f) { error = Loc.Pick("delta 不能为 0", "delta must not be 0"); return false; }
            if (delta < -0.2f || delta > 0.2f) { error = Loc.Pick("delta 必须在 -0.2..0.2", "delta must be within -0.2..0.2"); return false; }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Social, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, true, out Pawn pawn, out error)) return false;
            if (pawn.needs == null || pawn.needs.mood == null) { error = Loc.Pick("目标没有心情系统", "the target has no mood system"); return false; }

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
            Loc.Pick(
                "pawn.inspiration{who=..., inspirationDef=具体的灵感 DefName, replace=可选 true} —— " +
                "让角色进入指定灵感（灵感效果各不相同，必须写具体 DefName；原版灵感只能给殖民者；" +
                "目标已有灵感时默认拒绝，replace=true 会先结束旧的再给新的）",
                "pawn.inspiration{who=..., inspirationDef=the concrete inspiration DefName, replace=optional true} — " +
                "put the pawn into the given inspiration (each inspiration does something different, so a concrete DefName is required; vanilla inspirations only work on colonists; " +
                "if the target already has an inspiration this is rejected by default, and replace=true ends the old one first)");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, false, out Pawn pawn, out error)) return false;

            string name = a.ArgString("inspirationDef");
            if (name.Length == 0) { error = Loc.Pick("缺少 inspirationDef", "missing inspirationDef"); return false; }

            InspirationDef def = DefDatabase<InspirationDef>.GetNamedSilentFail(name);
            if (def == null)
            {
                error = Loc.Pick("不存在的 InspirationDef：" + name, "unknown InspirationDef: " + name);
                return false;
            }
            if (def.Worker == null)
            {
                error = Loc.Pick(def.defName + " 没有可用的 InspirationWorker", def.defName + " has no usable InspirationWorker");
                return false;
            }

            // 校验阶段目标可能还是"本阶段待生成"的句柄（pawn == null）；真正的角色状态检查放到执行时。
            if (pawn != null && !CheckTarget(pawn, def, a.ArgBool("replace", false), out error)) return false;

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Social, 1, out error);
        }

        /// <summary>
        /// 把 TryStartInspiration 里所有会导致"执行到一半才失败"的条件提前到校验阶段：
        /// 只有殖民者能获得原版灵感、不能叠加、不能被状态阻断、必须满足 Def 的技能/工作/年龄前提。
        /// </summary>
        private static bool CheckTarget(Pawn pawn, InspirationDef def, bool replace, out string error)
        {
            error = null;

            if (pawn.mindState == null || pawn.mindState.inspirationHandler == null)
            {
                error = Loc.Pick("目标没有灵感系统：" + pawn.LabelShort, "the target has no inspiration system: " + pawn.LabelShort);
                return false;
            }

            if (!def.allowedOnNonColonists && !pawn.IsColonist)
            {
                error = Loc.Pick("原版灵感只能给殖民者，" + pawn.LabelShort + " 不是殖民者。"
                        + "请把 who 改成 colonist:名字，或改用其它动作。",
                        "vanilla inspirations only work on colonists, and " + pawn.LabelShort + " is not a colonist. "
                        + "Change who to colonist:name, or use a different action.");
                return false;
            }

            InspirationHandler handler = pawn.mindState.inspirationHandler;
            if (handler.Inspired && !replace)
            {
                InspirationDef current = handler.CurStateDef;
                error = Loc.Pick("「" + pawn.LabelShort + "」已经有灵感「"
                        + (current != null ? current.label : "?") + "」，游戏不允许叠加。"
                        + "要改成 " + def.defName + "（" + def.label + "），请加 replace=true。",
                        pawn.LabelShort + " already has the inspiration \""
                        + (current != null ? current.label : "?") + "\"; the game does not allow stacking. "
                        + "To change it to " + def.defName + " (" + def.label + "), add replace=true.");
                return false;
            }

            if (BlocksInspirations(pawn))
            {
                error = Loc.Pick("「" + pawn.LabelShort + "」身上的状态（blocksInspirations）阻断了灵感，"
                        + "先移除相关状态或换一个人。",
                        pawn.LabelShort + " has a hediff (blocksInspirations) that blocks inspirations; "
                        + "remove it first or pick another pawn.");
                return false;
            }

            if (!CanOccur(def, pawn))
            {
                error = Loc.Pick("「" + pawn.LabelShort + "」不满足 " + def.defName + "（" + def.label
                        + "）的前提（技能 / 工作类型 / 年龄 / 身体状态）。" + Available(pawn),
                        pawn.LabelShort + " does not meet the requirements of " + def.defName + " (" + def.label
                        + "): skills / work types / age / body state." + Available(pawn));
                return false;
            }

            return true;
        }

        private static bool CanOccur(InspirationDef def, Pawn pawn)
        {
            try
            {
                return def.Worker != null && def.Worker.InspirationCanOccur(pawn);
            }
            catch
            {
                return false;
            }
        }

        private static bool BlocksInspirations(Pawn pawn)
        {
            if (pawn.health == null || pawn.health.hediffSet == null) return false;

            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                HediffStage stage = hediffs[i] != null ? hediffs[i].CurStage : null;
                if (stage != null && stage.blocksInspirations) return true;
            }

            return false;
        }

        /// <summary>错误信息里给出"这个人现在能获得哪些灵感"，模型下一轮就能改对。</summary>
        private static string Available(Pawn pawn)
        {
            var names = new List<string>();
            foreach (InspirationDef def in DefDatabase<InspirationDef>.AllDefs)
            {
                if (def == null || !CanOccur(def, pawn)) continue;
                names.Add(def.defName + "(" + def.label + ")");
            }

            if (names.Count == 0)
            {
                return Loc.Pick("他/她现在没有可获得的灵感。", " They cannot get any inspiration right now.");
            }

            return Loc.Pick("他/她现在可以获得：", " They can currently get: ")
                   + string.Join(Loc.Pick("、", ", "), names.ToArray())
                   + Loc.Pick("。", ".");
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, false, out Pawn pawn, out error)) return false;

            InspirationDef def = DefDatabase<InspirationDef>.GetNamedSilentFail(a.ArgString("inspirationDef"));
            if (def == null || pawn.mindState == null || pawn.mindState.inspirationHandler == null)
            {
                error = Loc.Pick("灵感定义或目标灵感系统不可用", "the inspiration def or the target's inspiration system is unavailable");
                return false;
            }

            if (!CheckTarget(pawn, def, a.ArgBool("replace", false), out error)) return false;
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Social, 1, out error)) return false;

            InspirationHandler handler = pawn.mindState.inspirationHandler;
            if (handler.Inspired)
            {
                // replace=true 已在 CheckTarget 里确认；先结束旧的，TryStartInspiration 才不会被 "已有灵感" 挡住。
                Inspiration current = handler.CurState;
                if (current != null) handler.EndInspiration(current);
            }

            if (!handler.TryStartInspiration(def, Loc.Pick("剧情事件", "story event"), true))
            {
                error = Loc.Pick("目标未能获得指定灵感（可能已有灵感或当前不可获得）", "the target did not gain that inspiration (it may already have one, or cannot get it right now)");
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
            Loc.Pick(
                "pawn.trait{who=handle:p1 | colonist:名字, traitDef=特质 DefName, remove=可选 true} —— 给角色加/去特质；能否指向你的殖民者由设置决定",
                "pawn.trait{who=handle:p1 | colonist:name, traitDef=trait DefName, remove=optional true} — add or remove a trait on that pawn; whether you may target your own colonists depends on the settings");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, true, out Pawn pawn, out error)) return false;

            string name = a.ArgString("traitDef");
            if (name.Length == 0) { error = Loc.Pick("缺少 traitDef", "missing traitDef"); return false; }

            TraitDef def = DefDatabase<TraitDef>.GetNamedSilentFail(name);
            if (def == null) { error = Loc.Pick("不存在的 TraitDef：" + name, "unknown TraitDef: " + name); return false; }

            // 重复加/删不报错：这不是"非法参数"，只是无事可做。
            // 让整段脚本因为"他早就有这个特质"而作废，代价远大于静默跳过。
            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.PawnEdit, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, true, out Pawn pawn, out error)) return false;

            TraitDef def = DefDatabase<TraitDef>.GetNamedSilentFail(a.ArgString("traitDef"));
            if (def == null || pawn.story == null || pawn.story.traits == null) { error = Loc.Pick("特质定义或目标特质系统不可用", "the trait def or the target's trait system is unavailable"); return false; }

            Trait existing = pawn.story.traits.GetTrait(def);
            bool remove = a.ArgBool("remove", false);
            if (remove && existing == null)
            {
                error = Loc.Pick("目标没有指定特质", "the target does not have that trait");
                return false;
            }
            if (!remove && existing != null)
            {
                error = Loc.Pick("目标已经有指定特质", "the target already has that trait");
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
            Loc.Pick(
                "pawn.relation{who=handle:p1 | colonist:名字, other=handle:p2|colonist:名字, relationDef=关系 DefName, both=可选 默认 true} " +
                "—— 在两个角色之间建立关系",
                "pawn.relation{who=handle:p1 | colonist:name, other=handle:p2|colonist:name, relationDef=relation DefName, both=optional, default true} " +
                "— create a relation between the two pawns");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, true, out _, out error)) return false;

            string otherToken = a.ArgString("other");
            if (otherToken.Length == 0) { error = Loc.Pick("缺少 other（第二个角色）", "missing other (the second pawn)"); return false; }

            // other 走同一个 TargetPolicy：另一端如果是你的殖民者，同样要过"影响范围"设置。
            if (!WhoResolver.TryResolveToken(ctx, otherToken, TargetPolicy.NonPlayerOnly, true, out _, out error))
            {
                return false;
            }

            string name = a.ArgString("relationDef");
            if (name.Length == 0) { error = Loc.Pick("缺少 relationDef", "missing relationDef"); return false; }
            if (DefDatabase<PawnRelationDef>.GetNamedSilentFail(name) == null)
            {
                error = Loc.Pick("不存在的 PawnRelationDef：" + name, "unknown PawnRelationDef: " + name);
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
            if (def == null || pawn.relations == null || other.relations == null || pawn == other) { error = Loc.Pick("关系定义不可用，或目标无法建立该关系", "the relation def is unavailable, or the targets cannot have that relation"); return false; }

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
            Loc.Pick(
                "pawn.mental{who=handle:p1 | colonist:名字, mentalStateDef=精神状态的 DefName} —— 让该角色进入精神崩溃（危险预算）；能否指向你的殖民者由设置决定",
                "pawn.mental{who=handle:p1 | colonist:name, mentalStateDef=the mental state DefName} — put that pawn into a mental break (counts against the danger budget); whether you may target your own colonists depends on the settings");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, true, out _, out error)) return false;

            string name = a.ArgString("mentalStateDef");
            if (name.Length == 0) { error = Loc.Pick("缺少 mentalStateDef", "missing mentalStateDef"); return false; }
            if (DefDatabase<MentalStateDef>.GetNamedSilentFail(name) == null)
            {
                error = Loc.Pick("不存在的 MentalStateDef：" + name, "unknown MentalStateDef: " + name);
                return false;
            }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Danger, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!WhoResolver.TryResolve(a, ctx, Policy, true, out Pawn pawn, out error)) return false;

            MentalStateDef def = DefDatabase<MentalStateDef>.GetNamedSilentFail(a.ArgString("mentalStateDef"));
            if (def == null || pawn.mindState == null || pawn.mindState.mentalStateHandler == null) { error = Loc.Pick("精神状态定义或目标精神状态系统不可用", "the mental state def or the target's mental state system is unavailable"); return false; }

            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Danger, 1, out error)) return false;
            if (!pawn.mindState.mentalStateHandler.TryStartMentalState(def, Loc.Pick("剧情事件", "story event"), true))
            {
                error = Loc.Pick("目标未能进入指定精神状态", "the target did not enter that mental state");
                return false;
            }
            return true;
        }
    }
}