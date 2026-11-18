using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>已批准、等待到点发射的事件。</summary>
    public class PendingEventRecord
    {
        public string defName = "";
        public string category = "";
        public float pointsMultiplier = 1f;
        public int fireAtTick;
        public int createdTick;
        public string reason = "";

        /// <summary>模型给这次事件写的一句/一段描述；发射时用来替换原版事件信件的正文。</summary>
        public string announce = "";

        public int mapId = -1;

        [JsonIgnore]
        public string Label => (string.IsNullOrEmpty(defName) ? "(none)" : defName);
    }

    /// <summary>已经发生过的事件（无论由 AI 还是原版触发）。</summary>
    public class HistoryRecord
    {
        public int tick;
        public string defName = "";
        public string category = "";
        public string reason = "";
        public bool fromAi;
        public bool fired;
    }

    /// <summary>一条叙事目标（story.objective）。用消息提醒，不做常驻 HUD。</summary>
    public class ObjectiveRecord
    {
        public string id = "";
        public string text = "";
        public int createdTick;
        public int dueTick;              // 0 = 无期限
        public int lastReminderTick;
        public bool done;
    }

    /// <summary>
    /// 叙述者的全部持久状态。整个对象被序列化成一个 JSON 字符串存进存档。
    /// 新增字段时 schemaVersion +1，并在 <see cref="Migrate"/> 里补默认值。
    /// </summary>
    public class NarratorState
    {
        public const int CurrentSchemaVersion = 4;

        public int schemaVersion = CurrentSchemaVersion;

        // 节奏
        public int lastRequestTick;
        public int lastFailTick;
        public int failStreak;

        // 配额
        public int callDay = -1;
        public int callsToday;
        public int luaEventsToday;

        // 最小间隔用真实时间（成本控制），单位毫秒 since boot
        public long lastRequestRealtimeMs;

        // 内容
        public List<PendingEventRecord> pending = new List<PendingEventRecord>();
        public List<HistoryRecord> history = new List<HistoryRecord>();

        // 剧情事件（P2）
        public List<EventDefinitionRecord> defs = new List<EventDefinitionRecord>();
        public List<EventInstanceRecord> instances = new List<EventInstanceRecord>();
        public int eventSeq;

        // 叙事目标（story.objective）
        public List<ObjectiveRecord> objectives = new List<ObjectiveRecord>();
        public int objectiveSeq;

        [JsonIgnore]
        public string LastEventLabel =>
            history.Count > 0 ? history[history.Count - 1].defName : "";

        /// <summary>给 prompt 用的"最近 N 条"摘要，纯文本、定长。</summary>
        public string RecentSummary(int max)
        {
            if (history.Count == 0 || max <= 0) return "";
            var sb = new StringBuilder(256);
            int start = history.Count - max;
            if (start < 0) start = 0;
            for (int i = start; i < history.Count; i++)
            {
                HistoryRecord r = history[i];
                sb.Append(Loc.Pick("- 第" + (r.tick / 60000) + "天 " + r.defName
                        + "（" + (string.IsNullOrEmpty(r.category) ? "?" : r.category) + "）"
                        + (r.fired ? "" : " [未执行]"),
                    "- day " + (r.tick / 60000) + " " + r.defName
                        + " (" + (string.IsNullOrEmpty(r.category) ? "?" : r.category) + ")"
                        + (r.fired ? "" : " [not fired]")))
                  .Append('\n');
            }

            return sb.ToString();
        }

        public void PushHistory(HistoryRecord record, int maxRecords)
        {
            if (record == null) return;
            history.Add(record);
            int overflow = history.Count - maxRecords;
            if (overflow > 0) history.RemoveRange(0, overflow);
        }

        public void RemoveExpiredPending(int nowTick)
        {
            // 待发事件最多保留 1 游戏天，过期就丢，避免读档后突然炸出一堆旧事件。
            pending.RemoveAll(p => p == null || nowTick - p.createdTick > 60000);
        }

        public static NarratorState FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new NarratorState();
            try
            {
                NarratorState state = JsonSerializer.Deserialize<NarratorState>(json, Options);
                if (state == null) return new NarratorState();
                return Migrate(state);
            }
            catch (JsonException e)
            {
                Verse.Log.Error(AINarratorMod.LogPrefix + "state json corrupt, resetting: " + e.Message);
                return new NarratorState();
            }
        }

        public string ToJson()
        {
            try
            {
                return JsonSerializer.Serialize(this, Options);
            }
            catch (JsonException e)
            {
                Verse.Log.Error(AINarratorMod.LogPrefix + "state serialize failed: " + e.Message);
                return "";
            }
        }

        private static NarratorState Migrate(NarratorState state)
        {
            if (state.pending == null) state.pending = new List<PendingEventRecord>();
            if (state.history == null) state.history = new List<HistoryRecord>();
            if (state.defs == null) state.defs = new List<EventDefinitionRecord>();
            if (state.instances == null) state.instances = new List<EventInstanceRecord>();
            if (state.objectives == null) state.objectives = new List<ObjectiveRecord>();

            for (int i = 0; i < state.pending.Count; i++)
            {
                PendingEventRecord pending = state.pending[i];
                if (pending == null) continue;
                if (pending.announce == null) pending.announce = "";
            }

            for (int i = 0; i < state.instances.Count; i++)
            {
                EventInstanceRecord instance = state.instances[i];
                if (instance == null) continue;
                if (instance.handles == null) instance.handles = new List<HandleRecord>();
                if (instance.timers == null) instance.timers = new List<EventTimerRecord>();
                if (instance.executedOps == null) instance.executedOps = new List<string>();
                if (instance.lastActions == null) instance.lastActions = new List<ActionOutcomeRecord>();
                if (state.schemaVersion < 3 && !instance.done)
                {
                    instance.awaitingChoice = instance.lastActions.Exists(a => a != null && a.ok &&
                        (a.type == "story.show_choices" || a.type == "story.choice_letter"));
                }
            }
            state.schemaVersion = CurrentSchemaVersion;
            return state;
        }

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            IncludeFields = true,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
    }
}
