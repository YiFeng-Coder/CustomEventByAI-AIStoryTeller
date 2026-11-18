using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Stellaris.AI
{
    // ─────────────────────────────────────────────────────────────────────────
    // story.message —— 屏幕顶部小提示（不打断、不进信件栏）
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class StoryMessageHandler : IGameActionHandler
    {
        public string OpType => "story.message";
        public ActionPermission Permission => ActionPermission.Narrative;

        public ActionCategory Category => ActionCategory.Narrative;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            "story.message{text=字符串≤200, kind=neutral|positive|negative} —— 屏幕顶部一行小提示，不打断游戏";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            string text = a.ArgString("text");
            if (text.Length == 0) { error = "text 不能为空"; return false; }
            if (text.Length > 200) { error = "text 超过 200 字"; return false; }

            string kind = a.ArgString("kind", "neutral").ToLowerInvariant();
            if (kind != "neutral" && kind != "positive" && kind != "negative")
            {
                error = "kind 只能是 neutral / positive / negative";
                return false;
            }

            return true;
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            string kind = a.ArgString("kind", "neutral").ToLowerInvariant();
            MessageTypeDef type = kind == "positive"
                ? MessageTypeDefOf.PositiveEvent
                : kind == "negative"
                    ? MessageTypeDefOf.NegativeEvent
                    : MessageTypeDefOf.NeutralEvent;

            Messages.Message(a.ArgString("text"), type, false);
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // story.dialog —— 纯叙事弹窗（无选项，只有文本与关闭）
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class StoryDialogHandler : IGameActionHandler
    {
        public string OpType => "story.dialog";
        public ActionPermission Permission => ActionPermission.Narrative;

        public ActionCategory Category => ActionCategory.Narrative;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            "story.dialog{title=字符串≤60, text=字符串≤1500} —— 弹一个纯叙事窗口（玩家点右上角关闭），不会卡住事件";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            string text = a.ArgString("text");
            if (text.Length == 0) { error = "text 不能为空"; return false; }
            if (text.Length > 1500) { error = "text 超过 1500 字"; return false; }
            if (a.ArgString("title").Length > 60) { error = "title 超过 60 字"; return false; }
            return true;
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            string title = a.ArgString("title", ctx.Definition != null ? ctx.Definition.title : "叙事");
            Find.WindowStack.Add(new Dialog_NarrativeChoice(
                title,
                a.ArgString("text"),
                new List<KeyValuePair<string, string>>(),
                null));
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // story.highlight —— 把玩家注意力引到某个坐标（可点击跳转的提示）
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class StoryHighlightHandler : IGameActionHandler
    {
        public string OpType => "story.highlight";
        public ActionPermission Permission => ActionPermission.Narrative;

        public ActionCategory Category => ActionCategory.Narrative;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            "story.highlight{spot=锚点, text=字符串≤120} —— 在屏幕上给出一个可点击跳转的目标提示";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.Map == null) { error = "没有可用地图"; return false; }
            if (!ActionSpot.TryResolve(a, ctx, out _, out error)) return false;
            if (a.ArgString("text").Length > 120) { error = "text 超过 120 字"; return false; }
            return true;
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!ActionSpot.TryResolve(a, ctx, out IntVec3 cell, out error)) return false;

            string text = a.ArgString("text", "这里有什么东西。");
            Messages.Message(new Message(text, MessageTypeDefOf.NeutralEvent,
                new LookTargets(cell, ctx.Map)), false);
            return true;
        }

    }

    // ─────────────────────────────────────────────────────────────────────────
    // story.reward —— 给玩家一份明确奖励（结算用，区别于 thing.spawn 的"随机落地"）
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class StoryRewardHandler : IGameActionHandler
    {
        public string OpType => "story.reward";
        public ActionPermission Permission => ActionPermission.BoundedSpawn;

        public ActionCategory Category => ActionCategory.Narrative;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            "story.reward{thingDef=物品 DefName, count=1~5, quality=可选(awful/poor/normal/good/excellent/masterwork/legendary)} —— " +
            "给玩家的明确奖励，落在基地中心附近";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.Map == null) { error = "没有可用地图"; return false; }

            string defName = a.ArgString("thingDef");
            if (defName.Length == 0) { error = "缺少 thingDef"; return false; }
            if (DefDatabase<ThingDef>.GetNamedSilentFail(defName) == null)
            {
                error = "不存在的 ThingDef：" + defName;
                return false;
            }

            int count = a.ArgInt("count", 1);
            if (count < 1 || count > 5) { error = "count 必须在 1..5"; return false; }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Spawn, count, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(a.ArgString("thingDef"));
            if (def == null || ctx.Map == null) { error = "没有可用地图或定义"; return false; }

            int count = System.Math.Max(1, System.Math.Min(5, a.ArgInt("count", 1)));
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Spawn, count, out error)) return false;

            QualityCategory quality;
            bool hasQuality = System.Enum.TryParse(a.ArgString("quality"), true, out quality);

            for (int i = 0; i < count; i++)
            {
                Thing thing = ThingMaker.MakeThing(def);
                if (hasQuality)
                {
                    CompQuality comp = thing.TryGetComp<CompQuality>();
                    if (comp != null) comp.SetQuality(quality, ArtGenerationContext.Outsider);
                }

                IntVec3 cell = CellFinder.RandomClosewalkCellNear(ctx.Map.Center, ctx.Map, 10);
                if (!GenPlace.TryPlaceThing(thing, cell, ctx.Map, ThingPlaceMode.Near))
                {
                    error = "未能放置奖励物品";
                    return false;
                }
            }
            return true;
        }
    }
}
