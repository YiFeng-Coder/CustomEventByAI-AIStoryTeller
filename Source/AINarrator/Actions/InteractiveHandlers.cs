using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Stellaris.AI
{
    // ─────────────────────────────────────────────────────────────────────────
    // story.objective —— 给长线事件一个"目标 + 期限"，并每天提醒一次
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class StoryObjectiveHandler : IGameActionHandler
    {
        public string OpType => "story.objective";
        public ActionPermission Permission => ActionPermission.Narrative;
        public ActionCategory Category => ActionCategory.Interactive;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick("story.objective{text=字符串≤120, days=0~10} —— 记一条叙事目标（days>0 时到期提醒；每天提醒一次）",
                     "story.objective{text=string ≤120, days=0~10} — records a narrative objective (reminds on expiry when days>0; reminds once per day)");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;

            string text = a.ArgString("text");
            if (text.Length == 0) { error = Loc.Pick("text 不能为空", "text must not be empty"); return false; }
            if (text.Length > 120) { error = Loc.Pick("text 超过 120 字", "text exceeds 120 characters"); return false; }

            int days = a.ArgInt("days", 0);
            if (days < 0 || days > 10) { error = Loc.Pick("days 必须在 0..10", "days must be within 0..10"); return false; }

            return true;
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.State == null) { error = Loc.Pick("缺少叙述者状态", "missing narrator state"); return false; }

            int days = Math.Max(0, Math.Min(10, a.ArgInt("days", 0)));
            var record = new ObjectiveRecord
            {
                id = "obj_" + (++ctx.State.objectiveSeq),
                text = a.ArgString("text"),
                createdTick = ctx.Now,
                dueTick = days > 0 ? ctx.Now + days * 60000 : 0
            };

            ctx.State.objectives.Add(record);
            while (ctx.State.objectives.Count > 6) ctx.State.objectives.RemoveAt(0);

            string title = ctx.Definition != null && !string.IsNullOrEmpty(ctx.Definition.title)
                ? ctx.Definition.title
                : Loc.Pick("新的目标", "New Objective");

            Find.LetterStack.ReceiveLetter(title, record.text, LetterDefOf.NeutralEvent);

            ctx.State.PushHistory(new HistoryRecord
            {
                tick = ctx.Now,
                defName = record.id,
                category = "Objective",
                reason = record.text,
                fromAi = true,
                fired = true
            }, AINarratorMod.Settings != null ? AINarratorMod.Settings.maxLogRecords : 200);
            return true;
        }
    }

    /// <summary>
    /// 目标的每日提醒与到期结算。
    ///
    /// 为什么不用 Alert：1.7 的 AlertsReadout 已经没有公开的注册入口（Register/AllAlerts
    /// 全部不存在），硬塞进去只能反射私有字段——比"一天一条消息"更容易炸。
    /// 所以这里选了可见、可停、可测的做法：到期一条消息，之后每天最多一条提醒。
    /// </summary>
    public static class ObjectiveTracker
    {
        public const int ReminderIntervalTicks = 60000;   // 1 游戏日

        public static void Tick(NarratorState state, int now)
        {
            if (state == null || state.objectives == null || state.objectives.Count == 0) return;

            for (int i = state.objectives.Count - 1; i >= 0; i--)
            {
                ObjectiveRecord record = state.objectives[i];
                if (record == null || record.done) continue;

                if (record.dueTick > 0 && now >= record.dueTick)
                {
                    record.done = true;
                    Messages.Message(Loc.Pick("目标到期：" + record.text, "Objective expired: " + record.text), MessageTypeDefOf.NeutralEvent, false);
                    continue;
                }

                if (now - record.lastReminderTick < ReminderIntervalTicks) continue;
                record.lastReminderTick = now;

                string suffix = record.dueTick > 0
                    ? Loc.Pick("（还剩 " + Math.Max(1, (record.dueTick - now) / 60000) + " 天）",
                               " (" + Math.Max(1, (record.dueTick - now) / 60000) + " days left)")
                    : "";
                Messages.Message(Loc.Pick("叙事目标：" + record.text + suffix, "Narrative objective: " + record.text + suffix), MessageTypeDefOf.NeutralEvent, false);
            }
        }

        public static string ActiveSummary(NarratorState state)
        {
            if (state == null || state.objectives == null) return "";
            for (int i = state.objectives.Count - 1; i >= 0; i--)
            {
                if (state.objectives[i] != null && !state.objectives[i].done) return state.objectives[i].text;
            }

            return "";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // story.focus —— 镜头 + 音效（零叙事风险的"演出"）
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class StoryFocusHandler : IGameActionHandler
    {
        public string OpType => "story.focus";
        public ActionPermission Permission => ActionPermission.Narrative;
        public ActionCategory Category => ActionCategory.Interactive;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick("story.focus{spot=锚点} —— 把镜头跳到该位置（纯粹的演出，没有副作用）",
                     "story.focus{spot=anchor} — jumps the camera to that position (pure presentation, no side effects)");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            return ActionSpot.TryResolve(a, ctx, out _, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!ActionSpot.TryResolve(a, ctx, out IntVec3 cell, out error)) return false;
            CameraJumper.TryJump(cell, ctx.Map);
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // story.choice_letter —— 走原版信件栏的选项（不弹窗打断）
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class StoryChoiceLetterHandler : IGameActionHandler
    {
        public string OpType => "story.choice_letter";
        public ActionPermission Permission => ActionPermission.Narrative;
        public ActionCategory Category => ActionCategory.Interactive;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick("story.choice_letter{title=≤60, prompt=≤600, options={id,label}2~4 个} —— 选项放在信件栏里，不弹窗；" +
                     "选后回调 trigger=\"choice_selected\"，选中项在 ctx.payload.choice",
                     "story.choice_letter{title=≤60, prompt=≤600, options={id,label} 2~4 entries} — options live in the letter stack instead of a popup; " +
                     "after a choice, the callback fires with trigger=\"choice_selected\" and the chosen option is in ctx.payload.choice");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;

            string title = a.ArgString("title");
            if (title.Length > 60) { error = Loc.Pick("title 超过 60 字", "title exceeds 60 characters"); return false; }

            string prompt = a.ArgString("prompt");
            if (prompt.Length > 600) { error = Loc.Pick("prompt 超过 600 字", "prompt exceeds 600 characters"); return false; }

            List<object> options = a.ArgList("options");
            if (options == null || options.Count < 2) { error = Loc.Pick("至少需要 2 个选项", "at least 2 options are required"); return false; }
            if (options.Count > 4) { error = Loc.Pick("最多 4 个选项", "at most 4 options are allowed"); return false; }

            foreach (object item in options)
            {
                Dictionary<string, object> dict = PlannedAction.AsDict(item);
                if (dict == null) { error = Loc.Pick("options 的每一项必须是 table", "each entry in options must be a table"); return false; }

                dict.TryGetValue("id", out object idObj);
                dict.TryGetValue("label", out object labelObj);
                string id = idObj as string;
                string label = labelObj as string;

                if (string.IsNullOrEmpty(id)) { error = Loc.Pick("选项缺少 id", "option is missing id"); return false; }
                if (string.IsNullOrEmpty(label)) { error = Loc.Pick("选项缺少 label", "option is missing label"); return false; }
                if (label.Length > 40) { error = Loc.Pick("选项文字超过 40 字", "option label exceeds 40 characters"); return false; }
            }

            return true;
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            string instanceId = ctx.Instance != null ? ctx.Instance.id : "";
            var letter = new NarrativeChoiceLetter
            {
                def = LetterDefOf.NeutralEvent,
                Label = ctx.Definition != null && !string.IsNullOrEmpty(ctx.Definition.title)
                    ? ctx.Definition.title
                    : a.ArgString("title", Loc.Pick("抉择", "Choice")),
                Text = a.ArgString("prompt", Loc.Pick("需要你做一个决定。", "You need to make a decision.")),
                lookTargets = LookTargets.Invalid,
                instanceId = instanceId
            };

            List<object> raw = a.ArgList("options") ?? new List<object>();
            foreach (object item in raw)
            {
                Dictionary<string, object> dict = PlannedAction.AsDict(item);
                if (dict == null) continue;

                dict.TryGetValue("id", out object idObj);
                dict.TryGetValue("label", out object labelObj);

                string id = idObj as string;
                string label = labelObj as string;
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(label)) continue;

                letter.optionIds.Add(id);
                letter.optionLabels.Add(label);
            }

            // 直接 new 出来的 Letter 没有经过 LetterMaker，ID 会是 0；
            // 归档/去重都依赖 ID，这里补一个唯一值。
            letter.ID = Find.UniqueIDsManager.GetNextLetterID();
            Find.LetterStack.ReceiveLetter(letter);
            return true;
        }
    }

    /// <summary>
    /// 信件栏里的抉择。
    ///
    /// 为什么点开后用我们自己的弹窗列选项：原版 <see cref="ChoiceLetter"/> 的选项面板
    /// 依赖内部的 DiaNode 构造，在 1.7 上行为不可控（实测出现过"点开信但没有任何按钮"）。
    /// 这里直接复用 0.5.0 起就在用的 <see cref="Dialog_NarrativeChoice"/>：
    /// 信还是那封信，点开后选项一定在。
    ///
    /// Scribe 只存 instanceId + 选项文本，读档后照样能把选择投回事件。
    /// </summary>
    public sealed class NarrativeChoiceLetter : ChoiceLetter
    {
        public string instanceId = "";
        public List<string> optionIds = new List<string>();
        public List<string> optionLabels = new List<string>();

        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                var list = new List<DiaOption>();
                for (int i = 0; i < optionIds.Count && i < optionLabels.Count; i++)
                {
                    string id = optionIds[i];
                    var option = new DiaOption(optionLabels[i]);
                    option.action = () =>
                    {
                        Find.LetterStack.RemoveLetter(this);

                        var payload = new Dictionary<string, object>
                        {
                            { "choice", id },
                            { "instance_id", instanceId }
                        };

                        EventRuntime.FireSignalTo(instanceId, "choice_selected", payload);
                    };
                    option.resolveTree = true;
                    list.Add(option);
                }

                return list;
            }
        }

        protected override string GetMouseoverText() =>
            string.IsNullOrEmpty(Label) ? Loc.Pick("这是一次抉择", "This is a choice") : Label;

        /// <summary>点开信件：用我们自己的弹窗列出选项（不依赖原版 ChoiceLetter 的选项面板）。</summary>
        public override void OpenLetter()
        {
            var options = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < optionIds.Count && i < optionLabels.Count; i++)
            {
                options.Add(new KeyValuePair<string, string>(optionIds[i], optionLabels[i]));
            }

            if (options.Count == 0)
            {
                // 理论上不会发生（Validate 要求 2~4 个选项），兜底成一条"知道了"。
                options.Add(new KeyValuePair<string, string>("ok", Loc.Pick("知道了", "Understood")));
            }

            NarrativeChoiceLetter letter = this;
            string instance = instanceId;

            Find.WindowStack.Add(new Dialog_NarrativeChoice(Label, Text, options, choice =>
            {
                Find.LetterStack.RemoveLetter(letter);

                var payload = new Dictionary<string, object>
                {
                    { "choice", choice },
                    { "instance_id", instance }
                };

                EventRuntime.FireSignalTo(instance, "choice_selected", payload);
            }));
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref instanceId, "instanceId");
            Scribe_Collections.Look(ref optionIds, "optionIds", LookMode.Value);
            Scribe_Collections.Look(ref optionLabels, "optionLabels", LookMode.Value);

            if (optionIds == null) optionIds = new List<string>();
            if (optionLabels == null) optionLabels = new List<string>();
        }
    }
}