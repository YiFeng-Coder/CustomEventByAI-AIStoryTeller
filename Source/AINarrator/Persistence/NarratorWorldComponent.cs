using RimWorld;
using RimWorld.Planet;
using Verse;

namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>
    /// 叙述者持久状态。整包 JSON 存一个字符串字段，内部再带 schemaVersion。
    ///
    /// 为什么用整包 JSON 而不是逐字段 Scribe：
    ///  * 事件实例是变长结构，逐字段 Scribe 会让存档代码随 schema 变化反复改；
    ///  * 与 RimLua 的 LuaStateStore 用同一套思路，迁移时只改一个字符串。
    ///
    /// 注意：节点名 ("aiNarratorState") 必须唯一，避免与其它模组同名组件的 Scribe 节点冲突。
    /// </summary>
    public class NarratorWorldComponent : WorldComponent
    {
        private NarratorState _state = new NarratorState();

        public static NarratorWorldComponent Instance => Current.Game?.World?.GetComponent<NarratorWorldComponent>();

        public NarratorState State => _state;

        public NarratorWorldComponent(World world) : base(world)
        {
            Verse.Log.Message(AINarratorMod.LogPrefix + "world state component created.");
        }

        public override void ExposeData()
        {
            base.ExposeData();

            string json = null;

            if (Scribe.mode == LoadSaveMode.Saving)
            {
                json = _state != null ? _state.ToJson() : "";
            }

            Scribe_Values.Look(ref json, "aiNarratorState", "");

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                _state = NarratorState.FromJson(json);
                Verse.Log.Message(AINarratorMod.LogPrefix + "world state loaded, bytes=" +
                                  (json == null ? 0 : json.Length));
            }
        }

        public override void WorldComponentTick()
        {
            base.WorldComponentTick();
            DirectorLoop.Tick();
            EventRuntime.Tick();
        }

        public override void WorldComponentUpdate()
        {
            base.WorldComponentUpdate();
            DirectorLoop.Update();
        }
    }
}
