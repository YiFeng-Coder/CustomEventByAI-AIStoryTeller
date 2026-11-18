using System;
using System.Text;
using System.Text.Json;
using RimWorld;

namespace Stellaris.AI
{
    public enum ProposalKind
    {
        Invalid = 0,
        Rest,
        Incident,

        /// <summary>要现场写一段 Lua 剧情（需要第二次调用）。</summary>
        LuaEvent,

        /// <summary>先查一组 DefName 再继续决策。</summary>
        Lookup
    }

    public sealed class Proposal
    {
        public ProposalKind Kind = ProposalKind.Invalid;
        public string DefName = "";
        public string Category = "";
        public float PointsMultiplier = 1f;
        public string Title = "";
        public string Concept = "";
        public string Reason = "";
        public string Announce = "";
        public string Error = "";
        public string Raw = "";
        public DefLookupRequest Lookup;
    }

    /// <summary>
    /// 解析导演回复。只接受四种结构：
    ///   {"decision":"no_event","reason":"..."}
    ///   {"decision":"vanilla_incident","defName":"...","points_multiplier":1.0,"reason":"...","announce":"..."}
    ///   {"decision":"lua_event","title":"...","concept":"..."}
    ///   {"decision":"lookup","lookup":{"kind":"incident","q":"...","limit":8}}
    /// 其它一律判非法——宁可休整，也不猜。
    /// </summary>
    public static class ProposalParser
    {
        public static Proposal Parse(string text)
        {
            var p = new Proposal { Raw = Truncate(text, 2000) };

            if (string.IsNullOrWhiteSpace(text))
            {
                p.Error = "empty response";
                return p;
            }

            string json = JsonRepairer.ExtractJsonObject(text);
            if (string.IsNullOrEmpty(json))
            {
                p.Error = "no json object in response";
                return p;
            }

            try
            {
                return ParseObject(json, p);
            }
            catch (JsonException e)
            {
                p.Error = "json error: " + e.Message;
                return p;
            }
        }

        private static Proposal ParseObject(string json, Proposal p)
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                p.Error = "root is not an object";
                return p;
            }

            string decision = GetString(root, "decision").Trim().ToLowerInvariant();
            if (decision.Length == 0) decision = GetString(root, "action").Trim().ToLowerInvariant();

            p.Reason = Truncate(GetString(root, "reason"), 240);
            p.Announce = Truncate(GetString(root, "announce"), 240);

            if (decision == "no_event" || decision == "rest" || decision == "none")
            {
                p.Kind = ProposalKind.Rest;
                if (p.Reason.Length == 0) p.Reason = Loc.Pick("(模型未给出理由)", "(the model gave no reason)");
                return p;
            }

            if (decision == "lookup" || decision == "query")
            {
                p.Kind = ProposalKind.Lookup;
                p.Lookup = DefLookupRequest.FromJson(root, out string lookupError);
                if (p.Lookup == null)
                {
                    p.Kind = ProposalKind.Invalid;
                    p.Error = lookupError ?? Loc.Pick("lookup 查询缺少 kind", "the lookup query is missing kind");
                }

                return p;
            }

            if (decision == "lua_event" || decision == "story")
            {
                p.Kind = ProposalKind.LuaEvent;
                p.Title = Truncate(GetString(root, "title"), 40);
                p.Concept = Truncate(GetString(root, "concept"), 600);
                if (p.Concept.Length == 0) p.Concept = p.Reason;
                if (p.Title.Length == 0) p.Title = Loc.Pick("未命名剧情", "Untitled story");
                if (p.Concept.Length == 0)
                {
                    p.Kind = ProposalKind.Invalid;
                    p.Error = Loc.Pick("lua_event 缺少 concept", "lua_event is missing concept");
                }

                return p;
            }

            if (decision != "vanilla_incident" && decision != "incident" && decision != "fire")
            {
                p.Error = "unknown decision: " + decision;
                return p;
            }

