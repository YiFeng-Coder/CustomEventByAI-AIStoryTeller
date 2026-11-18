using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace CustomEventByAI.AIStoryTeller
{
    // ─────────────────────────────────────────────────────────────────────────
    // story.send_letter
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class StorySendLetterHandler : IGameActionHandler
    {
        public string OpType => "story.send_letter";
        public ActionPermission Permission => ActionPermission.Narrative;

        public ActionCategory Category => ActionCategory.Narrative;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick("story.send_letter{title=字符串≤80, text=字符串≤1200, letterDef=可选} —— 给玩家发一封信；title 与 text 不能都空",
                     "story.send_letter{title=string ≤80, text=string ≤1200, letterDef=optional} — sends a letter to the player; title and text must not both be empty");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            string title = a.ArgString("title");
            string text = a.ArgString("text");

            if (title.Length == 0 && text.Length == 0)
            {
                error = Loc.Pick("title 与 text 不能同时为空", "title and text must not both be empty");
                return false;
            }

            if (title.Length > 80) { error = Loc.Pick("title 超过 80 字", "title exceeds 80 characters"); return false; }
            if (text.Length > 1200) { error = Loc.Pick("text 超过 1200 字", "text exceeds 1200 characters"); return false; }
            return true;
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            string title = a.ArgString("title", Loc.Pick("叙事", "Narrative"));
            string text = a.ArgString("text");

            LetterDef def = DefDatabase<LetterDef>.GetNamedSilentFail(a.ArgString("letterDef"));
            if (def == null) def = LetterDefOf.NeutralEvent;

            Find.LetterStack.ReceiveLetter(title, text, def);
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // story.show_choices
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class StoryShowChoicesHandler : IGameActionHandler
    {
        public string OpType => "story.show_choices";
        public ActionPermission Permission => ActionPermission.Narrative;

        public ActionCategory Category => ActionCategory.Narrative;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick("story.show_choices{prompt=字符串≤600, options={{id,label}...}} —— 弹 2~4 个选项；" +
                     "玩家点选后，脚本会在 trigger=\"choice_selected\" 被再次调用，选中项在 ctx.payload.choice 里（等于该选项的 id）",
                     "story.show_choices{prompt=string ≤600, options={{id,label}...}} — pops 2~4 options; " +
                     "after the player picks one, the script is called again with trigger=\"choice_selected\", and the chosen option is in ctx.payload.choice (equal to that option's id)");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
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
            string prompt = a.ArgString("prompt");
            List<object> raw = a.ArgList("options") ?? new List<object>();

            var options = new List<KeyValuePair<string, string>>();
            foreach (object item in raw)
            {
                Dictionary<string, object> dict = PlannedAction.AsDict(item);
                if (dict == null) continue;

                dict.TryGetValue("id", out object idObj);
                dict.TryGetValue("label", out object labelObj);
                options.Add(new KeyValuePair<string, string>(idObj as string, labelObj as string));
            }

            string instanceId = ctx.Instance != null ? ctx.Instance.id : "";
            string title = ctx.Definition != null && !string.IsNullOrEmpty(ctx.Definition.title)
                ? ctx.Definition.title
                : Loc.Pick("抉择", "Choice");

            Find.WindowStack.Add(new Dialog_NarrativeChoice(title, prompt, options, choiceId =>
            {
                var payload = new Dictionary<string, object>
                {
                    { "choice", choiceId },
                    { "instance_id", instanceId }
                };

                EventRuntime.FireSignalTo(instanceId, "choice_selected", payload);
            }));
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // story.remember
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class StoryRememberHandler : IGameActionHandler
    {
        public string OpType => "story.remember";
        public ActionPermission Permission => ActionPermission.Narrative;

        public ActionCategory Category => ActionCategory.Narrative;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick("story.remember{text=字符串≤400} —— 往叙事历史里记一条，后续剧情会看到它（用来埋伏笔）",
                     "story.remember{text=string ≤400} — appends an entry to the narrative history; later story beats will see it (use it to plant foreshadowing)");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            string text = a.ArgString("text");
            if (text.Length == 0) { error = Loc.Pick("text 不能为空", "text must not be empty"); return false; }
            if (text.Length > 400) { error = Loc.Pick("text 超过 400 字", "text exceeds 400 characters"); return false; }
            return true;
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.State == null) { error = Loc.Pick("缺少叙述者状态", "missing narrator state"); return false; }
            ctx.State.PushHistory(new HistoryRecord
            {
                tick = ctx.Now,
                defName = ctx.Instance != null ? ctx.Instance.id : "memory",
                category = "Memory",
                reason = a.ArgString("text"),
                fromAi = true,
                fired = true
            }, AINarratorMod.Settings != null ? AINarratorMod.Settings.maxLogRecords : 200);
            return true;
        }
    }


    // ─────────────────────────────────────────────────────────────────────────
    // incident.fire
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class IncidentFireHandler : IGameActionHandler
    {
        public string OpType => "incident.fire";
        public ActionPermission Permission => ActionPermission.BoundedSpawn;

        public ActionCategory Category => ActionCategory.Spawn;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick("incident.fire{defName=事件 DefName 或 @incident/关键词, points_multiplier=0.3~2.0} —— 立刻触发一个原版事件；" +
                     "defName 必须是真实存在且此刻 CanFireNow 为真的事件名（ThreatBig / ThreatSmall / Misc 是类别，不是事件名），否则整段作废",
                     "incident.fire{defName=incident DefName or @incident/keyword, points_multiplier=0.3~2.0} — fires a vanilla incident immediately; " +
                     "defName must be an existing incident whose CanFireNow is true right now (ThreatBig / ThreatSmall / Misc are categories, not incident names), otherwise the whole batch is discarded");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;

            string defName = a.ArgString("defName");
            if (defName.Length == 0) { error = Loc.Pick("缺少 defName", "missing defName"); return false; }

            IncidentCandidate candidate = CandidateTable.Find(defName);
            if (candidate == null)
            {
                error = CategoryHint(defName) ?? Loc.Pick("候选表里没有 " + defName + "（defName 必须是候选表里的事件名）",
                                                          "no " + defName + " in the candidate table (defName must be an incident name from the candidate table)");
                return false;
            }

            float multiplier = a.ArgFloat("points_multiplier", 1f);
            if (!(multiplier >= 0.3f && multiplier <= 2.0f))
            {
                error = Loc.Pick("points_multiplier 超出 [0.3, 2.0]", "points_multiplier is outside [0.3, 2.0]");
                return false;
            }

            if (!AiIncidentHistory.TryPrepare(candidate, ctx.State, ctx.Map, multiplier, ctx.Now,
                    out IncidentParms parms, out error)) return false;

            return AiIncidentHistory.TryCheckReady(candidate, parms, out error);
        }

        /// <summary>
        /// 模型经常把候选表「事件名 | 类别」里的类别当成事件名填进来（ThreatSmall / ThreatBig / Misc）。
        /// 判错时就地把它翻成人话，并给出该类别下真实存在的几个例子——回喂给编剧后它一次就能改对。
        /// </summary>
        private static string CategoryHint(string defName)
        {
            if (defName != "ThreatBig" && defName != "ThreatSmall" && defName != "Misc") return null;

            var examples = new System.Text.StringBuilder();
            IReadOnlyList<IncidentCandidate> all = CandidateTable.All;
            int taken = 0;
            for (int i = 0; i < all.Count && taken < 3; i++)
            {
                if (all[i].Category != defName) continue;
                if (examples.Length > 0) examples.Append(Loc.IsChinese ? "、" : ", ");
                examples.Append(all[i].DefName);
                taken++;
            }

            string zh = defName + " 是事件类别，不是事件名。defName 要填候选表每行竖线左边那个名字"
                        + (examples.Length > 0 ? "（" + defName + " 下的例子：" + examples + "）" : "");
            string en = defName + " is an incident category, not an incident name. defName must be the name to the left of the pipe in each candidate-table row"
                        + (examples.Length > 0 ? " (examples under " + defName + ": " + examples + ")" : "");
            return Loc.Pick(zh, en);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            IncidentCandidate candidate = CandidateTable.Find(a.ArgString("defName"));
            if (candidate == null) { error = Loc.Pick("事件候选已不存在", "the incident candidate no longer exists"); return false; }

            string reason = a.ArgString("reason", Loc.Pick("由 Lua 剧情触发", "triggered by a Lua story event"));
            if (!AiIncidentHistory.TryPrepare(candidate, ctx.State, ctx.Map, a.ArgFloat("points_multiplier", 1f),
                    ctx.Now, out IncidentParms parms, out error))
            {
                AiIncidentHistory.Record(ctx.State, candidate, ctx.Now, error, false);
                return false;
            }

            var firing = new FiringIncident(candidate.Def, null, parms);
            AiIncidentHistory.Track(firing, ctx.State, candidate, ctx.Now, reason);
            bool fired = false;
            Exception failure = null;
            try
            {
                fired = Find.Storyteller.TryFire(firing);
                error = fired ? null : AiIncidentHistory.Rejected;
                return fired;
            }
            catch (Exception e)
            {
                failure = e;
                throw;
            }
            finally
            {
                // TryFire 的 Harmony 收尾先记录。这里也覆盖补丁未加载的情况。
                AiIncidentHistory.Complete(firing, fired, failure);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // thing.spawn
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class ThingSpawnHandler : IGameActionHandler
    {
        public string OpType => "thing.spawn";
        public ActionPermission Permission => ActionPermission.BoundedSpawn;

        public ActionCategory Category => ActionCategory.Spawn;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick("thing.spawn{thingDef=物品 DefName 或 @item/关键词, count=1~10, handle=可选句柄名} —— 在基地附近生成物品；" +
                     "thingDef 必须是真实存在的物品；之后要用 handle:名字 指向它时，必须在这里写 handle",
                     "thing.spawn{thingDef=item DefName or @item/keyword, count=1~10, handle=optional handle name} — spawns items near the base; " +
                     "thingDef must be a real item; if you later want to refer to it with handle:name, you must set handle here");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            string defName = a.ArgString("thingDef");
            if (defName.Length == 0) { error = Loc.Pick("缺少 thingDef", "missing thingDef"); return false; }

            ThingDef thingDef = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (thingDef == null)
            {
                error = Loc.Pick("不存在的 ThingDef：" + defName, "unknown ThingDef: " + defName);
                return false;
            }

            // 需要材质的定义（墙/家具）不能直接 MakeThing，否则执行到一半会抛异常。
            if (!SpawnSafety.CanMakeThing(thingDef, out error)) return false;

            int count = a.ArgInt("count", 1);
            if (count < 1 || count > 10) { error = Loc.Pick("count 必须在 1..10", "count must be within 1..10"); return false; }
            if (ctx.Map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }
            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Spawn, count, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(a.ArgString("thingDef"));
            if (def == null || ctx.Map == null) { error = Loc.Pick("没有可用地图或定义", "no map or def available"); return false; }

            int count = Math.Max(1, Math.Min(10, a.ArgInt("count", 1)));
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Spawn, count, out error)) return false;

            string handle = a.ArgString("handle");

            for (int i = 0; i < count; i++)
            {
                IntVec3 cell = CellFinder.RandomClosewalkCellNear(ctx.Map.Center, ctx.Map, 12);
                Thing thing = ThingMaker.MakeThing(def);
                if (GenSpawn.Spawn(thing, cell, ctx.Map) == null || !thing.Spawned)
                {
                    error = Loc.Pick("物品生成失败", "failed to spawn the item");
                    return false;
                }

                if (i == 0 && handle.Length > 0) ctx.RegisterHandle(handle, thing);
            }
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // pawn.spawn
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class PawnSpawnHandler : IGameActionHandler
    {
        public string OpType => "pawn.spawn";
        public ActionPermission Permission => ActionPermission.BoundedSpawn;

        public ActionCategory Category => ActionCategory.Spawn;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick("pawn.spawn{pawnKind=角色 DefName 或 @pawnKind/关键词, count=1~3, handle=可选句柄名} —— 在地图边缘生成角色（无派系，不会自动加入玩家）；" +
                     "pawnKind 必须是普通 PawnKindDef（不能是 CreepJoiner 等特殊子类）；之后要用 handle:名字 指向它时，必须在这里写 handle",
                     "pawn.spawn{pawnKind=pawn DefName or @pawnKind/keyword, count=1~3, handle=optional handle name} — spawns pawns at the map edge (no faction, they will not join the player); " +
                     "pawnKind must be a plain PawnKindDef (not a special subclass such as CreepJoiner); if you later want to refer to it with handle:name, you must set handle here");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            string kindName = a.ArgString("pawnKind");
            if (kindName.Length == 0) { error = Loc.Pick("缺少 pawnKind", "missing pawnKind"); return false; }
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(kindName);
            if (kind == null)
            {
                error = Loc.Pick("不存在的 PawnKindDef：" + kindName, "unknown PawnKindDef: " + kindName);
                return false;
            }

            if (!PawnKindSafety.IsPlainPawnKind(kind))
            {
                error = PawnKindSafety.RejectSpecialPawnKind(kindName, kind);
                return false;
            }

            int count = a.ArgInt("count", 1);
            if (count < 1 || count > 3) { error = Loc.Pick("count 必须在 1..3", "count must be within 1..3"); return false; }
            if (ctx.Map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }
            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Spawn, count, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(a.ArgString("pawnKind"));
            if (kind == null || ctx.Map == null) { error = Loc.Pick("没有可用地图或定义", "no map or def available"); return false; }
            if (!PawnKindSafety.IsPlainPawnKind(kind))
            {
                error = PawnKindSafety.RejectSpecialPawnKind(a.ArgString("pawnKind"), kind);
                return false;
            }

            int count = Math.Max(1, Math.Min(3, a.ArgInt("count", 1)));
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Spawn, count, out error)) return false;
            string handle = a.ArgString("handle");

            for (int i = 0; i < count; i++)
            {
                try
                {
                    IntVec3 cell = CellFinder.RandomEdgeCell(ctx.Map);
                    Pawn pawn = PawnGenerator.GeneratePawn(kind, null);
                    PawnKindSafety.RepairGeneratedPawn(pawn);
                    if (pawn == null || GenSpawn.Spawn(pawn, cell, ctx.Map) == null || !pawn.Spawned)
                    {
                        error = Loc.Pick("角色生成失败", "failed to spawn the pawn");
                        return false;
                    }

                    if (i == 0 && handle.Length > 0) ctx.RegisterHandle(handle, pawn);
                }
                catch (Exception e)
                {
                    error = Loc.Pick("角色生成失败：" + e.Message, "failed to spawn the pawn: " + e.Message);
                    return false;
                }
            }
            return true;
        }
    }
}