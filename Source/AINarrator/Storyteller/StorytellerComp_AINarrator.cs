using System.Collections.Generic;
using RimWorld;
using Verse;

namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>
    /// AI 叙述者的入口组件。
    ///
    /// 设计约束（改动前先读）：
    ///  1. 本组件是"无状态壳"：任何需要存档的状态都在 NarratorWorldComponent 里。
    ///  2. MakeIntervalIncidents 是惰性迭代器，只允许 O(1) 取值，绝不等待网络或执行 Lua。
    ///  3. 只对「玩家基地地图」发事件。
    ///  4. 到期后先出队再复查，由 Storyteller 执行并按实际结果记账。
    /// </summary>
    public class StorytellerComp_AINarrator : StorytellerComp
    {
        public StorytellerCompProperties_AINarrator Props =>
            (StorytellerCompProperties_AINarrator)props;

        public override IEnumerable<FiringIncident> MakeIntervalIncidents(IIncidentTarget target)
        {
            if (!(target is Map map)) yield break;

            Map home = Find.AnyPlayerHomeMap;
            if (home == null || map.uniqueID != home.uniqueID) yield break;

            NarratorWorldComponent comp = NarratorWorldComponent.Instance;
            if (comp == null) yield break;

            NarratorState state = comp.State;
            if (state == null || state.pending.Count == 0) yield break;

            PendingEventRecord pending = state.pending[0];
            if (pending == null)
            {
                state.pending.Clear();
                yield break;
            }

            if (pending.mapId != map.uniqueID)
            {
                // 换了地图，旧事件作废。
                state.pending.Clear();
                yield break;
            }

            int now = Find.TickManager.TicksGame;
            if (now < pending.fireAtTick) yield break;

            IncidentCandidate candidate = CandidateTable.Find(pending.defName);
            if (candidate == null || candidate.Def == null || candidate.Def.Worker == null)
            {
                state.pending.Clear();
                yield break;
            }

            AINarratorSettings settings = AINarratorMod.Settings;

            // 先出队：之后无论成功、失败还是异常，这个 pending 都不会再被处理第二次。
            state.pending.RemoveAt(0);

            if (!AiIncidentHistory.TryPrepare(candidate, state, map, pending.pointsMultiplier, now,
                    out IncidentParms parms, out string failReason) ||
                !AiIncidentHistory.TryCheckReady(candidate, parms, out failReason))
            {
                AiIncidentHistory.Record(state, candidate, now, failReason, false);
                Verse.Log.Warning(AINarratorMod.LogPrefix + "pending event dropped (" + candidate.DefName
                                  + "): " + failReason);
                yield break;
            }

            var firing = new FiringIncident(candidate.Def, this, parms);
            AiIncidentHistory.Track(firing, state, candidate, now, pending.reason, pending.announce);

            if (settings != null && settings.debugLogging)
            {
                Verse.Log.Message(AINarratorMod.LogPrefix + "firing " + candidate.DefName);
            }

            yield return firing;
        }
    }
}
