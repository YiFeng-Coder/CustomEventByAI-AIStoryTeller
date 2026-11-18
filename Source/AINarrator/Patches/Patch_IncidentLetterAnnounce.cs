using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>
    /// 原版事件信件的"正文替换"通道。
    ///
    /// 为什么分两步（SendIncidentLetter 起止 + MakeLetter 改文本）：
    ///  * <c>IncidentWorker.SendIncidentLetter</c> 内部会先按 IncidentDef.letterText
    ///    套一遍模板，再交给 <c>LetterMaker.MakeLetter</c>。直接改 SendIncidentLetter
    ///    的 text 参数，遇到带 letterText 模板的事件（例如机械集群、虫巢）只会替换模板里
    ///    的 {BASETEXT}，原版大段说明仍会留下。所以必须等模板套完、真正生成信件的那一刻再换。
    ///  * 用 parms 反查"AiIncidentHistory 里有没有这条 AI 事件"，作用域只覆盖这一次
    ///    SendIncidentLetter，不会误伤同一帧里其它模组/原版发出的信件。
    ///
    /// 只改正文，不改标题：玩家仍能一眼看出这是"袭击 / 虫巢 / 热浪"，
    /// AI 只负责把这件事讲成一句/一段叙事。事件本身、选项、链接、信纸样式都不变。
    /// </summary>
    internal static class IncidentLetterAnnounce
    {
        private static string _current;

        public static string Current => _current;

        /// <summary>进入一次原版事件信件生成；只有 AI 批准的事件才会激活替换。</summary>
        public static void Begin(IncidentParms parms)
        {
            _current = null;

            AINarratorSettings settings = AINarratorMod.Settings;
            if (settings == null || !settings.useAiIncidentAnnounce) return;
            if (!AiIncidentHistory.TryGetAnnounce(parms, out string announce)) return;

            _current = announce;
            if (settings.debugLogging)
            {
                Verse.Log.Message(AINarratorMod.LogPrefix + "incident letter text replaced by AI announce.");
            }
        }

        public static void End() => _current = null;
    }

    /// <summary>标记一次原版事件信件生成的开始/结束；不修改任何参数。</summary>
    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.SendIncidentLetter))]
    internal static class Patch_IncidentWorker_SendIncidentLetter
    {
        public static void Prefix(IncidentParms parms) => IncidentLetterAnnounce.Begin(parms);
        public static void Postfix() => IncidentLetterAnnounce.End();

        // 异常时也清掉作用域：否则后续普通信件会误用上一条 AI 文本。
        public static void Finalizer(Exception __exception) => IncidentLetterAnnounce.End();
    }

    /// <summary>模板已经套完，这里拿到的 text 就是信件最终正文。</summary>
    [HarmonyPatch(typeof(LetterMaker), nameof(LetterMaker.MakeLetter),
        new[]
        {
            typeof(TaggedString), typeof(TaggedString), typeof(LetterDef), typeof(LookTargets),
            typeof(Faction), typeof(Quest), typeof(List<ThingDef>)
        })]
    internal static class Patch_LetterMaker_MakeLetter
    {
        public static void Prefix(ref TaggedString text)
        {
            string announce = IncidentLetterAnnounce.Current;
            if (!string.IsNullOrWhiteSpace(announce)) text = announce;
        }
    }
}
