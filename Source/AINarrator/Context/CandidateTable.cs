using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace Stellaris.AI
{
    /// <summary>候选表里的一项。缓存字符串，避免每次拼 prompt 时反射。</summary>
    public sealed class IncidentCandidate
    {
        public IncidentDef Def;
        public string DefName;
        public string Category;
        public float MinPoints;
    }

    /// <summary>
    /// 候选事件表。
    ///
    /// 为什么需要它：把 IncidentDef 的名字直接交给模型自由生成，是"幻觉事件"和
    /// "反复刷同一类"的根源。按已加载的 IncidentDef 构建候选，供 prompt 与运行时共同使用。
    ///
    /// 过滤规则（P1）：
    ///  * 类别只保留 ThreatSmall / ThreatBig / Misc —— 疾病、商队、任务仍走原版 comps；
    ///  * 必须有 workerClass；
    ///  * minThreatPoints 不超过当前殖民地的威胁点数（在 ForPoints 里做）。
    /// </summary>
    public static class CandidateTable
    {
        private static readonly List<IncidentCandidate> _all = new List<IncidentCandidate>();
        private static int _scannedDefCount;
        private static bool _initialized;
        private static bool _sawUnresolvedCategory;

        private static readonly string[] AllowedCategories = { "ThreatSmall", "ThreatBig", "Misc" };

        public static IReadOnlyList<IncidentCandidate> All
        {
            get
            {
                // Mod 构造时 DefDatabase 还是空的（那时静态构造只看到 0 个 IncidentDef）。
                // 后续每次读取都按"当前定义数量"检查；同时兜住"定义数量没变、但第一次扫描
                // 发生在 ResolveReferences 之前导致 category 还是 null"的情况。
                int defCount = DefDatabase<IncidentDef>.AllDefsListForReading.Count;
                if (!_initialized || defCount != _scannedDefCount ||
                    (_all.Count == 0 && _sawUnresolvedCategory))
                    Rebuild();
                return _all;
            }
        }

        public static int Count => All.Count;

        public static void Rebuild()
        {
            var defs = DefDatabase<IncidentDef>.AllDefsListForReading;
            _all.Clear();
            _sawUnresolvedCategory = false;

            var byCategory = new Dictionary<string, int>();
            foreach (IncidentDef def in defs)
            {
                if (def == null) continue;
                if (def.category == null)
                {
                    // Def 已经进库、ResolveReferences 还没跑完：这一趟不算数，
                    // 下次读取时再重建一次（只在这一种情况下才允许"空结果重试"）。
                    _sawUnresolvedCategory = true;
                    continue;
                }

                if (def.workerClass == null) continue;

                string category = def.category.defName;
                if (Array.IndexOf(AllowedCategories, category) < 0) continue;

                _all.Add(new IncidentCandidate
                {
                    Def = def,
                    DefName = def.defName,
                    Category = category,
                    MinPoints = def.minThreatPoints
                });

                byCategory.TryGetValue(category, out int n);
                byCategory[category] = n + 1;
            }

            _all.Sort((a, b) => string.CompareOrdinal(a.DefName, b.DefName));
            _scannedDefCount = defs.Count;
            _initialized = true;

            var sb = new StringBuilder();
            sb.Append(AINarratorMod.LogPrefix)
              .Append("candidate incidents=").Append(_all.Count)
              .Append(" (scanned ").Append(defs.Count).Append(" IncidentDefs)")
              .Append(" categories:");
            foreach (var kv in byCategory)
            {
                sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            }

            Verse.Log.Message(sb.ToString());
        }

        /// <summary>按当前威胁点数过滤出"此刻用得起"的候选。</summary>
        public static List<IncidentCandidate> ForPoints(float points)
        {
            IReadOnlyList<IncidentCandidate> candidates = All;
            var list = new List<IncidentCandidate>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                IncidentCandidate c = candidates[i];
                if (c.MinPoints <= points) list.Add(c);
            }

            return list;
        }

        /// <summary>
        /// 真正"此刻能触发"的候选：除了威胁点数，还要过 <c>IncidentDef.TargetAllowed</c>
        /// 与 <c>IncidentWorker.CanFireNow</c>（后者才是权威判断——研究、派系、季节、
        /// 已有的同类事件等等，都由各 worker 自己检查）。
        ///
        /// 这一步在**每次请求前**跑，所以 AI 看到的候选表总是当前局势下真的能落地的。
        /// 结果保持 defName 序；需要"精选给模型看"时用 <see cref="SelectForPrompt"/>。
        /// 这里不再洗牌：洗牌会让同一局势下每次请求看到不同子集，且会消耗全局 Rand。
        /// </summary>
        public static List<IncidentCandidate> Firable(Map map, float points, int max)
        {
            if (map == null) return new List<IncidentCandidate>();

            IReadOnlyList<IncidentCandidate> candidates = All;
            var result = new List<IncidentCandidate>(candidates.Count);

            int rejected = 0;
            string lastError = null;

            for (int i = 0; i < candidates.Count; i++)
            {
                IncidentCandidate c = candidates[i];
                if (c.MinPoints > points) continue;

                try
                {
                    if (!c.Def.TargetAllowed(map)) continue;

                    IncidentParms parms = StorytellerUtility.DefaultParmsNow(c.Def.category, map);
                    if (parms == null)
                    {
                        rejected++;
                        continue;
                    }

                    if (!c.Def.Worker.CanFireNow(parms))
                    {
                        rejected++;
                        continue;
                    }
                }
                catch (Exception e)
                {
                    // 单个 def 的判断炸了不能拖垮整张表，也不能每个 def 刷一条日志。
                    rejected++;
                    lastError = e.GetType().Name;
                    continue;
                }

                result.Add(c);
            }

            if (rejected > 0 && AINarratorMod.Settings != null && AINarratorMod.Settings.debugLogging)
            {
                Verse.Log.Message(AINarratorMod.LogPrefix + "candidates: " + result.Count
                                  + " firable / " + candidates.Count + " total (" + rejected
                                  + " rejected by CanFireNow"
                                  + (lastError != null ? ", last error " + lastError : "") + ")");
            }

            if (max > 0 && result.Count > max)
            {
                result.RemoveRange(max, result.Count - max);
            }

            return result;
        }

        /// <summary>
        /// 导演/编剧 prompt 里的"精选"：只保留预算允许的，按类别轮换，类内按最低点数降序、
        /// 再按 defName 升序。确定性——同一局势、同一预算得到同一份 16 个。
        /// </summary>
        public static List<IncidentCandidate> SelectForPrompt(Map map, float points,
            NarratorState state, AINarratorSettings settings, int now, int max)
        {
            var result = new List<IncidentCandidate>();
            List<IncidentCandidate> firable = Firable(map, points, 0);
            if (max <= 0) max = firable.Count;

            var groups = new Dictionary<string, List<IncidentCandidate>>(StringComparer.Ordinal);
            for (int i = 0; i < firable.Count; i++)
            {
                IncidentCandidate candidate = firable[i];
                if (!BudgetPolicy.CanUse(candidate, state, now, settings, out _)) continue;

                string category = string.IsNullOrEmpty(candidate.Category) ? "Misc" : candidate.Category;
                if (!groups.TryGetValue(category, out List<IncidentCandidate> list))
                {
                    list = new List<IncidentCandidate>();
                    groups[category] = list;
                }

                list.Add(candidate);
            }

            foreach (List<IncidentCandidate> list in groups.Values)
            {
                list.Sort((a, b) =>
                {
                    int byPoints = b.MinPoints.CompareTo(a.MinPoints);
                    if (byPoints != 0) return byPoints;
                    return string.CompareOrdinal(a.DefName, b.DefName);
                });
            }

            string[] order = { "ThreatBig", "ThreatSmall", "Misc" };
            int perCategory = Math.Max(1, max / 3);

            for (int o = 0; o < order.Length && result.Count < max; o++)
            {
                if (!groups.TryGetValue(order[o], out List<IncidentCandidate> list)) continue;
                int take = Math.Min(perCategory, Math.Min(list.Count, max - result.Count));
                for (int i = 0; i < take; i++) result.Add(list[i]);
            }

            // 三类不足时，用剩下的类别/候选补齐；类别顺序固定，保证确定性。
            var remaining = new List<IncidentCandidate>();
            foreach (var pair in groups)
            {
                int skip = 0;
                if (Array.IndexOf(order, pair.Key) >= 0)
                {
                    skip = Math.Min(perCategory, pair.Value.Count);
                }

                for (int i = skip; i < pair.Value.Count; i++) remaining.Add(pair.Value[i]);
            }

            remaining.Sort((a, b) =>
            {
                int byCategory = string.CompareOrdinal(a.Category, b.Category);
                if (byCategory != 0) return byCategory;
                int byPoints = b.MinPoints.CompareTo(a.MinPoints);
                if (byPoints != 0) return byPoints;
                return string.CompareOrdinal(a.DefName, b.DefName);
            });

            for (int i = 0; i < remaining.Count && result.Count < max; i++) result.Add(remaining[i]);

            return result;
        }

        public static IncidentCandidate Find(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return null;
            IReadOnlyList<IncidentCandidate> candidates = All;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (string.Equals(candidates[i].DefName, defName, StringComparison.Ordinal))
                {
                    return candidates[i];
                }
            }

            return null;
        }

        /// <summary>渲染成 prompt 里的候选段。MockGateway 依赖这个格式（"- Def | Category"）。</summary>
        public static string ToPromptText(List<IncidentCandidate> list, int max)
        {
            var sb = new StringBuilder(512);
            int count = list.Count < max ? list.Count : max;
            for (int i = 0; i < count; i++)
            {
                sb.Append("- ").Append(list[i].DefName).Append(" | ").Append(list[i].Category).Append('\n');
            }

            return sb.ToString();
        }
    }
}
