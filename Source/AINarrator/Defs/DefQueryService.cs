using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using RimWorld;
using Verse;

namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>一次 Def 查询请求。字段全部是纯数据，可从 JSON 解析。</summary>
    public sealed class DefLookupRequest
    {
        public string Kind = "";
        public string Query = "";
        public int Limit = 8;
        public int Offset;
        public string Category = "";
        public float MinPoints;
        public string Raw = "";
        public string Error = "";

        public void Normalize()
        {
            Kind = (Kind ?? "").Trim().ToLowerInvariant();
            Query = (Query ?? "").Trim();
            if (Query.Length > 64) Query = Query.Substring(0, 64);
            Category = (Category ?? "").Trim();
            Limit = Limit <= 0 ? 8 : Math.Max(1, Limit);
            if (Offset < 0) Offset = 0;
            if (MinPoints < 0f) MinPoints = 0f;
        }

        public static DefLookupRequest FromJson(JsonElement root, out string error)
        {
            error = null;
            var req = new DefLookupRequest();
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = Loc.Pick("查询必须是一个 JSON 对象", "The query must be a JSON object");
                return null;
            }

            // 允许 {"kind":...} 与 {"lookup":{"kind":...}} 两种包装。
            if (root.TryGetProperty("lookup", out JsonElement lookup) &&
                lookup.ValueKind == JsonValueKind.Object)
            {
                root = lookup;
            }

            req.Kind = GetString(root, "kind");
            if (req.Kind.Length == 0) req.Kind = GetString(root, "type");
            req.Query = GetString(root, "q");
            if (req.Query.Length == 0) req.Query = GetString(root, "query");
            req.Category = GetString(root, "category");
            req.Limit = GetInt(root, "limit", 8);
            req.Offset = GetInt(root, "offset", 0);
            req.MinPoints = GetFloat(root, "min_points", GetFloat(root, "minPoints", 0f));
            req.Raw = root.GetRawText();
            req.Normalize();

            if (req.Kind.Length == 0)
            {
                error = Loc.Pick("查询缺少 kind（例如 item / weapon / incident）",
                    "The query is missing kind (for example item / weapon / incident)");
                return null;
            }

            if (DefKindRegistry.Get(req.Kind) == null)
            {
                error = Loc.Pick("未知的 Def 类别：" + req.Kind + "；可用类别：" + DefKindRegistry.ValidIds(),
                    "Unknown Def kind: " + req.Kind + "; available kinds: " + DefKindRegistry.ValidIds());
                return null;
            }

            return req;
        }

        private static string GetString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out JsonElement el)) return "";
            switch (el.ValueKind)
            {
                case JsonValueKind.String: return el.GetString() ?? "";
                case JsonValueKind.Number: return el.GetRawText();
                default: return "";
            }
        }

        private static int GetInt(JsonElement root, string name, int fallback)
        {
            if (!root.TryGetProperty(name, out JsonElement el)) return fallback;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int i)) return i;
            if (el.ValueKind == JsonValueKind.String &&
                int.TryParse(el.GetString(), out int parsed)) return parsed;
            return fallback;
        }

        private static float GetFloat(JsonElement root, string name, float fallback)
        {
            if (!root.TryGetProperty(name, out JsonElement el)) return fallback;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetSingle(out float f)) return f;
            if (el.ValueKind == JsonValueKind.String &&
                float.TryParse(el.GetString(), out float parsed)) return parsed;
            return fallback;
        }
    }

    public sealed class DefLookupResult
    {
        public bool Ok;
        public string Text = "";
        public string Error = "";
        public int Total;
        public List<DefEntry> Entries = new List<DefEntry>();

        public static DefLookupResult Failure(string error) =>
            new DefLookupResult
            {
                Ok = false,
                Error = error ?? "",
                Text = Loc.Pick("查询失败：" + (error ?? ""), "Query failed: " + (error ?? ""))
            };
    }

    public sealed class DefResolution
    {
        public bool Ok;
        public string DefName = "";
        public int Total;
        public string Candidates = "";
        public string Error = "";
    }

    /// <summary>
    /// 查询入口：普通 Def 走 DefIndex，incident 走 CandidateTable 的运行时过滤。
    /// 所有方法只允许在主线程调用（会访问 DefDatabase / Find / Map）。
    /// </summary>
    public static class DefQueryService
    {
        public static DefLookupResult Search(DefLookupRequest request, Map map, NarratorState state,
            AINarratorSettings settings, int now)
        {
            try
            {
                return SearchCore(request, map, state, settings, now);
            }
            catch (Exception e)
            {
                return DefLookupResult.Failure(Loc.Pick(
                    "查询异常：" + e.GetType().Name + ": " + e.Message,
                    "Lookup error: " + e.GetType().Name + ": " + e.Message));
            }
        }

        private static DefLookupResult SearchCore(DefLookupRequest request, Map map, NarratorState state,
            AINarratorSettings settings, int now)
        {
            if (request == null)
            {
                return DefLookupResult.Failure(Loc.Pick("查询为空", "The query is empty"));
            }
            request.Normalize();

            if (DefKindRegistry.Get(request.Kind) == null)
            {
                return DefLookupResult.Failure(Loc.Pick(
                    "未知的 Def 类别：" + request.Kind + "；可用类别：" + DefKindRegistry.ValidIds(),
                    "Unknown Def kind: " + request.Kind + "; available kinds: "
                    + DefKindRegistry.ValidIds()));
            }

            if (string.Equals(request.Kind, "incident", StringComparison.OrdinalIgnoreCase))
            {
                return SearchIncidents(request, map, state, settings, now);
            }

            List<DefEntry> page = DefIndex.Search(request.Kind, request.Query,
                request.Limit, request.Offset, out int total, out string error);
            if (error != null) return DefLookupResult.Failure(error);

            return new DefLookupResult
            {
                Ok = true,
                Total = total,
                Entries = page,
                Text = DefIndex.Render(request.Kind, request.Query, page, total, request.Offset)
            };
        }

        /// <summary>把 @kind/query 解析成一个真实 defName。返回 total 供 strict 模式判断歧义。</summary>
        public static DefResolution ResolveOne(string kindId, string query, Map map, NarratorState state,
            AINarratorSettings settings, int now)
        {
            var resolution = new DefResolution();
            DefKind kind = DefKindRegistry.Get(kindId);
            if (kind == null)
            {
                resolution.Error = Loc.Pick(
                    "未知的 Def 类别：" + (kindId ?? "") + "；可用类别：" + DefKindRegistry.ValidIds(),
                    "Unknown Def kind: " + (kindId ?? "") + "; available kinds: "
                    + DefKindRegistry.ValidIds());
                return resolution;
            }

            var request = new DefLookupRequest
            {
                Kind = kind.Id,
                Query = query,
                Limit = 5,
                Offset = 0
            };

            DefLookupResult result = Search(request, map, state, settings, now);
            resolution.Total = result.Total;
            resolution.Candidates = result.Text;

            if (!result.Ok)
            {
                resolution.Error = result.Error;
                return resolution;
            }

            if (result.Total <= 0 || result.Entries.Count == 0)
            {
                if (kind.Id == "incident")
                {
                    resolution.Error = Loc.Pick(
                        "@incident/" + (query ?? "") + " 没有匹配且当前可触发的原版事件。"
                        + "可用本次提示词中的精确 DefName。没有适合的候选时，请移除 incident.fire，改用其它有效动作。",
                        "@incident/" + (query ?? "")
                        + " matches no vanilla incident that can fire right now. Use an exact DefName from "
                        + "this prompt. If nothing fits, remove incident.fire and use another valid action.");
                    return resolution;
                }

                string examples = DefIndex.ExamplesText(kind.Id, 5);
                resolution.Error = Loc.Pick(
                    "@" + kind.Id + "/" + (query ?? "") + " 没有匹配。"
                    + (examples.Length > 0 ? " " + kind.Label + " 的示例：" + examples + "。" : "")
                    + "请换更短的关键词，或先用 LOOKUP 查一次。",
                    "No match for @" + kind.Id + "/" + (query ?? "") + ". "
                    + (examples.Length > 0 ? "Examples of " + kind.Label + ": " + examples + ". " : "")
                    + "Try a shorter keyword, or run a LOOKUP first.");
                return resolution;
            }

            resolution.Ok = true;
            resolution.DefName = result.Entries[0].DefName;
            return resolution;
        }

        // ─────────────────────────────────────────────────────────────────────
        // incident：必须在"此刻真的能触发 + 预算允许"的集合里查
        // ─────────────────────────────────────────────────────────────────────

        private static DefLookupResult SearchIncidents(DefLookupRequest request, Map map,
            NarratorState state, AINarratorSettings settings, int now)
        {
            if (map == null)
            {
                return DefLookupResult.Failure(Loc.Pick("没有玩家基地地图，无法查询事件",
                    "There is no player colony map, so incidents cannot be looked up"));
            }

            float points = StorytellerUtility.DefaultThreatPointsNow(map);
            string normalized = DefEntry.NormalizeText(request.Query);
            string[] tokens = normalized.Length == 0
                ? new string[0]
                : normalized.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            var matches = new List<(DefEntry Entry, int Score)>();
            IReadOnlyList<IncidentCandidate> all = CandidateTable.All;

            for (int i = 0; i < all.Count; i++)
            {
                IncidentCandidate candidate = all[i];
                if (candidate == null || candidate.Def == null || candidate.Def.Worker == null) continue;
                if (candidate.MinPoints > points) continue;
                if (!string.IsNullOrEmpty(request.Category) &&
                    !string.Equals(candidate.Category, request.Category, StringComparison.OrdinalIgnoreCase)) continue;
                if (request.MinPoints > 0f && candidate.MinPoints < request.MinPoints) continue;

                DefEntry entry = DefEntry.FromIncident(candidate);
                if (entry == null) continue;

                int score = tokens.Length == 0 ? 0 : MatchScore(entry, normalized, tokens);
                if (score < 0) continue;

                try
                {
                    if (!candidate.Def.TargetAllowed(map)) continue;

                    IncidentParms parms = StorytellerUtility.DefaultParmsNow(candidate.Def.category, map);
                    if (parms == null) continue;
                    if (!candidate.Def.Worker.CanFireNow(parms)) continue;

                    if (!BudgetPolicy.CanUse(candidate, state, now, settings, out _)) continue;
                }
                catch
                {
                    continue;
                }

                matches.Add((entry, score));
            }

            // 相关度优先。同分时按原有的类别、最低点数降序和 defName 排序。
            matches.Sort((a, b) =>
            {
                int byScore = b.Score.CompareTo(a.Score);
                if (byScore != 0) return byScore;
                int byCategory = CategoryRank(a.Entry.Category).CompareTo(CategoryRank(b.Entry.Category));
                if (byCategory != 0) return byCategory;
                int byPoints = b.Entry.Numeric.CompareTo(a.Entry.Numeric);
                if (byPoints != 0) return byPoints;
                return string.CompareOrdinal(a.Entry.DefName, b.Entry.DefName);
            });

            int total = matches.Count;
            int start = Math.Min(request.Offset, matches.Count);
            int take = Math.Min(request.Limit, matches.Count - start);
            var page = new List<DefEntry>(take);
            for (int i = 0; i < take; i++) page.Add(matches[start + i].Entry);

            return new DefLookupResult
            {
                Ok = true,
                Total = total,
                Entries = page,
                Text = DefIndex.Render("incident", request.Query, page, total, request.Offset)
            };
        }

        private static int CategoryRank(string category)
        {
            if (string.Equals(category, "ThreatBig", StringComparison.Ordinal)) return 0;
            if (string.Equals(category, "ThreatSmall", StringComparison.Ordinal)) return 1;
            if (string.Equals(category, "Misc", StringComparison.Ordinal)) return 2;
            return 3;
        }

        private static int MatchScore(DefEntry entry, string query, string[] tokens)
        {
            if (string.Equals(entry.NDef, query, StringComparison.Ordinal)) return int.MaxValue;
            if (string.Equals(entry.NLabel, query, StringComparison.Ordinal)) return int.MaxValue - 1;

            int total = 0;
            for (int i = 0; i < tokens.Length; i++)
            {
                int best = TokenScore(entry, tokens[i]);
                if (best <= 0) return -1;
                total += best;
            }

            return total;
        }

        private static int TokenScore(DefEntry entry, string token)
        {
            if (string.Equals(entry.NDef, token, StringComparison.Ordinal)) return 1000;
            if (string.Equals(entry.NLabel, token, StringComparison.Ordinal)) return 950;
            if (entry.NDef.StartsWith(token, StringComparison.Ordinal)) return 800;
            if (entry.NLabel.StartsWith(token, StringComparison.Ordinal)) return 750;
            if (entry.NDef.IndexOf(token, StringComparison.Ordinal) >= 0) return 600;
            if (entry.NLabel.IndexOf(token, StringComparison.Ordinal) >= 0) return 550;
            if (entry.NDesc.IndexOf(token, StringComparison.Ordinal) >= 0) return 100;
            return 0;
        }
    }
}
