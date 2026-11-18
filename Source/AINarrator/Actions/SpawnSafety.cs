using RimWorld;
using Verse;

namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>
    /// "能不能直接 MakeThing/生成"的公共检查。
    ///
    /// 踩过的坑：thing.spawn / story.reward / map.drop_pod 都直接调用 ThingMaker.MakeThing(def)，
    /// 如果 def 是"需要材质"的定义（MadeFromStuff，例如墙、家具），MakeThing 会抛异常，
    /// 整段脚本在已经扣过预算之后崩掉。这里提前挡住，并给出可执行的替代说法。
    /// </summary>
    internal static class SpawnSafety
    {
        public static bool CanMakeThing(ThingDef def, out string error)
        {
            error = null;

            if (def == null)
            {
                error = Loc.Pick("定义不存在", "That definition does not exist");
                return false;
            }

            if (def.thingClass == null)
            {
                error = Loc.Pick(def.defName + " 没有可实例化的 ThingClass",
                    def.defName + " has no instantiable ThingClass");
                return false;
            }

            if (def.category == ThingCategory.Pawn)
            {
                error = Loc.Pick(def.defName + " 是角色，请用 pawn.spawn 生成",
                    def.defName + " is a pawn; spawn it with pawn.spawn");
                return false;
            }

            if (def.MadeFromStuff)
            {
                error = Loc.Pick(
                    def.defName + " 需要材质（MadeFromStuff），不能用这个动作直接生成；"
                    + "请换一个成品定义，或改用 map.building（它支持 stuff）",
                    def.defName + " needs stuff (MadeFromStuff), so this action cannot spawn it directly; "
                    + "use a finished def instead, or use map.building (which accepts stuff)");
                return false;
            }

            return true;
        }
    }
}