            p.DefName = GetString(root, "defName").Trim();
            if (p.DefName.Length == 0) p.DefName = GetString(root, "def").Trim();
            if (p.DefName.Length == 0)
            {
                p.Kind = ProposalKind.Invalid;
                p.Error = "missing defName";
                return p;
            }

            p.Category = GetString(root, "category").Trim();

            float multiplier = GetFloat(root, "points_multiplier", 1f);
            if (multiplier < 0.3f) multiplier = 0.3f;
            if (multiplier > 2.0f) multiplier = 2.0f;
            p.PointsMultiplier = multiplier;

            p.Kind = ProposalKind.Incident;
            return p;
        }

        /// <summary>
        /// 解析第二次调用（写 Lua）的回复。两种格式都接受：
        ///
        ///   1) TITLE: 标题
        ///      ```lua
        ///      function run(ctx, state) ... end
        ///      ```
        ///
        ///   2) {"title":"...","lua":"..."}
        ///
        /// 正常路径是 1)：编码请求时已经关掉了 JSON 模式（见 DirectorLoop.DispatchWriter），
        /// 让模型按提示词写代码块，避免把脚本塞进 JSON 字符串时的转义地狱。
        /// 2) 是兜底：万一服务商仍然强制 JSON，就尽量把脚本从它自己起的键名里捞出来。
        /// </summary>
        public static bool TryParseWriter(string text, out string title, out string lua, out string error)
        {
            title = "";
            lua = "";
            error = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                error = Loc.Pick("空回复", "Empty response");
                return false;
            }

            string jsonError = null;
            bool looksJson = LooksLikeJson(text);

            // 以 { 开头说明这是 JSON 模式下的产物：必须先按 JSON 解析。
            // 反过来先跑代码块提取器的话，JSON 字符串里的 "```lua" 会被当成真围栏，
            // 从字符串中间切出一段半截脚本（或者干脆切空）。
            if (!looksJson && TryExtractFenced(text, out string fencedTitle, out string fencedScript))
            {
                title = TrimTitle(fencedTitle);
                lua = fencedScript;
                return ValidateScript(lua, ref title, ref error);
            }

            if (TryExtractJson(text, out string jsonTitle, out string jsonScript, out jsonError))
            {
                title = TrimTitle(jsonTitle);
                lua = jsonScript;
                return ValidateScript(lua, ref title, ref error);
            }

            // JSON 不成立（比如字符串里写了裸换行）时，再退回围栏提取。
            if (looksJson && TryExtractFenced(text, out fencedTitle, out fencedScript))
            {
                title = TrimTitle(fencedTitle);
                lua = fencedScript;
                return ValidateScript(lua, ref title, ref error);
            }

            error = jsonError ?? Loc.Pick("回复里既没有 ```lua 代码块，也没有可解析的 JSON",
                "the response has neither a ```lua code block nor parseable JSON");
            return false;
        }

        private static bool ValidateScript(string script, ref string title, ref string error)
        {
            if (string.IsNullOrWhiteSpace(script))
            {
                error = Loc.Pick("脚本为空", "The script is empty");
                return false;
            }

            if (!script.Contains("function"))
            {
                error = Loc.Pick("提取到的内容不像 Lua 脚本（没有 function）",
                    "The extracted content does not look like a Lua script (no function)");
                return false;
            }

            if (script.Length > 64 * 1024)
            {
                error = Loc.Pick("脚本超过 64KB", "The script is larger than 64KB");
                return false;
            }

            if (string.IsNullOrEmpty(title)) title = Loc.Pick("未命名剧情", "Untitled story");
            return true;
        }

        private static string TrimTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return "";
            title = title.Trim().Trim('"', '“', '”', '\'', '：', ':');
            return title.Length > 40 ? title.Substring(0, 40) : title;
        }

        /// <summary>从 ```lua ... ``` 代码块里取脚本；标题取代码块之前的 TITLE/标题 行。</summary>
        private static bool TryExtractFenced(string text, out string title, out string script)
        {
            title = "";
            script = "";

            int fence = text.IndexOf("```lua", StringComparison.OrdinalIgnoreCase);
            int tagEnd;

            if (fence >= 0)
            {
                tagEnd = fence + 6;
            }
            else
            {
                fence = text.IndexOf("```", StringComparison.Ordinal);
                if (fence < 0) return false;
                tagEnd = fence + 3;
            }

            int bodyStart = text.IndexOf('\n', tagEnd);

            if (bodyStart < 0)
            {
                // 单行围栏：```lua function run(...) ... —— 围栏标记后面直接就是代码，
                // 没有换行可找。输出被截断时也常长这样，不能直接判"没有代码块"。
                string body = text.Substring(tagEnd);
                int closeTag = body.IndexOf("```", StringComparison.Ordinal);
                if (closeTag >= 0) body = body.Substring(0, closeTag);
                script = body.Trim();
            }
            else
            {
                int bodyEnd = text.IndexOf("```", bodyStart + 1, StringComparison.Ordinal);

                // 没有闭合围栏通常是输出被 token 上限截断：照样把剩余内容当脚本，
                // 让编译错误去说明问题，比笼统报"没有代码块"有用得多。
                script = bodyEnd < 0
                    ? text.Substring(bodyStart + 1).Trim()
                    : text.Substring(bodyStart + 1, bodyEnd - bodyStart - 1).Trim();
            }

            string head = text.Substring(0, fence);
            foreach (string rawLine in head.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                int colon = line.IndexOf(':');
                int cjkColon = line.IndexOf('：');
                if (cjkColon >= 0 && (colon < 0 || cjkColon < colon)) colon = cjkColon;
                if (colon < 0) continue;

                string key = line.Substring(0, colon).Trim();
                if (string.Equals(key, "title", StringComparison.OrdinalIgnoreCase) ||
                    key == "标题")
                {
                    title = line.Substring(colon + 1).Trim();
                }
            }

            return true;
        }

        private static bool TryExtractJson(string text, out string title, out string lua, out string error)
        {
            title = "";
            lua = "";
            error = null;

            string json = JsonRepairer.ExtractJsonObject(text);
            if (string.IsNullOrEmpty(json))
            {
                error = Loc.Pick("回复里没有 JSON 对象", "The response contains no JSON object");
                return false;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    error = Loc.Pick("JSON 根不是对象", "The JSON root is not an object");
                    return false;
                }

                title = GetString(root, "title");
                if (title.Length == 0) title = GetString(root, "标题");

                lua = FindScript(root, 0);

                if (lua.Length == 0)
                {
                    error = Loc.Pick("JSON 里缺少 lua 字段（实际字段：" + ListKeys(root) + "）",
                        "The JSON has no lua field (actual fields: " + ListKeys(root) + ")");
                    return false;
                }

                return true;
            }
            catch (JsonException e)
            {
                error = Loc.Pick("JSON 解析失败：" + e.Message,
                    "Failed to parse the JSON: " + e.Message);
                return false;
            }
        }

        private static bool LooksLikeJson(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int i = 0;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            return i < text.Length && text[i] == '{';
        }

        /// <summary>JSON 模式下的应答没有被给过 schema，lua 的键名是模型自己起的。</summary>
        private static readonly string[] ScriptKeys =
        {
            "lua", "script", "code", "lua_code", "luacode", "script_code", "脚本", "代码"
        };

        /// <summary>
        /// 在 JSON 里找脚本。顺序：常见键名（忽略大小写）→ 任何看起来像脚本的字符串 →
        /// 嵌在下一层的对象（{"result":{"lua":...}} 这种包装）。
        /// </summary>
        private static string FindScript(JsonElement root, int depth)
        {
            foreach (string key in ScriptKeys)
            {
                foreach (JsonProperty p in root.EnumerateObject())
                {
                    if (!string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) continue;

                    string value = NormalizeScript(AsString(p.Value));
                    if (value.Length > 0) return value;
                }
            }

            foreach (JsonProperty p in root.EnumerateObject())
            {
                string value = AsString(p.Value);
                if (value.Length == 0) continue;
                if (value.IndexOf("```lua", StringComparison.OrdinalIgnoreCase) < 0 &&
                    value.IndexOf("function run", StringComparison.OrdinalIgnoreCase) < 0 &&
                    value.IndexOf("function(", StringComparison.Ordinal) < 0) continue;

                value = NormalizeScript(value);
                if (value.Length > 0) return value;
            }

            if (depth < 3)
            {
                foreach (JsonProperty p in root.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Object) continue;

                    string value = FindScript(p.Value, depth + 1);
                    if (value.Length > 0) return value;
                }
            }

            return "";
        }

        /// <summary>字符串值；字符串数组按行拼起来（有的模型会把脚本拆成多行数组）。</summary>
        private static string AsString(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String:
                    return el.GetString() ?? "";

                case JsonValueKind.Array:
                {
                    var sb = new StringBuilder();
                    foreach (JsonElement item in el.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String) continue;
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(item.GetString());
                    }

                    return sb.ToString();
                }

                default:
                    return "";
            }
        }

        /// <summary>
        /// 去掉脚本外面可能残留的 ``` 围栏。JSON 模式会被模型理解成「把代码块塞进字符串」，
        /// 于是 lua 的值常常是 "```lua\n...\n```" —— 直接拿去编译必炸。
        /// </summary>
        private static string NormalizeScript(string script)
        {
            if (string.IsNullOrEmpty(script)) return "";

            script = script.Trim();
            int open = script.IndexOf("```", StringComparison.Ordinal);
            if (open < 0) return script;

            int bodyStart = script.IndexOf('\n', open);
            string body;

            if (bodyStart < 0)
            {
                // 单行形式 "```luafunction run..."：跳过语言名。
                body = script.Substring(open + 3).TrimStart();
                int i = 0;
                while (i < body.Length && char.IsLetter(body[i])) i++;
                body = body.Substring(i);
            }
            else
            {
                body = script.Substring(bodyStart + 1);
            }

            int close = body.IndexOf("```", StringComparison.Ordinal);
            if (close >= 0) body = body.Substring(0, close);

            return body.Trim();
        }

        /// <summary>把顶层键名列表拼出来，出错时写进日志——不用再猜模型这次起了什么名字。</summary>
        private static string ListKeys(JsonElement root)
        {
            var sb = new StringBuilder();
            int count = 0;

            foreach (JsonProperty p in root.EnumerateObject())
            {
                if (count > 0) sb.Append(", ");
                sb.Append(p.Name);
                count++;
                if (count >= 8)
                {
                    sb.Append(", …");
                    break;
                }
            }

            return sb.Length == 0 ? Loc.Pick("(空对象)", "(empty object)") : sb.ToString();
        }

        private static string GetString(JsonElement root, string name)        {
            if (!root.TryGetProperty(name, out JsonElement el)) return "";
            switch (el.ValueKind)
            {
                case JsonValueKind.String: return el.GetString() ?? "";
                case JsonValueKind.Number: return el.GetRawText();
                case JsonValueKind.True: return "true";
                case JsonValueKind.False: return "false";
                default: return "";
            }
        }

        private static float GetFloat(JsonElement root, string name, float fallback)
        {
            if (!root.TryGetProperty(name, out JsonElement el)) return fallback;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetSingle(out float f)) return f;
            if (el.ValueKind == JsonValueKind.String &&
                float.TryParse(el.GetString(), out float parsed)) return parsed;
            return fallback;
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace('\r', ' ').Replace('\n', ' ');
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }
    }
}
