using RimWorld;
using Verse;

namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>
    /// PawnKindDef 的通用安全边界。
    ///
    /// 规则不按 defName 列举：只有"普通 PawnKindDef"（运行时类型正好是 PawnKindDef）
    /// 才允许走 pawn.spawn / map.corpse 的裸 PawnGenerator 生成。
    /// 任何 PawnKindDef 子类都视为"需要专用生成器"的特殊模板：
    /// CreepJoinerFormKindDef 只是当前遇到的第一个，以后新增的潜行者 / 隐身者 /
    /// 其它自定义子类会自动走同一条拒绝路径，不需要再改一次名单。
    ///
    /// 另外提供生成后的兜底：即使某个普通模板因为模组/存档原因带出了初始化
    /// 不完整的 tracker，也在落地前摘掉，避免原版每 tick 抛 NRE。
    /// </summary>
    internal static class PawnKindSafety
    {
        internal static bool IsPlainPawnKind(PawnKindDef kind) =>
            kind != null && kind.GetType() == typeof(PawnKindDef);

        internal static string RejectSpecialPawnKind(string defName, PawnKindDef kind)
        {
            string typeName = kind != null ? kind.GetType().Name : Loc.Pick("未知子类", "unknown subclass");
            return Loc.Pick(
                defName + " 使用特殊 PawnKindDef 子类 " + typeName
                + "，需要原版事件或专用生成器初始化；pawn.spawn / map.corpse 只接受普通 PawnKindDef。"
                + "请改用普通 pawnKind，或用 incident.fire 触发原版事件",
                defName + " uses the special PawnKindDef subclass " + typeName
                + ", which must be initialized by a vanilla incident or a dedicated generator; "
                + "pawn.spawn / map.corpse only accept a plain PawnKindDef. "
                + "Use a plain pawnKind instead, or fire the vanilla incident with incident.fire");
        }

        /// <summary>
        /// 生成后的通用清理：CreepJoiner tracker 只有在 form/downside 都完整时才保留。
        /// 不完整说明它不是通过 CreepJoinerUtility 生成的，留着会在 Tick / 检视 / 派系变更时 NRE。
        /// </summary>
        internal static void RepairGeneratedPawn(Pawn pawn)
        {
            if (pawn == null) return;

            Pawn_CreepJoinerTracker tracker = pawn.creepjoiner;
            if (tracker == null) return;
            if (tracker.form != null && tracker.downside != null) return;

            pawn.creepjoiner = null;
            if (AINarratorMod.Settings != null && AINarratorMod.Settings.debugLogging)
            {
                string label = string.IsNullOrEmpty(pawn.LabelShort) ? pawn.ThingID : pawn.LabelShort;
                Verse.Log.Warning(AINarratorMod.LogPrefix + "removed incomplete "
                                  + tracker.GetType().Name + " from " + label
                                  + " (kind=" + (pawn.kindDef != null ? pawn.kindDef.defName : "?") + ")");
            }
        }
    }
}
