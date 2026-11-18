using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace Stellaris.AI
{
    /// <summary>
    /// Def 查询索引。只读、主线程、按 kind 懒构建。
    ///
    /// 不直接把全量 DefName 塞进 prompt：模型先写 @kind/关键词，或在明确需要挑选时
    /// 发一次 LOOKUP。索引保证同一个查询在同一安装下返回稳定顺序。
    /// </summary>
    public static class DefIndex
    {
        private sealed class ScoredEntry
        {
            public DefEntry Entry;
            public int Score;
        }

        private static readonly Dictionary<string, List<DefEntry>> Cache =
            new Dictionary<string, List<DefEntry>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>清空缓存。开发者模式重载 Defs、或调试窗手动重扫时调用。</summary>
        public static void Rebuild()
        {
            Cache.Clear();
            Verse.Log.Message(AINarratorMod.LogPrefix + "DefIndex rebuilt (" + DefKindRegistry.Count + " kinds)");
        }

        public static int Count(string kindId)
        {
            DefKind kind = DefKindRegistry.Get(kindId);
            if (kind == null) return 0;

            List<DefEntry> entries = GetEntries(kind);
            int n = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (PassesRuntime(kind, entries[i])) n++;
            }

            return n;
        }

        public static List<DefEntry> Examples(string kindId, int count)
        {
            var result = new List<DefEntry>();
            DefKind kind = DefKindRegistry.Get(kindId);
            if (kind == null || count <= 0) return result;

            List<DefEntry> entries = GetEntries(kind);

            // 按来源模组分组：每组先取一个，轮流补。这样示例天然跨模组，
            // 不会变成"Core 广告"。
            var byMod = new Dictionary<string, List<DefEntry>>(StringComparer.Ordinal);
            for (int i = 0; i < entries.Count; i++)
            {
                DefEntry entry = entries[i];
                if (!PassesRuntime(kind, entry)) continue;

                string mod = string.IsNullOrEmpty(entry.Mod) ? "?" : entry.Mod;
                if (!byMod.TryGetValue(mod, out List<DefEntry> list))
                {
                    list = new List<DefEntry>();
                    byMod[mod] = list;
                }

                list.Add(entry);
            }

            var groups = new List<KeyValuePair<string, List<DefEntry>>>(byMod);
            groups.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            for (int round = 0; result.Count < count; round++)
            {
                bool added = false;
                for (int g = 0; g < groups.Count && result.Count < count; g++)
                {
                    List<DefEntry> list = groups[g].Value;
                    if (round >= list.Count) continue;
                    result.Add(list[round]);
                    added = true;
                }

                if (!added) break;
            }

            return result;
        }

        /// <summary>确定性搜索。返回当前页；total 是过滤后的全部命中数。</summary>
        public static List<DefEntry> Search(string kindId, string query, int limit, int offset,
            out int total, out string error)
        {
            total = 0;
            error = null;

            DefKind kind = DefKindRegistry.Get(kindId);
            if (kind == null)
            {
                error = "未知的 Def 类别：" + (kindId ?? "") + "；可用类别：" + DefKindRegistry.ValidIds();
                return new List<DefEntry>();
            }

            if (limit < 1) limit = 1;
            if (offset < 0) offset = 0;

            string normalized = DefEntry.NormalizeText(query);
            string[] tokens = normalized.Length == 0
                ? new string[0]
                : normalized.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            List<DefEntry> entries = GetEntries(kind);
            var scored = new List<ScoredEntry>(entries.Count);

            for (int i = 0; i < entries.Count; i++)
            {
                DefEntry entry = entries[i];
                if (!PassesRuntime(kind, entry)) continue;

                int score = tokens.Length == 0 ? 0 : MatchScore(entry, tokens);
                if (tokens.Length > 0 && score < 0) continue;

                scored.Add(new ScoredEntry { Entry = entry, Score = score });
            }

            // 分数降序；同分按 DefName 序数升序。全部确定性，无 Rand。
            scored.Sort((a, b) =>
            {
                int byScore = b.Score.CompareTo(a.Score);
                if (byScore != 0) return byScore;
                return string.CompareOrdinal(a.Entry.DefName, b.Entry.DefName);
            });

            total = scored.Count;
            int start = Math.Min(offset, scored.Count);
            int take = Math.Min(limit, scored.Count - start);
            var page = new List<DefEntry>(take);
            for (int i = 0; i < take; i++) page.Add(scored[start + i].Entry);
            return page;
        }

        public static string Render(string kindId, string query, List<DefEntry> entries,
            int total, int offset)
        {
            var sb = new StringBuilder(1024);
            int shown = entries != null ? entries.Count : 0;

            sb.Append("kind=").Append(kindId)
              .Append(" q=\"").Append(OneLine(query, 64).Replace('"', '\'')).Append("\"")
              .Append(" total=").Append(total);
            if (shown > 0)
            {
                sb.Append(" showing=").Append(offset + 1).Append('-').Append(offset + shown);
            }

            sb.Append('\n');

            if (total == 0)
            {
                sb.Append("没有匹配。换更短的关键词，或换一个 kind。\n");
                return sb.ToString();
            }

            string normalized = DefEntry.NormalizeText(query);
            for (int i = 0; i < shown; i++)
            {
                DefEntry entry = entries[i];
                bool exact = normalized.Length > 0 &&
                             (string.Equals(entry.NDef, normalized, StringComparison.Ordinal) ||
                              string.Equals(entry.NLabel, normalized, StringComparison.Ordinal));

                sb.Append(exact ? "★ " : (offset + i + 1) + ". ")
                  .Append(entry.DefName);

                if (!string.IsNullOrEmpty(entry.Label) &&
                    !string.Equals(entry.Label, entry.DefName, StringComparison.Ordinal))
                {
                    sb.Append(" | ").Append(entry.Label);
                }

                if (!string.IsNullOrEmpty(entry.Facts))
                {
                    sb.Append(" | ").Append(OneLine(entry.Facts, 100));
                }

                if (!string.IsNullOrEmpty(entry.Mod))
                {
                    sb.Append(" | 来源 ").Append(OneLine(entry.Mod, 60));
                }

                sb.Append('\n');
            }

            if (offset + shown < total)
            {
                sb.Append("更多：offset=").Append(offset + shown)
                  .Append("；换关键词请再查一次。\n");
            }

            return sb.ToString();
        }

        /// <summary>给"查询结果为空"或调试窗用的短候选列表。</summary>
        public static string ExamplesText(string kindId, int count)
        {
            List<DefEntry> examples = Examples(kindId, count);
            if (examples.Count == 0) return "";

            var sb = new StringBuilder(256);
            for (int i = 0; i < examples.Count; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(examples[i].DefName);
                if (!string.IsNullOrEmpty(examples[i].Label) &&
                    !string.Equals(examples[i].Label, examples[i].DefName, StringComparison.Ordinal))
                {
                    sb.Append('(').Append(examples[i].Label).Append(')');
                }
            }

            return sb.ToString();
        }

        /// <summary>查询结果为空时，判断一个精确名字是否在同一类别里（用于更友好的报错）。</summary>
        public static bool ContainsExact(string kindId, string defName)
        {
            if (string.IsNullOrEmpty(defName)) return false;
            DefKind kind = DefKindRegistry.Get(kindId);
            if (kind == null) return false;

            List<DefEntry> entries = GetEntries(kind);
            for (int i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].DefName, defName, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>
        /// 目录：每类只给总数 + 3 个跨模组示例。它不是白名单，真正的白名单是
        /// ActionRegistry.ValidateAll 里的 DefDatabase 查询。
        /// </summary>
        public static string BuildCatalog(ActionCategoryMask mask, LookupSession lookup,
            AINarratorSettings settings)
        {
            var sb = new StringBuilder(1200);
            sb.Append("\n【Def 目录】下面是每类的总数和几个示例（不是全部）。\n");

            bool canLookup = settings != null && settings.enableDefLookup &&
                             lookup != null && lookup.CanLookup(settings);

            if (canLookup)
            {
                sb.Append("需要具体名字时：单独输出一行\n")
                  .Append("LOOKUP: {\"kind\":\"item\",\"q\":\"药\",\"limit\":")
                  .Append(settings != null ? settings.lookupMaxResults : 8).Append("}\n")
                  .Append("我会把匹配结果贴回来（最多 ").Append(lookup.MaxRounds)
                  .Append(" 轮，剩余 ").Append(lookup.RoundsLeft).Append(" 轮）。\n");
            }
            else
            {
                sb.Append("本轮不提供 LOOKUP。可以直接写 @类别/关键词（例如 @item/药），")
                  .Append("或从示例里选精确名字。\n");
            }

            sb.Append("所有 Def 参数都可以写精确 defName，也可以写 @类别/关键词；")
              .Append("类别见每行开头的英文 id。\n");

            List<DefKind> kinds = DefKindRegistry.ForMask(mask);
            if (kinds.Count == 0) return "";

            for (int i = 0; i < kinds.Count; i++)
            {
                DefKind kind = kinds[i];
                int count = Count(kind.Id);
                sb.Append(kind.Label).Append(' ').Append(kind.Id)
                  .Append("：共 ").Append(count).Append(" 个");

                List<DefEntry> examples = Examples(kind.Id, 3);
                if (examples.Count > 0)
                {
                    sb.Append("；例：");
                    for (int e = 0; e < examples.Count; e++)
                    {
                        if (e > 0) sb.Append('、');
                        DefEntry entry = examples[e];
                        sb.Append(entry.DefName);
                        if (!string.IsNullOrEmpty(entry.Label) &&
                            !string.Equals(entry.Label, entry.DefName, StringComparison.Ordinal))
                        {
                            sb.Append('(').Append(entry.Label).Append(')');
                        }

                        if (!string.IsNullOrEmpty(entry.Mod)) sb.Append('，').Append(entry.Mod);
                    }
                }

                sb.Append('\n');
            }

            if (lookup != null && lookup.Blocks.Count > 0)
            {
                sb.Append('\n').Append(lookup.BuildPromptBlock());
            }

            return sb.ToString();
        }

        // ─────────────────────────────────────────────────────────────────────
        // 内部
        // ─────────────────────────────────────────────────────────────────────

        private static List<DefEntry> GetEntries(DefKind kind)
        {
            if (kind.Cacheable && Cache.TryGetValue(kind.Id, out List<DefEntry> cached))
            {
                return cached;
            }

            List<DefEntry> built;
            long started = Environment.TickCount64;
            try
            {
                built = kind.Build != null ? kind.Build() : new List<DefEntry>();
            }
            catch (Exception e)
            {
                Verse.Log.Error(AINarratorMod.LogPrefix + "DefIndex build failed for " + kind.Id + ": " + e);
                built = new List<DefEntry>();
            }

            built.RemoveAll(e => e == null || string.IsNullOrEmpty(e.DefName));
            built.Sort((a, b) => string.CompareOrdinal(a.DefName, b.DefName));

            if (kind.Cacheable) Cache[kind.Id] = built;

            if (AINarratorMod.Settings != null && AINarratorMod.Settings.debugLogging)
            {
                Verse.Log.Message(AINarratorMod.LogPrefix + "DefIndex built " + kind.Id + ": "
                                  + built.Count + " entries in " + (Environment.TickCount64 - started) + "ms");
            }

            return built;
        }

        private static bool PassesRuntime(DefKind kind, DefEntry entry)
        {
            if (kind.RuntimeFilter == null) return true;
            try
            {
                return kind.RuntimeFilter(entry);
            }
            catch
            {
                return false;
            }
        }

        private static int MatchScore(DefEntry entry, string[] tokens)
        {
            int total = 0;
            for (int i = 0; i < tokens.Length; i++)
            {
                int best = TokenScore(entry, tokens[i]);
                if (best <= 0) return -1;
                total += best;
            }

            if (tokens.Length > 1) total += 50;

            string joined = string.Join(" ", tokens);
            if (string.Equals(entry.NDef, joined, StringComparison.Ordinal)) total += 1000;
            else if (string.Equals(entry.NLabel, joined, StringComparison.Ordinal)) total += 950;

            return total;
        }

        private static int TokenScore(DefEntry entry, string token)
        {
            if (token.Length == 0) return 0;

            if (string.Equals(entry.NDef, token, StringComparison.Ordinal)) return 1000;
            if (string.Equals(entry.NLabel, token, StringComparison.Ordinal)) return 950;
            if (entry.NDef.StartsWith(token, StringComparison.Ordinal)) return 800;
            if (entry.NLabel.StartsWith(token, StringComparison.Ordinal)) return 750;
            if (entry.NDef.IndexOf(token, StringComparison.Ordinal) >= 0) return 600;
            if (entry.NLabel.IndexOf(token, StringComparison.Ordinal) >= 0) return 550;
            if (entry.NDesc.IndexOf(token, StringComparison.Ordinal) >= 0) return 100;
            return 0;
        }

        private static string OneLine(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/').Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }
    }
}
