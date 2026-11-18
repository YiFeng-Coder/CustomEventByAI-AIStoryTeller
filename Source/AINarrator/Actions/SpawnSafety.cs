using RimWorld;
using Verse;

namespace Stellaris.AI
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
                error = "定义不存在";
                return false;
            }

            if (def.thingClass == null)
            {
                error = def.defName + " 没有可实例化的 ThingClass";
                return false;
            }

            if (def.category == ThingCategory.Pawn)
            {
                error = def.defName + " 是角色，请用 pawn.spawn 生成";
                return false;
            }

            if (def.MadeFromStuff)
            {
                error = def.defName + " 需要材质（MadeFromStuff），不能用这个动作直接生成；"
                        + "请换一个成品定义，或改用 map.building（它支持 stuff）";
                return false;
            }

            return true;
        }
    }
}
