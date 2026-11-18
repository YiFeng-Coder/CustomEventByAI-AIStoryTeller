using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace Stellaris.AI
{
    /// <summary>
    /// 主线程收集殖民地状态。所有读取都在这里完成，产出纯数据。
    /// 该方法的耗时目标 &lt; 1ms：只做计数、求和与最多 8 个殖民者的摘要。
    /// </summary>
    public static class SnapshotBuilder
    {
        private const int MaxPawns = 8;

        public static Snapshot Build(Map map, NarratorState state)
        {
            if (map == null) return null;

            var snap = new Snapshot
            {
                Tick = Find.TickManager.TicksGame,
                Day = (int)(Find.TickManager.TicksGame / 60000f),
                MapName = map.Parent != null ? map.Parent.Label : map.uniqueID.ToString()
            };

            try
            {
                snap.Biome = map.Biome != null ? map.Biome.label : "";
                snap.Weather = map.weatherManager != null && map.weatherManager.curWeather != null
                    ? map.weatherManager.curWeather.label
                    : "";
                snap.OutdoorTemp = map.mapTemperature != null ? map.mapTemperature.OutdoorTemp : 0f;
            }
            catch (Exception e)
            {
                Verse.Log.Warning(AINarratorMod.LogPrefix + "snapshot env failed: " + e.Message);
            }

            try
            {
                snap.Colonists = map.mapPawns.FreeColonistsSpawnedCount;
                snap.ThreatPoints = StorytellerUtility.DefaultThreatPointsNow(map);
                snap.Wealth = map.wealthWatcher != null ? map.wealthWatcher.WealthTotal : 0f;
                snap.FoodNutrition = map.resourceCounter != null ? map.resourceCounter.TotalHumanEdibleNutrition : 0f;
            }
            catch (Exception e)
            {
                Verse.Log.Warning(AINarratorMod.LogPrefix + "snapshot colony failed: " + e.Message);
            }

            try
            {
                float moodSum = 0f;
                int moodCount = 0;
                int downed = 0;
                int mental = 0;
                int shown = 0;

                foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
                {
                    if (pawn == null || pawn.Dead) continue;

                    if (pawn.Downed) downed++;
                    if (pawn.InMentalState) mental++;

                    float mood = -1f;
                    if (pawn.needs != null && pawn.needs.mood != null)
                    {
                        mood = pawn.needs.mood.CurLevel;
                        moodSum += mood;
                        moodCount++;
                    }

                    if (shown < MaxPawns)
                    {
                        shown++;
                        snap.Pawns.Add(new PawnBrief
                        {
                            Name = Trim(pawn.LabelShort, 24),
                            Kind = pawn.kindDef != null ? pawn.kindDef.defName : "",
                            Age = pawn.ageTracker != null ? (int)pawn.ageTracker.AgeBiologicalYears : 0,
                            Mood = MoodBucket(mood),
                            Downed = pawn.Downed,
                            TopSkill = TopSkill(pawn)
                        });
                    }
                }

                snap.Downed = downed;
                snap.MentalBreak = mental;
                if (moodCount > 0) snap.AvgMood = moodSum / moodCount;
            }
            catch (Exception e)
            {
                Verse.Log.Warning(AINarratorMod.LogPrefix + "snapshot pawns failed: " + e.Message);
            }

            if (state != null)
            {
                snap.LastEvent = state.LastEventLabel;
                snap.RecentSummary = state.RecentSummary(6);
            }

            return snap;
        }

        private static string MoodBucket(float mood)
        {
            if (mood < 0f) return "?";
            if (mood < 0.3f) return "低";
            if (mood < 0.6f) return "中";
            return "高";
        }

        private static string TopSkill(Pawn pawn)
        {
            try
            {
                if (pawn.skills == null) return "";
                SkillRecord best = null;
                foreach (SkillRecord skill in pawn.skills.skills)
                {
                    if (skill == null || skill.TotallyDisabled) continue;
                    if (best == null || skill.Level > best.Level) best = skill;
                }

                return best != null ? best.def.defName : "";
            }
            catch
            {
                return "";
            }
        }

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= max ? text : text.Substring(0, max);
        }

        /// <summary>把快照渲染成 prompt 里那一段紧凑文本。</summary>
        public static string ToPromptText(Snapshot s)
        {
            if (s == null) return "(无快照)";

            var sb = new StringBuilder(1024);
            sb.Append("第 ").Append(s.Day).Append(" 天")
              .Append(" | 生物群系 ").Append(s.Biome)
              .Append(" | 天气 ").Append(s.Weather)
              .Append(" | 室外 ").Append(s.OutdoorTemp.ToString("F0")).Append("°C\n");

            sb.Append("殖民者 ").Append(s.Colonists)
              .Append(" 人（倒地 ").Append(s.Downed)
              .Append("，精神崩溃 ").Append(s.MentalBreak).Append("）")
              .Append(" | 平均心情 ").Append(s.AvgMood < 0 ? "?" : (s.AvgMood * 100f).ToString("F0") + "%")
              .Append(" | 财富 ").Append(s.Wealth.ToString("F0"))
              .Append(" | 威胁点数 ").Append(s.ThreatPoints.ToString("F0"))
              .Append(" | 食物 ").Append(s.FoodNutrition.ToString("F0")).Append(" 营养\n");


            if (s.Pawns.Count > 0)
            {
                sb.Append("主要殖民者：\n");
                foreach (PawnBrief p in s.Pawns)
                {
                    sb.Append("- ").Append(p.Name)
                      .Append(" | ").Append(p.Kind)
                      .Append(" | ").Append(p.Age).Append("岁")
                      .Append(" | 心情").Append(p.Mood)
                      .Append(p.Downed ? " | 倒地" : "")
                      .Append(string.IsNullOrEmpty(p.TopSkill) ? "" : " | 擅长" + p.TopSkill)
                      .Append('\n');
                }
            }

            if (!string.IsNullOrEmpty(s.LastEvent))
            {
                sb.Append("最近一次事件：").Append(s.LastEvent).Append('\n');
            }

            if (!string.IsNullOrEmpty(s.RecentSummary))
            {
                sb.Append("最近事件史：\n").Append(s.RecentSummary);
            }

            return sb.ToString();
        }
    }
}
