using System;
using System.Collections.Generic;
using System.Text.Json;

namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>脚本提出的一条动作。</summary>
    public class PlannedAction
    {
        public string opId = "";
        public string type = "";
        public Dictionary<string, object> args = new Dictionary<string, object>();

        public string ArgString(string name, string fallback = "")
        {
            if (args == null || !args.TryGetValue(name, out object value) || value == null) return fallback;
            return value as string ?? value.ToString();
        }

        public int ArgInt(string name, int fallback = 0)
        {
            if (args == null || !args.TryGetValue(name, out object value) || value == null) return fallback;
            if (value is long l) return (int)l;
            if (value is int i) return i;
            if (value is double d) return (int)Math.Round(d);
            return int.TryParse(value.ToString(), out int parsed) ? parsed : fallback;
        }

        public float ArgFloat(string name, float fallback = 0f)
        {
            if (args == null || !args.TryGetValue(name, out object value) || value == null) return fallback;
            if (value is double d) return (float)d;
            if (value is long l) return l;
            if (value is int i) return i;
            return float.TryParse(value.ToString(), out float parsed) ? parsed : fallback;
        }

        public bool ArgBool(string name, bool fallback = false)
        {
            if (args == null || !args.TryGetValue(name, out object value) || value == null) return fallback;
            if (value is bool b) return b;
            return bool.TryParse(value.ToString(), out bool parsed) ? parsed : fallback;
        }

        public List<object> ArgList(string name)
        {
            if (args == null || !args.TryGetValue(name, out object value)) return null;
            return value as List<object>;
        }

        /// <summary>args 里嵌套的字典（比如 options 里的每一项）。</summary>
        public static Dictionary<string, object> AsDict(object value) =>
            value as Dictionary<string, object>;
    }

    /// <summary>脚本要求的定时信号。</summary>
    public class TimerPlan
    {
        public int delayTicks;
        public string signal = "game_timer_due";
    }

    /// <summary>一个阶段执行完后的结构化结果。</summary>
    public class StagePlan
    {
        public readonly List<PlannedAction> Actions = new List<PlannedAction>();
        public readonly List<TimerPlan> Timers = new List<TimerPlan>();

        /// <summary>脚本返回的 next_state 原文（原样存档，不做解释）。</summary>
        public string NextStateJson = "{}";

        /// <summary>可选：脚本显式声明本阶段名。</summary>
        public string Stage = "";

        public bool Done;
        public string ParseError;
    }

    /// <summary>
    /// 解析脚本回传的 JSON：
    /// { actions = {...}, next_state = {...}, done = false,
    ///   timer = { hours = 6, signal = "..." }, stage = "await_entry" }
    /// 任何字段都不是必需的——空计划就是"这个阶段什么也不做"。
    /// </summary>
    public static class StagePlanParser
    {
        public static StagePlan Parse(string json)
        {
            var plan = new StagePlan();
            if (string.IsNullOrWhiteSpace(json))
            {
                return plan;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            }
            catch (JsonException e)
            {
                plan.ParseError = Loc.Pick("脚本返回的不是合法 JSON：" + e.Message,
                    "script did not return valid JSON: " + e.Message);
                return plan;
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    plan.ParseError = Loc.Pick("脚本必须返回一个 table",
                        "script must return a table");
                    return plan;
                }

                if (root.TryGetProperty("actions", out JsonElement actions) &&
                    actions.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in actions.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;

                        var action = new PlannedAction();
                        action.opId = ReadString(item, "opId");
                        if (action.opId.Length == 0) action.opId = ReadString(item, "op_id");
                        action.type = ReadString(item, "type");

                        if (item.TryGetProperty("args", out JsonElement argsEl) &&
                            argsEl.ValueKind == JsonValueKind.Object)
                        {
                            action.args = LuaJson.Decode(argsEl.GetRawText())
                                as Dictionary<string, object>
                                ?? new Dictionary<string, object>();
                        }

                        if (action.type.Length > 0)
                        {
                            plan.Actions.Add(action);
                        }
                    }
                }

                if (root.TryGetProperty("next_state", out JsonElement nextState))
                {
                    plan.NextStateJson = nextState.GetRawText();
                }

                if (root.TryGetProperty("stage", out JsonElement stageEl) &&
                    stageEl.ValueKind == JsonValueKind.String)
                {
                    plan.Stage = stageEl.GetString() ?? "";
                }

                if (root.TryGetProperty("done", out JsonElement doneEl))
                {
                    plan.Done = doneEl.ValueKind == JsonValueKind.True;
                }

                if (root.TryGetProperty("timer", out JsonElement timerEl) &&
                    timerEl.ValueKind == JsonValueKind.Object)
                {
                    TimerPlan timer = ReadTimer(timerEl);
                    if (timer != null) plan.Timers.Add(timer);
                }

                if (root.TryGetProperty("timers", out JsonElement timersEl) &&
                    timersEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in timersEl.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        TimerPlan timer = ReadTimer(item);
                        if (timer != null) plan.Timers.Add(timer);
                    }
                }
            }

            return plan;
        }

        private static TimerPlan ReadTimer(JsonElement element)
        {
            var timer = new TimerPlan();

            if (element.TryGetProperty("signal", out JsonElement signalEl) &&
                signalEl.ValueKind == JsonValueKind.String)
            {
                timer.signal = signalEl.GetString() ?? "game_timer_due";
            }

            if (element.TryGetProperty("hours", out JsonElement hoursEl) &&
                hoursEl.ValueKind == JsonValueKind.Number)
            {
                timer.delayTicks = (int)Math.Round(hoursEl.GetDouble() * 2500.0);
            }
            else if (element.TryGetProperty("days", out JsonElement daysEl) &&
                     daysEl.ValueKind == JsonValueKind.Number)
            {
                timer.delayTicks = (int)Math.Round(daysEl.GetDouble() * 60000.0);
            }
            else if (element.TryGetProperty("ticks", out JsonElement ticksEl) &&
                     ticksEl.ValueKind == JsonValueKind.Number)
            {
                timer.delayTicks = (int)Math.Round(ticksEl.GetDouble());
            }

            if (timer.delayTicks <= 0) timer.delayTicks = 2500;
            if (timer.delayTicks > 60000 * 5) timer.delayTicks = 60000 * 5;
            return timer;
        }

        private static string ReadString(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value)) return "";
            return value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "") : "";
        }
    }
}
