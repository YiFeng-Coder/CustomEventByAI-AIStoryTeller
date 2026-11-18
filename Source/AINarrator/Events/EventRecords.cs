using System.Collections.Generic;

namespace Stellaris.AI
{
    /// <summary>AI 写好的一份事件脚本（可复用）。</summary>
    public class EventDefinitionRecord
    {
        public string id = "";
        public string title = "";
        public string script = "";
        public string hash = "";
        public int createdTick;
        public int schemaVersion = 1;
    }

    /// <summary>某个具体事件的定时器。</summary>
    public class EventTimerRecord
    {
        public int atTick;
        public string signal = "game_timer_due";
    }

    /// <summary>上一阶段某条动作的执行回执（喂回脚本的 ctx.last）。</summary>
    public class ActionOutcomeRecord
    {
        public string type = "";
        public string opId = "";
        public bool ok;
        public string error = "";
    }

    /// <summary>某一次具体发生的事件（一次定义可以有多次实例）。</summary>
    public class EventInstanceRecord
    {
        public string id = "";
        public string defId = "";
        public string stageName = "";

        /// <summary>脚本自己维护的局部状态（原样 JSON，不解释）。</summary>
        public string stateJson = "{}";

        /// <summary>已执行的动作 opId，用于幂等（上限 64，超出丢最旧）。</summary>
        public List<string> executedOps = new List<string>();

        public List<EventTimerRecord> timers = new List<EventTimerRecord>();

        /// <summary>脚本声明的句柄（"p1" → ThingID），随实例存档。</summary>
        public List<HandleRecord> handles = new List<HandleRecord>();

        /// <summary>上一阶段的动作回执（上限 8 条），下一阶段作为 ctx.last 传回脚本。</summary>
        public List<ActionOutcomeRecord> lastActions = new List<ActionOutcomeRecord>();

        // 预算消耗
        public int spentSpawn;
        public int spentDanger;
        public int spentPawnEdit;
        public int spentSocial;
        public int spentWorld;

        public int createdTick;
        public int lastTick;
        public bool forced;
        public bool awaitingChoice;
        public bool done;
        public string lastError = "";

        /// <summary>幂等键按「阶段 + opId」作用域：不同分支复用同一个 opId 不会互相挡住。</summary>
        private static string OpKey(string scope, string opId) => (scope ?? "") + "|" + opId;

        public bool HasOp(string scope, string opId) =>
            !string.IsNullOrEmpty(opId) && executedOps.Contains(OpKey(scope, opId));

        public void MarkOp(string scope, string opId)
        {
            if (string.IsNullOrEmpty(opId)) return;

            string key = OpKey(scope, opId);
            if (executedOps.Contains(key)) return;

            executedOps.Add(key);
            if (executedOps.Count > 64) executedOps.RemoveAt(0);
        }
    }
}
