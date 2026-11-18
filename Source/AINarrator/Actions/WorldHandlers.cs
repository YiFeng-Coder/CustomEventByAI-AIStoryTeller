using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Stellaris.AI
{
    // ─────────────────────────────────────────────────────────────────────────
    // faction.goodwill
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class FactionGoodwillHandler : IGameActionHandler
    {
        public string OpType => "faction.goodwill";
        public ActionPermission Permission => ActionPermission.Major;

        public ActionCategory Category => ActionCategory.World;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            "faction.goodwill{factionDef=派系 DefName, delta=-30~30} —— 调整该派系对玩家的好感";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;

            string name = a.ArgString("factionDef");
            if (name.Length == 0) { error = "缺少 factionDef"; return false; }

            Faction faction = FindFaction(name);
            if (faction == null)
            {
                error = "找不到该派系的实例（可能已不存在/尚未接触）：" + name;
                return false;
            }

            int delta = a.ArgInt("delta", 0);
            if (delta == 0) { error = "delta 不能为 0"; return false; }
            if (delta < -30 || delta > 30) { error = "delta 必须在 -30..30"; return false; }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.World, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            Faction faction = FindFaction(a.ArgString("factionDef"));
            if (faction == null) { error = "派系已不存在"; return false; }

            int delta = System.Math.Max(-30, System.Math.Min(30, a.ArgInt("delta", 0)));
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.World, 1, out error)) return false;
            if (!faction.TryAffectGoodwillWith(Faction.OfPlayer, delta))
            {
                error = "派系好感未改变";
                return false;
            }
            return true;
        }

        private static Faction FindFaction(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return null;

            foreach (Faction faction in Find.FactionManager.AllFactions)
            {
                if (faction == null || faction.IsPlayer || faction.def == null) continue;
                if (faction.def.defName == defName || faction.def.defName == defName.Replace(" ", "_")) return faction;
            }

            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // research.grant
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class ResearchGrantHandler : IGameActionHandler
    {
        public string OpType => "research.grant";
        public ActionPermission Permission => ActionPermission.Major;

        public ActionCategory Category => ActionCategory.World;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            "research.grant{researchDef=研究 DefName, amount=0.05~0.5（占该项目比例）} —— 给指定研究项目加进度";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;

            string name = a.ArgString("researchDef");
            if (name.Length == 0) { error = "缺少 researchDef"; return false; }

            ResearchProjectDef def = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(name);
            if (def == null) { error = "不存在的 ResearchProjectDef：" + name; return false; }
            if (!def.CanStartNow && def.IsFinished)
            {
                error = "该研究已经完成";
                return false;
            }

            float amount = a.ArgFloat("amount", 0.15f);
            if (amount < 0.05f || amount > 0.5f) { error = "amount 必须在 0.05..0.5"; return false; }
            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.World, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            ResearchProjectDef def = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(a.ArgString("researchDef"));
            if (def == null) { error = "动作所需定义已不可用"; return false; }
            if (def.IsFinished) { error = "指定研究已经完成"; return false; }

            float fraction = System.Math.Max(0.05f, System.Math.Min(0.5f, a.ArgFloat("amount", 0.15f)));
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.World, 1, out error)) return false;
            Find.ResearchManager.AddProgress(def, def.baseCost * fraction);
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // quest.start
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class QuestStartHandler : IGameActionHandler
    {
        public string OpType => "quest.start";
        public ActionPermission Permission => ActionPermission.Major;

        public ActionCategory Category => ActionCategory.World;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            "quest.start{questScriptDef=任务模板 DefName, points=可选威胁点数} —— 抛出一个原版任务";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;

            string name = a.ArgString("questScriptDef");
            if (name.Length == 0) { error = "缺少 questScriptDef"; return false; }
            if (DefDatabase<QuestScriptDef>.GetNamedSilentFail(name) == null)
            {
                error = "不存在的 QuestScriptDef：" + name;
                return false;
            }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.World, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            QuestScriptDef def = DefDatabase<QuestScriptDef>.GetNamedSilentFail(a.ArgString("questScriptDef"));
            if (def == null) { error = "动作所需定义已不可用"; return false; }

            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.World, 1, out error)) return false;

            int points = a.ArgInt("points", 0);
            if (points <= 0 && ctx.Map != null)
            {
                points = (int)StorytellerUtility.DefaultThreatPointsNow(ctx.Map);
            }

            if (QuestUtility.GenerateQuestAndMakeAvailable(def, points) == null)
            {
                error = "未能生成任务";
                return false;
            }
            return true;
        }
    }
}
