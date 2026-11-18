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

        public override string SettingsCategory() => "AI 叙述者";

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
            listing.Label("AI 叙述者");
            Text.Font = GameFont.Small;
            listing.Gap(4f);

            DrawProviderSection(listing);
            DrawRhythmSection(listing);
            DrawIncidentTextSection(listing);
            DrawQuotaSection(listing);
            DrawPromptSection(listing);
            DrawDefLookupSection(listing);
            DrawActionSection(listing);
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

            if (Widgets.ButtonText(new Rect(footer.x, footer.y, width, footer.height), "恢复默认参数"))
            {
                ResetNarrativeDefaults();
            }

            if (Widgets.ButtonText(new Rect(footer.x + width + gap, footer.y, width, footer.height), "调试窗口"))
            {
                Find.WindowStack.Add(new Window_NarratorDebug());
            }

            if (Widgets.ButtonText(new Rect(footer.x + (width + gap) * 2f, footer.y, width, footer.height), "保存设置"))
            {
                Settings.Normalize();
                WriteSettings();
                _lastFingerprint = Settings.Fingerprint();
                _lastWriteMs = Environment.TickCount64;
                Messages.Message("AI 叙述者设置已保存", MessageTypeDefOf.NeutralEvent, false);
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
            Settings.enableSocialActions = true;
            Settings.enableSceneActions = true;
            Settings.enableInteractiveActions = true;
            Settings.affectPlayerColonistsSocial = false;
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
            listing.Label("—— 模型服务商 ——");

            // 玩家手改接口地址后，服务商标识要跟着走，否则模型列表会停留在旧厂商上。
            int detected = LlmProviderPresets.Detect(Settings.endpoint);
            if (detected >= 0 && detected != LlmProviderPresets.IndexOf(Settings.providerId))
            {
                Settings.providerId = LlmProviderPresets.Get(detected).Id;
            }

            LlmProviderPreset current = LlmProviderPresets.Get(LlmProviderPresets.IndexOf(Settings.providerId));

            if (listing.ButtonText("服务商：" + current.Label + "　（点击选择）"))
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
                ? "模型：" + Ellipsis(Settings.model, 30) + "　（点击从常用列表选择）"
                : "模型：" + Ellipsis(Settings.model, 30) + "　（该服务商没有预置列表，请手填）";

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

            listing.Label("模型名（可直接手改，用于填上面列表里没有的模型）");
            Settings.model = Widgets.TextField(listing.GetRect(28f), Settings.model ?? "");

            listing.Label("接口地址（OpenAI 兼容的完整 URL）");
            Settings.endpoint = Widgets.TextField(listing.GetRect(28f), Settings.endpoint ?? "");

            string keyHint = Settings.RequiresApiKey
                ? "API Key（只存本机 Mod 设置，不写进存档）"
                : "API Key（本地服务可留空）";
            listing.Label(keyHint);
            Settings.apiKey = Widgets.TextField(listing.GetRect(28f), Settings.apiKey ?? "");

            listing.Label("温度：" + Settings.temperature.ToString("F2") + "　（越低越稳，越高越野）");
            Settings.temperature = listing.Slider(Settings.temperature, 0f, 2f);

            listing.Label("导演调用超时：" + (Settings.timeoutSeconds / 60f).ToString("F1") + " 分钟");
            Settings.timeoutSeconds = (int)listing.Slider(Settings.timeoutSeconds, 10f, 3600f);

            listing.Label("编剧调用超时：" + (Settings.writerTimeoutSeconds / 60f).ToString("F1") + " 分钟");
            Settings.writerTimeoutSeconds = (int)listing.Slider(Settings.writerTimeoutSeconds, 30f, 900f);

            Settings.maxOutputTokens = DrawUnlimitedSlider(
                listing, "最大输出 token", Settings.maxOutputTokens, 200, 4000, 50, "");
        }

        private static void DrawRhythmSection(Listing_Standard listing)
        {
            listing.Gap(6f);
            listing.Label("—— 叙事节奏 ——");
            listing.CheckboxLabeled("启用自动触发", ref Settings.enableAutoTrigger,
                "关闭后叙述者只响应调试窗里的「立即请求一次」。");

            listing.Label("平均事件间隔：" + Settings.aiMtbDays.ToString("F2") + " 天");
            Settings.aiMtbDays = listing.Slider(Settings.aiMtbDays, 0.1f, 15f);

            Settings.minIntervalMinutes = DrawUnlimitedSlider(
                listing, "两次调用最小间隔", Settings.minIntervalMinutes, 1, 240, 1, "分钟");

            Settings.maxApiCallsPerDay = DrawUnlimitedSlider(
                listing, "每日调用上限", Settings.maxApiCallsPerDay, 1, 500, 1, "次");

            listing.CheckboxLabeled("启用 Lua 剧情事件", ref Settings.enableLuaEvents,
                "关闭后 AI 不再现场写原创剧情，只能选原版事件或休整：调用更省、行为更可预测。"
                + "已经开始的事件会正常跑完，不会卡在半个阶段。");

            // 关掉总开关后，限额滑条保留原值但置灰——不能动它，避免玩家以为改了却无效。
            bool prevLuaEnabled = GUI.enabled;
            GUI.enabled = prevLuaEnabled && Settings.enableLuaEvents;
            Settings.maxLuaEventsPerDay = DrawUnlimitedSlider(
                listing, "每日 Lua 剧情上限", Settings.maxLuaEventsPerDay, 1, 20, 1, "次");
            GUI.enabled = prevLuaEnabled;

        }

        /// <summary>
        /// 原版事件的文本开关：只决定"信件正文由谁写"，不改变事件本身。
        /// 单独成节，避免和 Lua 剧情/节奏滑条混在一起让人误以为它也控制触发。
        /// </summary>
        private static void DrawIncidentTextSection(Listing_Standard listing)
        {
            listing.Gap(6f);
            listing.Label("—— 原版事件文本 ——");

            listing.CheckboxLabeled("原版事件使用 AI 描述", ref Settings.useAiIncidentAnnounce,
                "开启后，AI 触发的原版事件会用模型返回的 announce 那句话替换事件信件的正文；"
                + "事件本身、信件标题与交互都不变。关闭则完全显示原版信件文字。"
                + "该开关只影响 AI 批准并触发的原版事件，不影响游戏自己触发的事件。");
        }

        private static void DrawQuotaSection(Listing_Standard listing)
        {
            listing.Gap(6f);
            listing.Label("—— 事件配额（滑到最右 = 不限制）——");

            Settings.quotaWindow = DrawPlainSlider(listing, "统计窗口（最近 N 个事件）",
                Settings.quotaWindow, 1, 50, "个");

            Settings.threatBigQuota = DrawUnlimitedSlider(listing, "ThreatBig 上限",
                Settings.threatBigQuota, 1, 20, 1, "次");

            Settings.threatSmallQuota = DrawUnlimitedSlider(listing, "ThreatSmall 上限",
                Settings.threatSmallQuota, 1, 20, 1, "次");

            Settings.miscQuota = DrawUnlimitedSlider(listing, "Misc 上限",
                Settings.miscQuota, 1, 20, 1, "次");

            Settings.consecutiveThreatLimit = DrawUnlimitedSlider(listing, "连续威胁上限",
                Settings.consecutiveThreatLimit, 1, 20, 1, "次");

            Settings.repeatWindowDays = DrawUnlimitedSlider(listing, "同一事件冷却",
                Settings.repeatWindowDays, 1, 365, 1, "天");
        }

        private static void DrawPromptSection(Listing_Standard listing)
        {
            listing.Gap(6f);
            listing.Label("—— 叙事风格提示词 ——");
            listing.Label("附加指令（追加在系统提示词后面，用来改剧本风格；留空则不加）");
            Settings.extraPrompt = Widgets.TextArea(listing.GetRect(96f), Settings.extraPrompt ?? "");

            listing.CheckboxLabeled("允许长线事件（多阶段 / 跨天定时器）", ref Settings.enableLongEvents,
                "关闭时只生成当场结束的短线事件。建议保持关闭：AI 写的长线事件经常前后不连贯、伏笔收不回来，"
                + "玩家体验往往很差；短线事件（一封信、一次抉择、一个后果）反而更稳。"
                + "需要长线时再打开。" );

            listing.CheckboxLabeled("完全覆盖默认系统提示词（进阶）", ref Settings.overrideSystemPrompt,
                "勾选后启用下面这段替换内置人设；JSON 输出格式仍会被强制追加，保证可解析。");

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
                ? "（已启用：上面这段会替换内置人设）"
                : "（未启用：上面这段不会生效）");
        }

        private static void DrawDefLookupSection(Listing_Standard listing)
        {
            listing.Gap(6f);
            listing.Label("—— DefName 查询（多模组友好）——");

            listing.CheckboxLabeled("允许 AI 按需查询 DefName", ref Settings.enableDefLookup,
                "推荐开启：不再把每类 Def 的静态名单塞进提示词，模型需要时用 LOOKUP 查，"
                + "或直接写 @item/药 让游戏解析。关闭后只剩目录示例与精确名字。");

            Settings.maxLookupRounds = DrawIntField(listing, "每次请求最多查询轮数",
                Settings.maxLookupRounds, ref _maxLookupRoundsText, 0, int.MaxValue, "轮", "0 = 关闭查询轮，不设上限");

            Settings.lookupMaxResults = DrawIntField(listing, "单次查询返回条数",
                Settings.lookupMaxResults, ref _lookupMaxResultsText, 4, int.MaxValue, "条",
                "不设上限；太大可能超出模型上下文");

            listing.CheckboxLabeled("回退：使用旧版全量 Def 名单", ref Settings.legacyDefLists,
                "仅用于对比/应急。旧版按加载顺序截断，多模组下会重新出现"
                + "「后装模组的 Def 永远看不到」的偏差。");

            listing.CheckboxLabeled("严格占位符模式（调试）", ref Settings.strictDefPlaceholders,
                "默认关闭：@类别/关键词 解析不唯一时自动取最匹配的第一个。"
                + "开启后改为报出候选并拒绝，方便观察解析行为。");
        }

        private static void DrawActionSection(Listing_Standard listing)
        {
            listing.Gap(6f);
            listing.Label("—— 动作库（AI 能用的叙事手段）——");

            listing.CheckboxLabeled("心理与关系（想法 / 心情 / 灵感 / 特质 / 关系 / 崩溃）",
                ref Settings.enableSocialActions,
                "让角色对事件产生情绪与关系变化。关闭后 AI 只能描写，不能改变任何人的心理状态。");

            // 关键开关：默认关闭。P3 的红线是"不碰玩家的人"，现在把它交给玩家自己决定。
            bool prev = GUI.enabled;
            GUI.enabled = Settings.enableSocialActions;
            listing.CheckboxLabeled("允许心理层影响你的殖民者",
                ref Settings.affectPlayerColonistsSocial,
                "开启后，AI 可以给你的殖民者加想法、改心情、建立关系（幅度有上限，且计入每次事件的社交预算）。"
                + "关闭时，心理层只作用于 AI 自己生成的角色。危险动作（伤害/击杀/崩溃）永远不能指向你的殖民者。");
            GUI.enabled = prev;

            listing.CheckboxLabeled("场景与破坏（建筑 / 地形 / 屋顶 / 污渍 / 动物 / 清除 / 塌方）",
                ref Settings.enableSceneActions,
                "让事件现场留下可见的痕迹。关闭后剧情不能执行这一组场景动作。");

            listing.CheckboxLabeled("交互与呈现（叙事目标 / 镜头 / 信件抉择）",
                ref Settings.enableInteractiveActions,
                "关闭后剧情不能创建叙事目标、移动镜头或使用信件栏抉择。");
        }

        private static void DrawDebugSection(Listing_Standard listing)
        {
            listing.Gap(6f);
            listing.Label("—— 状态 ——");
            listing.CheckboxLabeled("调试日志", ref Settings.debugLogging, "把每次决策要点写进 Player.log。");

            listing.Label("候选事件：" + CandidateTable.Count + " 个");
            listing.Label("配置：" + (Settings.IsConfigured ? "已完整" : "不完整（叙述者会保持静默）"));
            listing.Label("当前服务商：" + Settings.ProviderLabel + "　·　模型：" + Ellipsis(Settings.model, 40));
            listing.Label("操作按钮在窗口底部的固定栏里，不用滚到底找。");
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
                ? "≥" + min
                : min + "~" + max;
            if (!string.IsNullOrEmpty(unit)) range += " " + unit;
            listing.Label(label + "（" + range + "）" + (string.IsNullOrEmpty(hint) ? "" : " · " + hint));

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
            string shown = unlimited ? "不限制" : value + (string.IsNullOrEmpty(unit) ? "" : " " + unit);

            listing.Label(label + "：" + shown);
            float newPos = listing.Slider(pos, min, max);

            int rounded = Mathf.RoundToInt(newPos / (float)step) * step;
            if (rounded >= max - step / 2) return 0;
            return Mathf.Max(min, rounded);
        }

        /// <summary>按钮文字过长会溢出控件，截断显示（完整值在下面的输入框里）。</summary>
        private static string Ellipsis(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "(未填)";
            if (text.Length <= max) return text;
            return text.Substring(0, max - 1) + "…";
        }

        /// <summary>普通整数滑条（不提供"不限制"语义）。</summary>
        private static int DrawPlainSlider(Listing_Standard listing, string label, int value,
            int min, int max, string unit)
        {
            listing.Label(label + "：" + value + (string.IsNullOrEmpty(unit) ? "" : " " + unit));
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