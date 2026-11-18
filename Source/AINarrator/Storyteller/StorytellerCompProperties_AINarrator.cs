using RimWorld;
using Verse;

namespace Stellaris.AI
{
    /// <summary>
    /// AI 导演的故事叙述者组件属性。
    /// 注意：这里刻意不叫 mtbDays，避免与 <see cref="StorytellerCompProperties"/> 基类可能存在的同名字段混淆。
    /// </summary>
    public class StorytellerCompProperties_AINarrator : StorytellerCompProperties
    {
        /// <summary>AI 请求的平均间隔（游戏日），仅在自动触发开启时生效。</summary>
        public float aiMtbDays = 1.5f;

        /// <summary>GameComponent 侧的轮询间隔（tick）。</summary>
        public int tickInterval = 60;

        public StorytellerCompProperties_AINarrator()
        {
            compClass = typeof(StorytellerComp_AINarrator);
        }

    }
}
