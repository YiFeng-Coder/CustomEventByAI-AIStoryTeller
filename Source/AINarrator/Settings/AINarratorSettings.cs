using System;
using Verse;

namespace Stellaris.AI
{
    /// <summary>
    /// AI 叙述者设置。
    ///
    /// 约定：
    ///  * 服务商与模型名有预置（<see cref="LlmProviderPresets"/>），一键填好，仍可手改；
    ///  * 任何 Key 都不预置，玩家自己填；
    ///  * 所有"限制"类字段都可以设成 0 = 不限制。
    /// </summary>
    public class AINarratorSettings : ModSettings
    {
        // ---- 叙事节奏 ----
        public bool enableAutoTrigger = true;
        public float aiMtbDays = 1.5f;
        public int minIntervalMinutes = 0;      // 0 = 不限制
        public int maxApiCallsPerDay = 0;        // 0 = 不限制

        /// <summary>
        /// 是否允许 AI 现场写 Lua 剧情事件。默认开启。
        /// 关闭后导演只能选原版事件或休整，不再发起编剧调用；
        /// 已经开始的剧情照常跑完（中途掐断会让存档里的事件卡在半个阶段）。
        /// </summary>
        public bool enableLuaEvents = true;

        /// <summary>
        /// AI 触发的原版事件是否使用模型返回的 announce 文本作为事件信件正文。
        /// 默认开启：原版事件仍由原版 worker 执行（标题、信纸样式、链接都不变），
        /// 只有正文那句叙事描述换成 AI 写的。关闭后完全保留原版信件文字。
        /// </summary>
        public bool useAiIncidentAnnounce = true;

        public int maxLuaEventsPerDay = 0;       // 0 = 不限制

        /// <summary>
        /// Lua 脚本被拒后允许重写几轮（= 允许被拒几次）。
        /// 0 = 被拒即放弃；1 = 现有默认（带报错原因重写一次）；上限见 Normalize。
        /// 每多一轮就多一次编剧调用，会把"被拒 → 修好"的成功率换成 token 与等待时间。
        /// </summary>
        public int maxScriptRetries = 1;

        public bool debugLogging = true;
        public int maxLogRecords = 200;

        // ---- 配额（0 = 不限制）----
        public int quotaWindow = 10;
        public int threatBigQuota = 3;
        public int threatSmallQuota = 4;
        public int miscQuota = 4;
        public int consecutiveThreatLimit = 3;
        public int repeatWindowDays = 30;

        // ---- 剧情形态 ----
        /// <summary>是否允许 AI 写长线事件（多阶段 / 跨天定时器）。默认关闭。</summary>
        public bool enableLongEvents = false;

        // ---- Def 查询 ----
        /// <summary>允许模型用 LOOKUP 主动查看候选 DefName。</summary>
        public bool enableDefLookup = true;

        /// <summary>每次导演/编剧请求最多几轮查询（0 = 不允许）。</summary>
        public int maxLookupRounds = 2;

        /// <summary>单次查询返回条数（4~20）。</summary>
        public int lookupMaxResults = 8;

        /// <summary>回退到旧版全量 Def 名单（仅对比/应急）。</summary>
        public bool legacyDefLists = false;

        /// <summary>调试：占位符歧义时返回候选并拒绝，而不是自动取第一名。</summary>
        public bool strictDefPlaceholders = false;

        // ---- 动作库（P4）----
        /// <summary>心理与关系动作组（想法 / 心情 / 灵感 / 特质 / 关系 / 崩溃）。</summary>
        public bool enableSocialActions = true;

        /// <summary>场景与破坏动作组（建筑 / 地形 / 屋顶 / 污渍 / 动物 / 清除 / 塌方）。</summary>
        public bool enableSceneActions = true;

        /// <summary>交互与呈现动作组（目标 / 镜头 / 信件抉择）。</summary>
        public bool enableInteractiveActions = true;

        // AI 能对你的殖民者做什么：0 = 不允许，1 = 仅心理层，2 = 全部（含伤害/击杀/崩溃）。
        // 注意：这是"玩家自己承担的开关"，不是提示词里的建议——校验器按它放行或拒绝。
        public const int InfluenceNever = 0;
        public const int InfluencePsychology = 1;
        public const int InfluenceEverything = 2;

        /// <summary>
        /// AI 对你的殖民者的影响范围。
        /// 默认"全部"：危险动作也能落到你的殖民者身上（仍受每次事件 3 次的危险预算限制）。
        /// 想更保守就调成"仅心理层"或"不允许"。
        /// </summary>
        public int colonistInfluence = InfluenceEverything;

        public static string InfluenceLabel(int value)
        {
            switch (value)
            {
                case InfluenceNever: return "不允许";
                case InfluencePsychology: return "仅心理层";
                default: return "全部";
            }
        }

