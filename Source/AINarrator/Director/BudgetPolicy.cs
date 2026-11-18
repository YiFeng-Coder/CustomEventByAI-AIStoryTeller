using System.Collections.Generic;

namespace Stellaris.AI
{
    /// <summary>
    /// 配额与去重。数值全部来自玩家设置，**代码里不写死**；0 一律表示不限制。
    ///
    /// 规则：
    ///  1. 同一个 defName 在 repeatWindowDays 游戏日内不重复（0 = 不做冷却）；
    ///  2. 最近 quotaWindow 个事件里，各类别数量不超过对应上限（0 = 不限制该类）；
    ///  3. 最近连续 consecutiveThreatLimit 次都是威胁类时，下一次禁止再选威胁（0 = 不限制）。
    /// </summary>
    public static class BudgetPolicy
    {
        public static bool CanUse(IncidentCandidate candidate, NarratorState state, int nowTick,
            AINarratorSettings settings, out string reason)
        {
            reason = "";

            if (candidate == null)
            {
                reason = "候选为空";
                return false;
            }

            if (state == null)
            {
                reason = "状态未加载";
                return false;
            }

            if (settings == null)
            {
                return true;
            }

            // 1) 同 defName 冷却
            int repeatTicks = settings.repeatWindowDays * 60000;
            if (repeatTicks > 0)
            {
                for (int i = state.history.Count - 1; i >= 0; i--)
                {
                    HistoryRecord r = state.history[i];
                    if (!r.fired) continue;
                    if (!string.Equals(r.defName, candidate.DefName)) continue;
                    if (nowTick - r.tick < repeatTicks)
                    {
                        reason = candidate.DefName + " 在 " + settings.repeatWindowDays + " 天内刚发生过（第 "
                                 + (r.tick / 60000) + " 天）";
                        return false;
                    }

                    break;
                }
            }

            // 2) 类别配额
            int cap = QuotaFor(candidate.Category, settings);
            if (cap > 0 && settings.quotaWindow > 0)
            {
                int used = CountRecent(state, candidate.Category, settings.quotaWindow);
                if (used >= cap)
                {
                    reason = candidate.Category + " 最近 " + settings.quotaWindow + " 个事件里已用 "
                             + used + "/" + cap;
                    return false;
                }
            }

            // 3) 连续威胁
            if (settings.consecutiveThreatLimit > 0 &&
                IsThreat(candidate.Category) &&
                ConsecutiveThreats(state) >= settings.consecutiveThreatLimit)
            {
                reason = "最近连续 " + settings.consecutiveThreatLimit + " 次都是威胁，要求先休整";
                return false;
            }

            return true;
        }

        /// <summary>威胁类判定（ThreatBig / ThreatSmall）。</summary>
        public static bool IsThreat(string category) =>
            !string.IsNullOrEmpty(category) &&
            category.StartsWith("Threat", System.StringComparison.Ordinal);

        public static int QuotaFor(string category, AINarratorSettings settings)
        {
            if (settings == null) return 0;

            switch (category)
            {
                case "ThreatBig": return settings.threatBigQuota;
                case "ThreatSmall": return settings.threatSmallQuota;
                case "Misc": return settings.miscQuota;
                default: return 0;
            }
        }

        public static int CountRecent(NarratorState state, string category, int window)
        {
            if (state == null || window <= 0) return 0;

            int count = 0;
            int start = state.history.Count - window;
            if (start < 0) start = 0;

            for (int i = start; i < state.history.Count; i++)
            {
                HistoryRecord r = state.history[i];
                if (!r.fired) continue;
                if (string.Equals(r.category, category)) count++;
            }

            return count;
        }

        public static int ConsecutiveThreats(NarratorState state)
        {
            if (state == null) return 0;

            int count = 0;
            for (int i = state.history.Count - 1; i >= 0; i--)
            {
                HistoryRecord r = state.history[i];
                if (!r.fired) continue;
                if (IsThreat(r.category)) count++;
                else break;
            }

            return count;
        }

        /// <summary>给 prompt 看的配额说明，让模型知道还剩多少余地。</summary>
        public static string Describe(NarratorState state, AINarratorSettings settings)
        {
            if (state == null || settings == null) return "";

            var parts = new List<string>(4);
            parts.Add(DescribeOne(state, settings, "ThreatBig", settings.threatBigQuota));
            parts.Add(DescribeOne(state, settings, "ThreatSmall", settings.threatSmallQuota));
            parts.Add(DescribeOne(state, settings, "Misc", settings.miscQuota));
            parts.Add(settings.consecutiveThreatLimit > 0
                ? "连续威胁 " + ConsecutiveThreats(state) + "/" + settings.consecutiveThreatLimit
                : "连续威胁 不限制");
            return string.Join(" | ", parts.ToArray());
        }

        private static string DescribeOne(NarratorState state, AINarratorSettings settings,
            string category, int cap)
        {
            if (cap <= 0 || settings.quotaWindow <= 0) return category + " 不限制";
            return category + " " + CountRecent(state, category, settings.quotaWindow) + "/" + cap;
        }
    }
}
