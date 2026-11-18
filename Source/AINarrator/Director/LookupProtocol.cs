using System;
using System.Text.Json;

namespace Stellaris.AI
{
    /// <summary>
    /// 编剧侧的查询协议：单独一行 <c>LOOKUP: {...}</c>。
    /// 导演侧是 JSON 的 decision=lookup，由 ProposalParser 解析。
    /// </summary>
    public static class LookupProtocol
    {
        public static bool TryParseWriter(string text, out DefLookupRequest request, out string error)
        {
            request = null;
            error = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string first = FirstNonEmptyLine(text);
            if (first.Length == 0) return false;

            int colon = first.IndexOf(':');
            int cjkColon = first.IndexOf('：');
            if (cjkColon >= 0 && (colon < 0 || cjkColon < colon)) colon = cjkColon;
            if (colon < 0) return false;

            string head = first.Substring(0, colon).Trim();
            if (!string.Equals(head, "LOOKUP", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(head, "QUERY", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(head, "查询", StringComparison.Ordinal))
            {
                return false;
            }

            string payload = first.Substring(colon + 1).Trim();
            if (payload.Length == 0)
            {
                error = Loc.Pick("LOOKUP: 后面缺少 JSON，例如 LOOKUP: {\"kind\":\"item\",\"q\":\"药\"}",
                    "LOOKUP: is missing its JSON, for example LOOKUP: {\"kind\":\"item\",\"q\":\"medicine\"}");
                return true;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(payload, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

                request = DefLookupRequest.FromJson(doc.RootElement, out error);
                if (request == null)
                {
                    if (string.IsNullOrEmpty(error))
                    {
                        error = Loc.Pick("LOOKUP 的 JSON 不合法", "The LOOKUP JSON is not valid");
                    }
                    return true;
                }

                request.Raw = first;
                return true;
            }
            catch (JsonException e)
            {
                error = Loc.Pick("LOOKUP 的 JSON 解析失败：" + e.Message,
                    "Failed to parse the LOOKUP JSON: " + e.Message);
                return true;
            }
        }

        public static string FormatBlock(int index, DefLookupResult result)
        {
            if (result == null)
            {
                return Loc.Pick("【查询结果 #" + index + "】错误：查询没有返回结果",
                    "[Lookup result #" + index + "] error: the query returned nothing");
            }

            if (!result.Ok)
            {
                return Loc.Pick("【查询结果 #" + index + "】错误：" + result.Error,
                    "[Lookup result #" + index + "] error: " + result.Error);
            }

            return Loc.Pick("【查询结果 #" + index + "】" + result.Text,
                "[Lookup result #" + index + "] " + result.Text);
        }

        private static string FirstNonEmptyLine(string text)
        {
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length > 0) return line;
            }

            return "";
        }
    }
}
