namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>
    /// 一家 OpenAI 兼容服务商的预置。
    ///
    /// 数据来源（2026-11-17 实测，不是凭记忆写的）：
    ///  * 模型名：DeepSeek / Kimi / 智谱 / 通义 / 硅基流动 / Ollama 的模型名取自各自官方文档页；
    ///    OpenAI 的模型名经 OpenRouter 公开目录反查（platform.openai.com 对本机返回 403）；
    ///  * 接口地址：逐个 POST 空请求探测，返回 401/400 表示路径存在但未鉴权，000 表示本机不可达。
    ///
    /// 模型名会随厂商迭代变化，玩家仍可在设置页手输。
    /// </summary>
    public sealed class LlmProviderPreset
    {
        public readonly string Id;
        public readonly string Endpoint;
        public readonly string[] Models;

        /// <summary>中文名（品牌原名 + 中文习惯称呼）。</summary>
        public readonly string LabelZh;

        /// <summary>英文名。品牌名保持原样，中文习惯称呼换成英文说法。</summary>
        public readonly string LabelEn;

        /// <summary>
        /// 显示名。刻意做成属性而不是字段：取名字时才判语言，
        /// 这样无论 Mod 构造与语言加载谁先，显示都不会锁死在一种语言上。
        /// </summary>
        public string Label => Loc.Pick(LabelZh, LabelEn);

        public LlmProviderPreset(string id, string labelZh, string labelEn, string endpoint, string[] models)
        {
            Id = id;
            LabelZh = labelZh;
            LabelEn = labelEn;
            Endpoint = endpoint;
            Models = models ?? new string[0];
        }

        public string DefaultModel => Models.Length > 0 ? Models[0] : "";
    }

    public static class LlmProviderPresets
    {
        public static readonly LlmProviderPreset[] All =
        {
            // 文档原文：「模型名请使用 deepseek-flash」；deepseek-v4-pro 仍在售。
            new LlmProviderPreset("deepseek", "DeepSeek 深度求索", "DeepSeek",
                "https://api.deepseek.com/chat/completions",
                new[] { "deepseek-flash", "deepseek-v4-pro" }),

            // platform.openai.com 对本机 403，模型名来自 OpenRouter 目录里的 openai/* 条目。
            new LlmProviderPreset("openai", "OpenAI", "OpenAI",
                "https://api.openai.com/v1/chat/completions",
                new[] { "gpt-5-mini", "gpt-5", "gpt-5-nano", "gpt-4.1-mini" }),

            // 定价页实测：kimi-k3.1-pro / kimi-k3 / kimi-k2.7-code / kimi-k2.6。
            new LlmProviderPreset("moonshot", "月之暗面 Kimi", "Moonshot Kimi",
                "https://api.moonshot.cn/v1/chat/completions",
                new[] { "kimi-k3.1-pro", "kimi-k3", "kimi-k2.7-code", "kimi-k2.6" }),

            // 文档侧栏：GLM-5.3-Flash/FlashX、GLM-5.3、GLM-5.2。
            new LlmProviderPreset("zhipu", "智谱 GLM", "Zhipu GLM",
                "https://open.bigmodel.cn/api/paas/v4/chat/completions",
                new[] { "glm-5.3-flash", "glm-5.3", "glm-5.3-flashx", "glm-5.2" }),

            // 百炼「选择模型」页：qwen4-max（复杂推理）、qwen3.7-plus（均衡）、qwen3.8-flash（低成本）。
            new LlmProviderPreset("qwen", "阿里通义千问", "Alibaba Qwen",
                "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
                new[] { "qwen3.7-plus", "qwen4-max", "qwen3.8-flash" }),

            // 硅基流动模型页实测到的 id。
            new LlmProviderPreset("siliconflow", "硅基流动 SiliconFlow", "SiliconFlow",
                "https://api.siliconflow.cn/v1/chat/completions",
                new[] { "deepseek-ai/DeepSeek-V4.1-Flash", "deepseek-ai/DeepSeek-V4-Pro", "Qwen/Qwen3.8-27B" }),

            // OpenRouter 的 /api/v1/models 公开目录（470 个模型）里挑的几条。
            new LlmProviderPreset("openrouter", "OpenRouter", "OpenRouter",
                "https://openrouter.ai/api/v1/chat/completions",
                new[] { "openai/gpt-5-mini", "anthropic/claude-sonnet-5", "google/gemini-3.5-flash", "deepseek/deepseek-v4-flash" }),

            // ollama.com/library 实测存在的名字。
            new LlmProviderPreset("ollama", "Ollama（本地）", "Ollama (local)",
                "http://127.0.0.1:11434/v1/chat/completions",
                new[] { "qwen3", "llama3.3", "deepseek-v4-pro", "gemma4" }),

            // 文档站 403，模型名未能验证，留空让玩家按自己账号可用列表填。
            new LlmProviderPreset("groq", "Groq", "Groq",
                "https://api.groq.com/openai/v1/chat/completions",
                new string[0]),

            // LM Studio 的模型 id 取决于本地加载了哪个模型，没有固定值。
            new LlmProviderPreset("lmstudio", "LM Studio（本地）", "LM Studio (local)",
                "http://127.0.0.1:1234/v1/chat/completions",
                new string[0]),

            new LlmProviderPreset("custom", "自定义 / OpenAI 兼容", "Custom / OpenAI-compatible", "", new string[0])
        };

        public static int IndexOf(string id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                for (int i = 0; i < All.Length; i++)
                {
                    if (All[i].Id == id) return i;
                }
            }

            return All.Length - 1;
        }

        public static LlmProviderPreset Get(int index)
        {
            if (index < 0 || index >= All.Length) return All[All.Length - 1];
            return All[index];
        }

        /// <summary>按当前 endpoint 反推是哪家，用于 UI 显示。</summary>
        public static int Detect(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return All.Length - 1;

            string e = endpoint.Trim().ToLowerInvariant();
            for (int i = 0; i < All.Length; i++)
            {
                LlmProviderPreset p = All[i];
                if (string.IsNullOrEmpty(p.Endpoint)) continue;
                string host = HostOf(p.Endpoint);
                if (host.Length > 0 && e.Contains(host)) return i;
            }

            return All.Length - 1;
        }

        private static string HostOf(string url)
        {
            int scheme = url.IndexOf("://", System.StringComparison.Ordinal);
            if (scheme < 0) return "";
            string rest = url.Substring(scheme + 3);
            int slash = rest.IndexOf('/');
            if (slash >= 0) rest = rest.Substring(0, slash);
            return rest.ToLowerInvariant();
        }
    }
}
