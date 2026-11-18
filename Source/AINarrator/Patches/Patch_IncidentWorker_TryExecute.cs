using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>
    /// 记录"原版真正执行了什么事件"，用于历史与张力。
    ///
    /// 这是纯观测型 Postfix：不修改返回值、不拦截、不影响任何其它模组的事件。
    /// 思路与 RimMind-Storyteller 的 Patch_IncidentWorker_TryExecute 一致（MIT）。
    /// </summary>
    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute))]
    public static class Patch_IncidentWorker_TryExecute
    {
        public static void Postfix(IncidentWorker __instance, IncidentParms parms, bool __result)
        {
            if (!__result || __instance == null || __instance.def == null) return;
            if (AiIncidentHistory.IsTracked(__instance.def, parms)) return;

            try
            {
                NarratorWorldComponent comp = NarratorWorldComponent.Instance;
                if (comp == null) return;

                NarratorState state = comp.State;
                if (state == null) return;

                AINarratorSettings settings = AINarratorMod.Settings;
                int maxRecords = settings != null ? settings.maxLogRecords : 200;

                state.PushHistory(new HistoryRecord
                {
                    tick = Find.TickManager.TicksGame,
                    defName = __instance.def.defName,
                    category = __instance.def.category != null ? __instance.def.category.defName : "",
                    reason = Loc.Pick("(原版触发)", "(vanilla trigger)"),
                    fromAi = false,
                    fired = true
                }, maxRecords);

            }
            catch (System.Exception e)
            {
                Verse.Log.Warning(AINarratorMod.LogPrefix + "incident record failed: " + e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(Storyteller), nameof(Storyteller.TryFire), typeof(FiringIncident), typeof(bool))]
    internal static class Patch_Storyteller_TryFire
    {
        public static void Finalizer(FiringIncident fi, bool __result, Exception __exception) =>
            AiIncidentHistory.Complete(fi, __result, __exception);
    }

    /// <summary>把 AI 原版事件的来源与实际发射参数绑定，记账一次。</summary>
    internal static class AiIncidentHistory
    {
        internal static string Rejected => Loc.Pick("游戏未能触发该事件（CanFireNow 或 TryExecute 为假）",
            "the game could not fire this incident (CanFireNow or TryExecute returned false)");

        private sealed class Attempt
        {
            public NarratorState State;
            public IncidentCandidate Candidate;
            public int Tick;
            public string Reason;
            public string Announce;
        }

        private static readonly ConditionalWeakTable<IncidentParms, Attempt> Attempts =
            new ConditionalWeakTable<IncidentParms, Attempt>();

        public static bool TryPrepare(IncidentCandidate candidate, NarratorState state, Map map,
            float multiplier, int now, out IncidentParms parms, out string error)
        {
            parms = null;
            if (!BudgetPolicy.CanUse(candidate, state, now, AINarratorMod.Settings, out error)) return false;
            if (map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }

            try
            {
                if (candidate.Def.requireColonistsPresent && map.mapPawns.FreeColonistsSpawnedCount == 0)
                {
                    error = Loc.Pick("当前地图没有该事件所需的殖民者",
                        "the current map has none of the colonists this incident requires");
                    return false;
                }

                parms = StorytellerUtility.DefaultParmsNow(candidate.Def.category, map);
                if (parms == null) { error = Loc.Pick("无法构造事件参数", "could not build incident parms"); return false; }
                parms.points = StorytellerUtility.DefaultThreatPointsNow(map) * multiplier;
                return true;
            }
            catch (Exception e)
            {
                error = Loc.Pick("构造事件参数失败：" + e.Message,
                    "building incident parms failed: " + e.Message);
                return false;
            }
        }

        public static bool TryCheckReady(IncidentCandidate candidate, IncidentParms parms, out string error)
        {
            error = null;
            try
            {
                if (candidate.Def.Worker.CanFireNow(parms)) return true;
                error = Loc.Pick(candidate.DefName + " 当前不可触发",
                    candidate.DefName + " cannot fire right now");
                return false;
            }
            catch (Exception e)
            {
                error = Loc.Pick("CanFireNow 异常：" + e.Message,
                    "CanFireNow threw: " + e.Message);
                return false;
            }
        }

        public static void Track(FiringIncident firing, NarratorState state, IncidentCandidate candidate,
            int tick, string reason, string announce = null) =>
            Attempts.Add(firing.parms, new Attempt
            {
                State = state,
                Candidate = candidate,
                Tick = tick,
                Reason = reason,
                Announce = announce ?? ""
            });

        public static bool IsTracked(IncidentDef def, IncidentParms parms) =>
            parms != null && Attempts.TryGetValue(parms, out Attempt attempt) && ReferenceEquals(def, attempt.Candidate.Def);

        /// <summary>
        /// 这条原版事件是不是 AI 批准并发射的；是的话取出模型写的描述。
        /// 只会在 TryExecute 执行期间命中：Complete 之后 Attempt 会被移除。
        /// </summary>
        public static bool TryGetAnnounce(IncidentParms parms, out string announce)
        {
            announce = null;
            if (parms == null || !Attempts.TryGetValue(parms, out Attempt attempt)) return false;
            if (string.IsNullOrWhiteSpace(attempt.Announce)) return false;
            announce = attempt.Announce;
            return true;
        }

        public static void Complete(FiringIncident firing, bool fired, Exception exception)
        {
            if (firing?.parms == null || !Attempts.TryGetValue(firing.parms, out Attempt attempt) ||
                !ReferenceEquals(firing.def, attempt.Candidate.Def) || !Attempts.Remove(firing.parms))
                return;

            string reason = exception == null
                ? (fired ? attempt.Reason : Rejected)
                : Loc.Pick("事件执行异常：" + exception.Message,
                    "incident execution threw: " + exception.Message);
            Record(attempt.State, attempt.Candidate, attempt.Tick, reason, fired && exception == null);
        }

        public static void Record(NarratorState state, IncidentCandidate candidate, int now, string reason, bool fired)
        {
            if (state == null) return;
            try
            {
                int maxRecords = AINarratorMod.Settings?.maxLogRecords ?? 200;
                state.PushHistory(new HistoryRecord
                {
                    tick = now,
                    defName = candidate.DefName,
                    category = candidate.Category,
                    reason = reason,
                    fromAi = true,
                    fired = fired
                }, maxRecords);
            }
            catch (Exception e)
            {
                Verse.Log.Warning(AINarratorMod.LogPrefix + "incident record failed: " + e.Message);
            }
        }
    }
}
