using HarmonyLib;
using RimWorld;

namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>
    /// 兜底：早期版本允许 pawn.spawn 直接生成任何 PawnKindDef 子类。某些子类
    /// （CreepJoinerFormKindDef 等）要求通过专用生成器/原版事件初始化 form、benefit、downside。
    /// 裸生成的小人 downside 为 null，Pawn_CreepJoinerTracker.CheckTriggersTickInterval
    /// 每 tick 都会 NullReferenceException，日志被同一个 pawn 刷屏。
    ///
    /// 这个 Prefix 只处理初始化不完整（form/downside 为空）的 tracker；正常 CreepJoiner 不受影响。
    /// 老存档里已经存在的坏小人也能靠它立刻止血。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_CreepJoinerTracker), nameof(Pawn_CreepJoinerTracker.TickInterval))]
    internal static class Patch_Pawn_CreepJoinerTracker_TickInterval
    {
        private static bool Prefix(Pawn_CreepJoinerTracker __instance)
        {
            if (__instance == null) return false;
            if (__instance.form != null && __instance.downside != null) return true;

            // 坏 tracker 留着还会在检视/派系变更等路径继续 NRE；直接摘掉，
            // 这个 pawn 就退化成普通角色（它本来就只是个没有剧情数据的半成品）。
            PawnKindSafety.RepairGeneratedPawn(__instance.Pawn);
            return false;
        }
    }
}
