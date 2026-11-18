using System;
using Verse;

namespace Stellaris.AI
{
    /// <summary>
    /// Def 装载完成后的补建入口。
    ///
    /// 为什么需要：Mod 的构造函数在 DefDatabase 还是空的时候就会跑（日志里曾经出现
    /// "candidate incidents=0" 后永远不重建）。把候选表的首次构建固定到
    /// StaticConstructorOnStartup —— 这个钩子在所有 Def 装载、ResolveReferences 之后
    /// 才执行，保证 category / workerClass 已经可用。
    ///
    /// CandidateTable.All 自己也有懒重建兜底；这里是"第一落点"，
    /// 让日志在加载阶段就能看到真实候选数，而不是等到第一次请求才发现是 0。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class AINarratorBootstrap
    {
        static AINarratorBootstrap()
        {
            try
            {
                CandidateTable.Rebuild();
            }
            catch (Exception e)
            {
                Verse.Log.Error(AINarratorMod.LogPrefix + "candidate table init failed: " + e);
            }
        }
    }
}
