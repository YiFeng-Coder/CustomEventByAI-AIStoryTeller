using System.Text;

namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>
    /// 从模型输出里"捞"出 JSON 对象，并做最小修复。
    /// 大模型常见的三种坏习惯都在这里处理：外面包 ```json 围栏、前后加解释性文字、结尾被截断。
    /// </summary>
    public static class JsonRepairer
    {
        public static string ExtractJsonObject(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            string s = StripFences(text.Trim());

            int start = s.IndexOf('{');
            if (start < 0) return null;

            int depth = 0;
            bool inString = false;
            bool escape = false;

            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];

                if (inString)
                {
                    if (escape) { escape = false; continue; }
                    if (c == '\\') { escape = true; continue; }
                    if (c == '"') inString = false;
                    continue;
                }

                if (c == '"') { inString = true; continue; }
                if (c == '{') { depth++; continue; }

                if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return s.Substring(start, i - start + 1);
                    }
                }
            }

            // 走到这里说明括号没闭合（多半是输出被截断）。
            return RepairTruncated(s.Substring(start), depth, inString);
        }

        private static string StripFences(string text)
        {
            if (!text.Contains("```")) return text;

            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                int fence = text.IndexOf("```", i, System.StringComparison.Ordinal);
                if (fence < 0)
                {
                    sb.Append(text, i, text.Length - i);
                    break;
                }

                // 只认行首的围栏。模型把代码块塞进 JSON 字符串时也会出现 "```lua"
                // （前面是 `"` 而不是换行），那不是围栏；当成围栏剥掉会把整个 lua 字段吞空，
                // 最后只剩下一句误导人的「JSON 里缺少 lua 字段」。
                if (!IsLineStart(text, fence))
                {
                    sb.Append(text, i, fence - i + 3);
                    i = fence + 3;
                    continue;
                }

                sb.Append(text, i, fence - i);

                int lineEnd = text.IndexOf('\n', fence);
                if (lineEnd < 0) break;

                i = lineEnd + 1;
                int close = text.IndexOf("```", i, System.StringComparison.Ordinal);
                if (close < 0)
                {
                    sb.Append(text, i, text.Length - i);
                    break;
                }

                sb.Append(text, i, close - i);
                i = close + 3;
            }

            return sb.ToString();
        }

        private static bool IsLineStart(string text, int index)
        {
            if (index <= 0) return true;
            char prev = text[index - 1];
            return prev == '\n' || prev == '\r';
        }

        private static string RepairTruncated(string partial, int depth, bool inString)
        {
            var sb = new StringBuilder(partial);

            // 去掉结尾可能残留的逗号与空白
            while (sb.Length > 0)
            {
                char last = sb[sb.Length - 1];
                if (last == ' ' || last == '\t' || last == '\r' || last == '\n' || last == ',')
                {
                    sb.Length--;
                    continue;
                }

                break;
            }

            if (inString) sb.Append('"');

            for (int i = 0; i < depth; i++) sb.Append('}');

            return sb.ToString();
        }
    }
}
