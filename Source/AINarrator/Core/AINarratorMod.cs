using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Stellaris.AI
{
    public class AINarratorMod : Mod
    {
        public const string LogPrefix = "[AI Narrator] ";

        public static AINarratorSettings Settings { get; private set; }

        private static Harmony _harmony;
        private static Vector2 _scroll;

        // 首次打开给足高度：宁可滚动条长一点，也不能让视口比内容矮。
        private static float _contentHeight = 1600f;
        private static string _lastFingerprint = "";
        private static long _lastWriteMs;

        // 数字输入框的编辑缓冲：不能每帧用 ToString() 覆盖，否则玩家永远输不进第二位。
        private static string _maxLookupRoundsText;
        private static string _lookupMaxResultsText;
        private static string _maxScriptRetriesText;

        public AINarratorMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<AINarratorSettings>();
            Settings.Normalize();

            try
            {
                _harmony = new Harmony("com.feng.ainarrator");
                _harmony.PatchAll(typeof(AINarratorMod).Assembly);
                Verse.Log.Message(LogPrefix + "harmony patched.");
            }
            catch (Exception e)
            {
                Verse.Log.Error(LogPrefix + "harmony patch failed: " + e);
            }

            try
            {
                LuaApiBridge.Register();
                ActionRegistry.RegisterBuiltins();
                Verse.Log.Message(LogPrefix + "lua api + " + ActionRegistry.All.Count + " action handlers registered.");
            }
            catch (Exception e)
            {
                Verse.Log.Error(LogPrefix + "action/lua registration failed: " + e);
            }

            Verse.Log.Message(LogPrefix + "loaded."
                + " provider=" + Settings.ProviderLabel
                + ", model=" + Settings.model
                + ", configured=" + Settings.IsConfigured
                + ", autoTrigger=" + Settings.enableAutoTrigger);
        }

        public override string SettingsCategory() => Loc.T("AI_Name");

        public override void DoSettingsWindowContents(Rect inRect)
        {
            // 结构照抄 RimLua 的设置页（同一 RimWorld 版本上跑通的范式）：
            //   上方 = 可滚动内容区，下方 = 固定 footer（关键按钮永远可见，不用滚到底去找）。
            const float footerHeight = 44f;
            Rect scrollRect = new Rect(inRect.x, inRect.y, inRect.width, inRect.height - footerHeight);

            // 视口高度取"内容估计值"与"可见区"的较大者；_contentHeight 初值给足，
            // 只有测到合理值（>100）才更新，避免测量异常把视口压成几十像素。
            float viewHeight = Mathf.Max(scrollRect.height, _contentHeight);
            float viewWidth = scrollRect.width - 20f;
            Rect viewRect = new Rect(0f, 0f, viewWidth, viewHeight);

            Widgets.BeginScrollView(scrollRect, ref _scroll, viewRect, true);

            var listing = new Listing_Standard();
            listing.Begin(viewRect);

            Text.Font = GameFont.Medium;
            listing.Label(Loc.T("AI_Settings_Title"));
            Text.Font = GameFont.Small;
            listing.Gap(4f);

            DrawProviderSection(listing);
            DrawRhythmSection(listing);
            DrawIncidentTextSection(listing);
            DrawQuotaSection(listing);
            DrawActionSection(listing);
            DrawPromptSection(listing);
            DrawDefLookupSection(listing);
            DrawDebugSection(listing);

            listing.End();
            // CurHeight 必须在 End() 之后读。之前把它写在 End() 前面，
            // 首帧读到 0，视口被算成 28px，整页滚不动——那就是"更严重"的来源。
            float measured = listing.CurHeight;

            // 只增不减：内容结构现在是恒定的（不再有"勾选才插入控件"的重排），
            // 测量值正常时精确跟随；万一某次测量异常，至少不会把已经够用的视口缩小。
            if (measured > 100f && measured < 20000f)
            {
                _contentHeight = Mathf.Max(_contentHeight, measured + 16f);
            }

            Widgets.EndScrollView();

            DrawFooter(inRect, footerHeight);

            Settings.Normalize();
            PersistIfChanged();
        }

        /// <summary>固定底栏：三个动作永远可见，不依赖滚动位置。</summary>
        private void DrawFooter(Rect inRect, float footerHeight)
        {
            Rect footer = new Rect(inRect.x, inRect.yMax - footerHeight + 6f, inRect.width, 30f);
            float gap = 8f;
            float width = (footer.width - gap * 2f) / 3f;

            if (Widgets.ButtonText(new Rect(footer.x, footer.y, width, footer.height), Loc.T("AI_Settings_ResetDefaults")))
            {
                ResetNarrativeDefaults();
            }

            if (Widgets.ButtonText(new Rect(footer.x + width + gap, footer.y, width, footer.height), Loc.T("AI_Settings_OpenDebug")))
            {
                Find.WindowStack.Add(new Window_NarratorDebug());
            }

            if (Widgets.ButtonText(new Rect(footer.x + (width + gap) * 2f, footer.y, width, footer.height), Loc.T("AI_Settings_Save")))
            {
                Settings.Normalize();
                WriteSettings();
                _lastFingerprint = Settings.Fingerprint();
                _lastWriteMs = Environment.TickCount64;
                Messages.Message(Loc.T("AI_Settings_Saved"), MessageTypeDefOf.NeutralEvent, false);
            }
        }

        /// <summary>恢复叙事参数默认值；刻意不动 Key 与接口配置。</summary>
        private static void ResetNarrativeDefaults()
        {
            Settings.enableAutoTrigger = true;
            Settings.aiMtbDays = 1.5f;
            Settings.minIntervalMinutes = 0;
            Settings.maxApiCallsPerDay = 0;
            Settings.enableLuaEvents = true;
            Settings.useAiIncidentAnnounce = true;
            Settings.maxLuaEventsPerDay = 0;
            Settings.maxScriptRetries = 1;
            Settings.debugLogging = true;
            Settings.maxLogRecords = 200;
            Settings.quotaWindow = 10;
            Settings.threatBigQuota = 3;
            Settings.threatSmallQuota = 4;
            Settings.miscQuota = 4;
            Settings.consecutiveThreatLimit = 3;
            Settings.repeatWindowDays = 30;
            Settings.temperature = 0.7f;
            Settings.timeoutSeconds = 600;
            Settings.writerTimeoutSeconds = 180;
            Settings.maxOutputTokens = 700;
            Settings.enableLongEvents = false;
            Settings.enableDefLookup = true;
            Settings.maxLookupRounds = 2;
            Settings.lookupMaxResults = 8;
            Settings.legacyDefLists = false;
            Settings.strictDefPlaceholders = false;
            _maxLookupRoundsText = null;
            _lookupMaxResultsText = null;
            _maxScriptRetriesText = null;
            Settings.enableSocialActions = true;
            Settings.enableSceneActions = true;
            Settings.enableInteractiveActions = true;
            Settings.colonistInfluence = AINarratorSettings.InfluenceEverything;
            Settings.overrideSystemPrompt = false;
            Settings.systemPromptOverride = "";
            Settings.extraPrompt = "";
        }

        /// <summary>
        /// 设置落盘。RimWorld 的 ModSettings.Write() 是真正的写文件操作，
        /// 所以既不能每帧调（拖动滑条会疯狂写盘），也不能完全不调（怕漏存）。
        /// 折中：值变了才写，且至少间隔 500ms。
        /// 关闭窗口时 Dialog_ModSettings 还会走一次 WriteSettings()，双保险。
        /// </summary>
        private static void PersistIfChanged()
        {
            string fingerprint = Settings.Fingerprint();
            if (fingerprint == _lastFingerprint) return;

            long now = Environment.TickCount64;
            if (_lastWriteMs != 0 && now - _lastWriteMs < 500) return;

            _lastFingerprint = fingerprint;
            _lastWriteMs = now;
            Settings.Write();
        }

        private static void DrawProviderSection(Listing_Standard listing)
        {
            listing.Label(Loc.T("AI_Settings_SectionProvider"));

            // 玩家手改接口地址后，服务商标识要跟着走，否则模型列表会停留在旧厂商上。
            int detected = LlmProviderPresets.Detect(Settings.endpoint);
            if (detected >= 0 && detected != LlmProviderPresets.IndexOf(Settings.providerId))
            {
                Settings.providerId = LlmProviderPresets.Get(detected).Id;
            }

            LlmProviderPreset current = LlmProviderPresets.Get(LlmProviderPresets.IndexOf(Settings.providerId));

            if (listing.ButtonText(Loc.T("AI_Settings_ProviderButton", current.Label)))
            {
                var options = new List<FloatMenuOption>();
                for (int i = 0; i < LlmProviderPresets.All.Length; i++)
                {
                    int index = i;
                    options.Add(new FloatMenuOption(LlmProviderPresets.All[i].Label,
                        () => Settings.ApplyPreset(index)));
                }

                Find.WindowStack.Add(new FloatMenu(options));
            }

            // 这个按钮**永远占位**：有的服务商（Groq / LM Studio / 自定义）没有预置模型列表，
            // 以前是"没列表就不画"，切服务商时高度会变——同样属于动态重排，一并去掉。
            bool hasModelPresets = current.Models.Length > 0;
            bool prevEnabled = GUI.enabled;
            GUI.enabled = hasModelPresets;

            string modelLabel = hasModelPresets
                ? Loc.T("AI_Settings_ModelButton", Ellipsis(Settings.model, 30))
                : Loc.T("AI_Settings_ModelButtonNoPresets", Ellipsis(Settings.model, 30));

            if (listing.ButtonText(modelLabel))
            {
                var options = new List<FloatMenuOption>();
                foreach (string candidate in current.Models)
                {
                    string picked = candidate;
                    options.Add(new FloatMenuOption(picked, () => Settings.model = picked));
                }

                Find.WindowStack.Add(new FloatMenu(options));
            }

            GUI.enabled = prevEnabled;

            listing.Label(Loc.T("AI_Settings_ModelLabel"));
            Settings.model = Widgets.TextField(listing.GetRect(28f), Settings.model ?? "");

            listing.Label(Loc.T("AI_Settings_EndpointLabel"));
            Settings.endpoint = Widgets.TextField(listing.GetRect(28f), Settings.endpoint ?? "");

            string keyHint = Settings.RequiresApiKey
                ? Loc.T("AI_Settings_KeyLabel")
                : Loc.T("AI_Settings_KeyLabelLocal");
            listing.Label(keyHint);
            Settings.apiKey = Widgets.TextField(listing.GetRect(28f), Settings.apiKey ?? "");

            listing.Label(Loc.T("AI_Settings_Temperature", Settings.temperature.ToString("F2")));
            Settings.temperature = listing.Slider(Settings.temperature, 0f, 2f);

            listing.Label(Loc.T("AI_Settings_DirectorTimeout", (Settings.timeoutSeconds / 60f).ToString("F1")));
            Settings.timeoutSeconds = (int)listing.Slider(Settings.timeoutSeconds, 10f, 3600f);

            listing.Label(Loc.T("AI_Settings_WriterTimeout", (Settings.writerTimeoutSeconds / 60f).ToString("F1")));
            Settings.writerTimeoutSeconds = (int)listing.Slider(Settings.writerTimeoutSeconds, 30f, 900f);

            Settings.maxOutputTokens = DrawUnlimitedSlider(
                listing, Loc.T("AI_Settings_MaxOutputTokens"), Settings.maxOutputTokens, 200, 4000, 50, "");
        }

        private static void DrawRhythmSection(Listing_Standard listing)
        {
            listing.Gap(6f);
            listing.Label(Loc.T("AI_Settings_SectionRhythm"));
            listing.CheckboxLabeled(Loc.T("AI_Settings_AutoTrigger"), ref Settings.enableAutoTrigger,
                Loc.T("AI_Settings_AutoTriggerDesc"));

            listing.Label(Loc.T("AI_Settings_Mtb", Settings.aiMtbDays.ToString("F2")));
            Settings.aiMtbDays = listing.Slider(Settings.aiMtbDays, 0.1f, 15f);

            Settings.minIntervalMinutes = DrawUnlimitedSlider(
                listing, Loc.T("AI_Settings_MinInterval"), Settings.minIntervalMinutes, 1, 240, 1,
                Loc.T("AI_Unit_Minutes"));

            Settings.maxApiCallsPerDay = DrawUnlimitedSlider(
                listing, Loc.T("AI_Settings_DailyCalls"), Settings.maxApiCallsPerDay, 1, 500, 1,
                Loc.T("AI_Unit_Times"));

            listing.CheckboxLabeled(Loc.T("AI_Settings_LuaEvents"), ref Settings.enableLuaEvents,
                Loc.T("AI_Settings_LuaEventsDesc"));

            // 关掉总开关后，限额滑条保留原值但置灰——不能动它，避免玩家以为改了却无效。
            bool prevLuaEnabled = GUI.enabled;
            GUI.enabled = prevLuaEnabled && Settings.enableLuaEvents;
            Settings.maxLuaEventsPerDay = DrawUnlimitedSlider(
                listing, Loc.T("AI_Settings_LuaDaily"), Settings.maxLuaEventsPerDay, 1, 20, 1,
                Loc.T("AI_Unit_Times"));

            // 剧本被拒后的重写轮数：这是"多花几次编剧调用换成功率"的旋钮，
            // 和每日上限一样，只在 Lua 剧情开启时有意义。
            Settings.maxScriptRetries = DrawIntField(listing, Loc.T("AI_Settings_ScriptRetries"),
                Settings.maxScriptRetries, ref _maxScriptRetriesText, 0, 10, Loc.T("AI_Unit_Rounds"),
                Loc.T("AI_Settings_ScriptRetriesHint"));
            GUI.enabled = prevLuaEnabled;

        }

        /// <summary>
        /// 原版事件的文本开关：只决定"信件正文由谁写"，不改变事件本身。
        /// 单独成节，避免和 Lua 剧情/节奏滑条混在一起让人误以为它也控制触发。
        /// </summary>
        private static void DrawIncidentTextSection(Listing_Standard listing)
        {
            listing.Gap(6f);
            listing.Label(Loc.T("AI_Settings_SectionIncidentText"));

            listing.CheckboxLabeled(Loc.T("AI_Settings_AiAnnounce"), ref Settings.useAiIncidentAnnounce,
                Loc.T("AI_Settings_AiAnnounceDesc"));
        }

        private static void DrawQuotaSection(Listing_Standard listing)
        {
            listing.Gap(6f);
            listing.Label(Loc.T("AI_Settings_SectionQuota"));

            Settings.quotaWindow = DrawPlainSlider(listing, Loc.T("AI_Settings_QuotaWindow"),
                Settings.quotaWindow, 1, 50, Loc.T("AI_Unit_Events"));

            Settings.threatBigQuota = DrawUnlimitedSlider(listing, Loc.T("AI_Settings_ThreatBig"),
                Settings.threatBigQuota, 1, 20, 1, Loc.T("AI_Unit_Times"));

            Settings.threatSmallQuota = DrawUnlimitedSlider(listing, Loc.T("AI_Settings_ThreatSmall"),
                Settings.threatSmallQuota, 1, 20, 1, Loc.T("AI_Unit_Times"));

            Settings.miscQuota = DrawUnlimitedSlider(listing, Loc.T("AI_Settings_Misc"),
                Settings.miscQuota, 1, 20, 1, Loc.T("AI_Unit_Times"));

            Settings.consecutiveThreatLimit = DrawUnlimitedSlider(listing, Loc.T("AI_Settings_ConsecutiveThreat"),
                Settings.consecutiveThreatLimit, 1, 20, 1, Loc.T("AI_Unit_Times"));

            Settings.repeatWindowDays = DrawUnlimitedSlider(listing, Loc.T("AI_Settings_RepeatWindow"),
                Settings.repeatWindowDays, 1, 365, 1, Loc.T("AI_Unit_Days"));
        }

        private static void DrawPromptSection(Listing_Standard listing)
        {
            listing.Gap(6f);
            listing.Label(Loc.T("AI_Settings_SectionPrompt"));
            listing.Label(Loc.T("AI_Settings_ExtraPrompt"));
            Settings.extraPrompt = Widgets.TextArea(listing.GetRect(96f), Settings.extraPrompt ?? "");

            listing.CheckboxLabeled(Loc.T("AI_Settings_LongEvents"), ref Settings.enableLongEvents,
                Loc.T("AI_Settings_LongEventsDesc"));

            listing.CheckboxLabeled(Loc.T("AI_Settings_OverridePrompt"), ref Settings.overrideSystemPrompt,
                Loc.T("AI_Settings_OverridePromptDesc"));

            // 关键设计：这个文本框**永远占位**，勾选与否不改变布局高度。
            // 之前是"勾选才插入"，导致内容高度在一帧内变化，滚动视口按旧高度算 → 整页被截。
            // 现在只切换可用状态（灰掉），高度恒定，滚动范围不受影响。
            bool enabled = GUI.enabled;
            GUI.enabled = Settings.overrideSystemPrompt;
            Settings.systemPromptOverride = Widgets.TextArea(listing.GetRect(120f),
                Settings.systemPromptOverride ?? "");
            GUI.enabled = enabled;

            // 提示行也常驻，勾选只改文字、不改行数——整段高度严格恒定。
            listing.Label(Settings.overrideSystemPrompt
                ? Loc.T("AI_Settings_OverrideOn")
                : Loc.T("AI_Settings_OverrideOff"));
        }

        private static void DrawDefLookupSection(Listing_Standard listing)
        {
            listing.Gap(6f);
            listing.Label(Loc.T("AI_Settings_SectionLookup"));

            listing.CheckboxLabeled(Loc.T("AI_Settings_DefLookup"), ref Settings.enableDefLookup,
                Loc.T("AI_Settings_DefLookupDesc"));

            Settings.maxLookupRounds = DrawIntField(listing, Loc.T("AI_Settings_LookupRounds"),
                Settings.maxLookupRounds, ref _maxLookupRoundsText, 0, int.MaxValue, Loc.T("AI_Unit_Rounds"),
                Loc.T("AI_Settings_LookupRoundsHint"));

            Settings.lookupMaxResults = DrawIntField(listing, Loc.T("AI_Settings_LookupResults"),
                Settings.lookupMaxResults, ref _lookupMaxResultsText, 4, int.MaxValue, Loc.T("AI_Unit_Events"),
                Loc.T("AI_Settings_LookupResultsHint"));

            listing.CheckboxLabeled(Loc.T("AI_Settings_LegacyDefs"), ref Settings.legacyDefLists,
                Loc.T("AI_Settings_LegacyDefsDesc"));

            listing.CheckboxLabeled(Loc.T("AI_Settings_StrictPlaceholders"), ref Settings.strictDefPlaceholders,
                Loc.T("AI_Settings_StrictPlaceholdersDesc"));
        }

        private static void DrawActionSection(Listing_Standard listing)
        {
            listing.Gap(6f);
            listing.Label(Loc.T("AI_Settings_SectionActions"));

            // 先放"能对我的殖民者做什么"：这是最容易被误解、也最需要一眼看见的一项。
            DrawInfluenceRow(listing);

            listing.CheckboxLabeled(Loc.T("AI_Settings_SocialActions"),
                ref Settings.enableSocialActions,
                Loc.T("AI_Settings_SocialActionsDesc"));

            listing.CheckboxLabeled(Loc.T("AI_Settings_SceneActions"),
                ref Settings.enableSceneActions,
                Loc.T("AI_Settings_SceneActionsDesc"));

            listing.CheckboxLabeled(Loc.T("AI_Settings_InteractiveActions"),
                ref Settings.enableInteractiveActions,
                Loc.T("AI_Settings_InteractiveActionsDesc"));
        }

        /// <summary>
        /// "AI 能对我的殖民者做什么"——三档选择。
        /// 用 FloatMenu 而不是滑条：只有 3 个离散值，滑条既难精确点到，也说不清差别。
        /// </summary>
        private static void DrawInfluenceRow(Listing_Standard listing)
        {
            int current = Settings.colonistInfluence;
            listing.Label(Loc.T("AI_Settings_Influence", AINarratorSettings.InfluenceLabel(current)));

            if (listing.ButtonText(Loc.T("AI_Settings_InfluenceButton")))
            {
                var options = new List<FloatMenuOption>
                {
                    new FloatMenuOption(Loc.T("AI_Settings_InfluenceNeverOption"), () =>
                    {
                        Settings.colonistInfluence = AINarratorSettings.InfluenceNever;
                    }),
                    new FloatMenuOption(Loc.T("AI_Settings_InfluencePsychologyOption"), () =>
                    {
                        Settings.colonistInfluence = AINarratorSettings.InfluencePsychology;
                    }),
                    new FloatMenuOption(Loc.T("AI_Settings_InfluenceEverythingOption"), () =>
                    {
                        Settings.colonistInfluence = AINarratorSettings.InfluenceEverything;
                    })
                };

                Find.WindowStack.Add(new FloatMenu(options));
            }

            listing.Label(current == AINarratorSettings.InfluenceEverything
                ? Loc.T("AI_Settings_InfluenceHintAll")
                : Loc.T("AI_Settings_InfluenceHintLimited"));
        }

        private static void DrawDebugSection(Listing_Standard listing)
        {
            listing.Gap(6f);
            listing.Label(Loc.T("AI_Settings_SectionStatus"));
            listing.CheckboxLabeled(Loc.T("AI_Settings_DebugLog"), ref Settings.debugLogging,
                Loc.T("AI_Settings_DebugLogDesc"));

            listing.Label(Loc.T("AI_Settings_CandidateCount", CandidateTable.Count));
            listing.Label(Loc.T("AI_Settings_Configured", Settings.IsConfigured
                ? Loc.T("AI_Settings_ConfiguredYes")
                : Loc.T("AI_Settings_ConfiguredNo")));
            listing.Label(Loc.T("AI_Settings_CurrentProvider", Settings.ProviderLabel, Ellipsis(Settings.model, 40)));
            listing.Label(Loc.T("AI_Settings_FooterHint"));
        }

        /// <summary>
        /// 整数输入框：用于"查询轮数 / 返回条数"这类次数限制。
        /// 滑条很难精确点到 2、8 这种小数值，玩家更愿意直接填。
        /// buffer 必须跨帧保存，否则每帧 ToString() 会把刚输入的第二位覆盖掉。
        /// </summary>
        private static int DrawIntField(Listing_Standard listing, string label, int value,
            ref string buffer, int min, int max, string unit, string hint = null)
        {
            string range = max == int.MaxValue
                ? Loc.T("AI_Common_AtLeast", min)
                : Loc.T("AI_Common_Range", min, max);
            if (!string.IsNullOrEmpty(unit)) range += " " + unit;
            listing.Label(label + Loc.T("AI_Common_RangeParen", range)
                + (string.IsNullOrEmpty(hint) ? "" : Loc.T("AI_Common_HintSep", hint)));

            if (buffer == null) buffer = value.ToString();

            Rect rect = listing.GetRect(28f);
            string text = Widgets.TextField(rect, buffer);
            if (!string.Equals(text, buffer, StringComparison.Ordinal))
            {
                buffer = text;
                if (int.TryParse(text.Trim(), out int parsed))
                {
                    value = Mathf.Clamp(parsed, min, max);

                    // 越界输入（例如 99）直接回写成夹紧值，避免输入框显示 99、实际存 4。
                    if (parsed != value) buffer = value.ToString();
                }
            }

            return value;
        }

        /// <summary>
        /// 带"不限制"的滑条：**最右端 = 不限制**（存 0），向左拖是具体数值。
        /// 业界习惯就是这样，别把不限制放在最左。
        /// </summary>
        private static int DrawUnlimitedSlider(Listing_Standard listing, string label, int value,
            int min, int max, int step, string unit)
        {
            bool unlimited = value <= 0;
            float pos = unlimited ? max : Mathf.Clamp(value, min, max - step);
            string shown = unlimited ? Loc.T("AI_Common_Unlimited") : value + (string.IsNullOrEmpty(unit) ? "" : " " + unit);

            listing.Label(Loc.T("AI_Common_LabelValue", label, shown));
            float newPos = listing.Slider(pos, min, max);

            int rounded = Mathf.RoundToInt(newPos / (float)step) * step;
            if (rounded >= max - step / 2) return 0;
            return Mathf.Max(min, rounded);
        }

        /// <summary>按钮文字过长会溢出控件，截断显示（完整值在下面的输入框里）。</summary>
        private static string Ellipsis(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return Loc.T("AI_Common_NotSet");
            if (text.Length <= max) return text;
            return text.Substring(0, max - 1) + "…";
        }

        /// <summary>普通整数滑条（不提供"不限制"语义）。</summary>
        private static int DrawPlainSlider(Listing_Standard listing, string label, int value,
            int min, int max, string unit)
        {
            listing.Label(Loc.T("AI_Common_LabelValue", label,
                value + (string.IsNullOrEmpty(unit) ? "" : " " + unit)));
            float newPos = listing.Slider(value, min, max);
            return Mathf.Clamp(Mathf.RoundToInt(newPos), min, max);
        }

        public override void WriteSettings()
        {
            Settings?.Normalize();
            base.WriteSettings();
        }
    }
}