        // ---- 模型接入 ----
        public string providerId = "deepseek";
        public string endpoint = "";
        public string model = "";
        public string apiKey = "";
        public float temperature = 0.7f;
        public int timeoutSeconds = 600;         // 默认 10 分钟
        public int writerTimeoutSeconds = 180;   // 写脚本单独的超时：脚本就几 KB，不需要 10 分钟
        public int maxOutputTokens = 700;

        // ---- 提示词 ----
        public bool overrideSystemPrompt = false;
        public string systemPromptOverride = "";
        public string extraPrompt = "";

        public bool IsConfigured
        {
            get
            {
                if (string.IsNullOrWhiteSpace(endpoint)) return false;
                if (string.IsNullOrWhiteSpace(model)) return false;
                return !RequiresApiKey || !string.IsNullOrWhiteSpace(apiKey);
            }
        }

        /// <summary>本地服务（Ollama / LM Studio）通常不需要 Key。</summary>
        public bool RequiresApiKey
        {
            get
            {
                string e = (endpoint ?? "").ToLowerInvariant();
                if (e.Length == 0) return true;
                return !(e.Contains("127.0.0.1") || e.Contains("localhost") || e.Contains("[::1]"));
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Values.Look(ref enableAutoTrigger, "enableAutoTrigger", true);
            Scribe_Values.Look(ref aiMtbDays, "aiMtbDays", 1.5f);
            Scribe_Values.Look(ref minIntervalMinutes, "minIntervalMinutes", 0);
            Scribe_Values.Look(ref maxApiCallsPerDay, "maxApiCallsPerDay", 0);
            Scribe_Values.Look(ref enableLuaEvents, "enableLuaEvents", true);
            Scribe_Values.Look(ref useAiIncidentAnnounce, "useAiIncidentAnnounce", true);
            Scribe_Values.Look(ref maxLuaEventsPerDay, "maxLuaEventsPerDay", 0);
            Scribe_Values.Look(ref maxScriptRetries, "maxScriptRetries", 1);
            Scribe_Values.Look(ref debugLogging, "debugLogging", true);
            Scribe_Values.Look(ref maxLogRecords, "maxLogRecords", 200);

            Scribe_Values.Look(ref quotaWindow, "quotaWindow", 10);
            Scribe_Values.Look(ref threatBigQuota, "threatBigQuota", 3);
            Scribe_Values.Look(ref threatSmallQuota, "threatSmallQuota", 4);
            Scribe_Values.Look(ref miscQuota, "miscQuota", 4);
            Scribe_Values.Look(ref consecutiveThreatLimit, "consecutiveThreatLimit", 3);
            Scribe_Values.Look(ref repeatWindowDays, "repeatWindowDays", 30);
            Scribe_Values.Look(ref enableLongEvents, "enableLongEvents", false);
            Scribe_Values.Look(ref enableDefLookup, "enableDefLookup", true);
            Scribe_Values.Look(ref maxLookupRounds, "maxLookupRounds", 2);
            Scribe_Values.Look(ref lookupMaxResults, "lookupMaxResults", 8);
            Scribe_Values.Look(ref legacyDefLists, "legacyDefLists", false);
            Scribe_Values.Look(ref strictDefPlaceholders, "strictDefPlaceholders", false);
            Scribe_Values.Look(ref enableSocialActions, "enableSocialActions", true);
            Scribe_Values.Look(ref enableSceneActions, "enableSceneActions", true);
            Scribe_Values.Look(ref enableInteractiveActions, "enableInteractiveActions", true);
            Scribe_Values.Look(ref colonistInfluence, "colonistInfluence", InfluenceEverything);

            Scribe_Values.Look(ref providerId, "providerId", "deepseek");
            Scribe_Values.Look(ref endpoint, "endpoint", LlmProviderPresets.All[0].Endpoint);
            Scribe_Values.Look(ref model, "model", LlmProviderPresets.All[0].DefaultModel);
            Scribe_Values.Look(ref apiKey, "apiKey", "");
            Scribe_Values.Look(ref temperature, "temperature", 0.7f);
            Scribe_Values.Look(ref timeoutSeconds, "timeoutSeconds", 600);
            Scribe_Values.Look(ref writerTimeoutSeconds, "writerTimeoutSeconds", 180);
            Scribe_Values.Look(ref maxOutputTokens, "maxOutputTokens", 700);

            Scribe_Values.Look(ref overrideSystemPrompt, "overrideSystemPrompt", false);
            Scribe_Values.Look(ref systemPromptOverride, "systemPromptOverride", "");
            Scribe_Values.Look(ref extraPrompt, "extraPrompt", "");
        }

        /// <summary>把字段夹到合法区间。限制类字段的 0 表示"不限制"，要保留。</summary>
        public void Normalize()
        {
            aiMtbDays = Clamp(aiMtbDays, 0.1f, 60f);
            minIntervalMinutes = Clamp(minIntervalMinutes, 0, 24 * 60);
            maxApiCallsPerDay = Clamp(maxApiCallsPerDay, 0, 5000);
            maxLuaEventsPerDay = Clamp(maxLuaEventsPerDay, 0, 200);
            maxScriptRetries = Clamp(maxScriptRetries, 0, 10);
            maxLogRecords = Clamp(maxLogRecords, 20, 1000);

            quotaWindow = Clamp(quotaWindow, 1, 50);
            threatBigQuota = Clamp(threatBigQuota, 0, 20);
            threatSmallQuota = Clamp(threatSmallQuota, 0, 20);
            miscQuota = Clamp(miscQuota, 0, 20);
            consecutiveThreatLimit = Clamp(consecutiveThreatLimit, 0, 20);
            repeatWindowDays = Clamp(repeatWindowDays, 0, 365);
            temperature = Clamp(temperature, 0f, 2f);
            timeoutSeconds = Clamp(timeoutSeconds, 10, 3600);
            writerTimeoutSeconds = Clamp(writerTimeoutSeconds, 30, 900);
            maxOutputTokens = Clamp(maxOutputTokens, 0, 8000);
            if (maxLookupRounds < 0) maxLookupRounds = 0;
            if (lookupMaxResults < 4) lookupMaxResults = 4;
            colonistInfluence = Clamp(colonistInfluence, InfluenceNever, InfluenceEverything);

            endpoint = (endpoint ?? "").Trim();
            model = (model ?? "").Trim();
            apiKey = (apiKey ?? "").Trim();
            providerId = string.IsNullOrEmpty(providerId) ? "custom" : providerId;

            systemPromptOverride = systemPromptOverride ?? "";
            extraPrompt = extraPrompt ?? "";
        }

        /// <summary>套用某家预置；不覆盖 Key（Key 是玩家的，永远不替玩家清空或改写）。</summary>
        public void ApplyPreset(int index)
        {
            LlmProviderPreset preset = LlmProviderPresets.Get(index);
            providerId = preset.Id;
            endpoint = preset.Endpoint;

            // 没有预置模型列表的服务商（Groq / LM Studio / 自定义）保留玩家已填的模型名。
            if (preset.Models.Length > 0) model = preset.DefaultModel;
        }

        public string ProviderLabel => LlmProviderPresets.Get(LlmProviderPresets.IndexOf(providerId)).Label;

        /// <summary>
        /// 把全部设置压成一个短字符串，用来判断"值到底变没变"，
        /// 避免每帧调 Write() 但又不漏存。不要在这里做任何 IO。
        /// </summary>
        public string Fingerprint()
        {
            return string.Join("|", new[]
            {
                enableAutoTrigger ? "1" : "0",
                aiMtbDays.ToString("R"),
                minIntervalMinutes.ToString(),
                maxApiCallsPerDay.ToString(),
                enableLuaEvents ? "1" : "0",
                useAiIncidentAnnounce ? "1" : "0",
                maxLuaEventsPerDay.ToString(),
                maxScriptRetries.ToString(),
                debugLogging ? "1" : "0",
                maxLogRecords.ToString(),
                quotaWindow.ToString(),
                threatBigQuota.ToString(),
                threatSmallQuota.ToString(),
                miscQuota.ToString(),
                consecutiveThreatLimit.ToString(),
                repeatWindowDays.ToString(),
                enableLongEvents ? "1" : "0",
                enableDefLookup ? "1" : "0",
                maxLookupRounds.ToString(),
                lookupMaxResults.ToString(),
                legacyDefLists ? "1" : "0",
                strictDefPlaceholders ? "1" : "0",
                enableSocialActions ? "1" : "0",
                enableSceneActions ? "1" : "0",
                enableInteractiveActions ? "1" : "0",
                colonistInfluence.ToString(),
                providerId ?? "",
                endpoint ?? "",
                model ?? "",
                apiKey ?? "",
                temperature.ToString("R"),
                timeoutSeconds.ToString(),
                writerTimeoutSeconds.ToString(),
                maxOutputTokens.ToString(),
                overrideSystemPrompt ? "1" : "0",
                systemPromptOverride ?? "",
                extraPrompt ?? ""
            });
        }

        private static float Clamp(float value, float min, float max) =>
            value < min ? min : (value > max ? max : value);

        private static int Clamp(int value, int min, int max) =>
            value < min ? min : (value > max ? max : value);
    }
}