using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>
    /// 位置锚点。
    ///
    /// 为什么需要：以前所有位置都是绝对坐标 {x,y,z}，而模型根本不知道地图多大、
    /// 基地在哪、仓库在哪 —— 猜错一次，整段脚本作废。现在模型写语义锚点
    /// （"storage" / "edge_north" / "handle:p1"），由 C# 在这里翻译成真实格。
    ///
    /// 兼容：仍然接受 cell={x,y,z}（老脚本不受影响），spot 与 cell 二选一，cell 优先。
    /// 解析失败一律**报错**，不静默落到"随便一个格子"——静默兜底会让 AI 学到错的因果。
    /// </summary>
    public static class ActionSpot
    {
        public const string AnchorsHelp =
            "center / storage / water / ore / edge_north / edge_south / edge_east / edge_west / random / handle:<名字>";

        private const string AnchorsHelpEn =
            "center / storage / water / ore / edge_north / edge_south / edge_east / edge_west / random / handle:<name>";

        public static bool TryResolve(PlannedAction action, ActionContext context, out IntVec3 cell, out string error)
        {
            cell = IntVec3.Invalid;
            error = null;

            Map map = context != null ? context.Map : null;
            if (map == null)
            {
                error = Loc.Pick("没有可用地图", "No map is available");
                return false;
            }

            // 1) 老写法：绝对坐标优先（保持 26 个老动作的语义完全不变）。
            if (ActionArgs.TryGetCell(action, out IntVec3 raw))
            {
                cell = raw;
            }
            else
            {
                // 句柄名保持原样（模型可能用 P1），其余锚点按小写匹配。
                string rawSpot = action.ArgString("spot").Trim();
                string spot = rawSpot.StartsWith("handle:", System.StringComparison.OrdinalIgnoreCase)
                    ? "handle:" + rawSpot.Substring("handle:".Length)
                    : rawSpot.ToLowerInvariant();

                if (spot.Length == 0)
                {
                    error = Loc.Pick("缺少位置：写 spot=\"" + AnchorsHelp.Split(' ')[0] + "\" 之类，或 cell={x,y,z}",
                        "Missing position: write something like spot=\"" + AnchorsHelpEn.Split(' ')[0]
                        + "\", or use cell={x,y,z}");
                    return false;
                }

                if (!TryAnchor(map, context, spot, out cell, out error)) return false;
            }

            // 2) 偏移：offset={x,y} 或 jitter=半径（随机散开，用于"一片"而不是"一点"）。
            cell = ApplyOffset(action, cell, map);

            if (!cell.InBounds(map))
            {
                error = Loc.Pick("位置在地图之外（地图 " + map.Size.x + "×" + map.Size.z + "，你给的是 "
                                 + cell.x + "," + cell.z + "）",
                    "Position is outside the map (the map is " + map.Size.x + "×" + map.Size.z
                    + ", but you gave " + cell.x + "," + cell.z + ")");
                return false;
            }

            return true;
        }

        private static IntVec3 ApplyOffset(PlannedAction action, IntVec3 cell, Map map)
        {
            if (action.args != null && action.args.TryGetValue("offset", out object raw))
            {
                Dictionary<string, object> dict = PlannedAction.AsDict(raw);
                if (dict != null)
                {
                    cell += new IntVec3(AsInt(dict, "x"), 0, AsInt(dict, "y"));
                }
            }

            int jitter = action.ArgInt("jitter", 0);
            if (jitter > 0)
            {
                jitter = Math.Min(jitter, 12);
                cell += new IntVec3(Rand.Range(-jitter, jitter + 1), 0, Rand.Range(-jitter, jitter + 1));
            }

            return cell;
        }

        private static int AsInt(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.TryGetValue(key, out object value) || value == null) return 0;
            if (value is long l) return (int)l;
            if (value is int i) return i;
            if (value is double d) return (int)Math.Round(d);
            return int.TryParse(value.ToString(), out int parsed) ? parsed : 0;
        }

        private static bool TryAnchor(Map map, ActionContext context, string spot, out IntVec3 cell, out string error)
        {
            cell = IntVec3.Invalid;
            error = null;

            if (spot.StartsWith("handle:"))
            {
                string name = spot.Substring("handle:".Length).Trim();
                if (!HandleRegistry.TryResolveReference(context, name, false, out Thing thing, out error))
                {
                    error = Loc.Pick("锚点 " + spot + "：" + error, "anchor " + spot + ": " + error);
                    return false;
                }

                if (thing == null)
                {
                    cell = map.Center;
                    return true;
                }

                if (!thing.Spawned)
                {
                    error = Loc.Pick("锚点 " + spot + "：该对象不在任何地图上",
                        "anchor " + spot + ": that object is not on any map");
                    return false;
                }

                cell = thing.Position;
                return true;
            }

            switch (spot)
            {
                case "center":
                case "colony":
                case "base":
                case "colony_center":
                    cell = map.Center;
                    return true;

                case "random":
                case "near":
                case "near_center":
                    cell = CellFinder.RandomClosewalkCellNear(map.Center, map, 12);
                    return true;

                case "storage":
                case "stockpile":
                    return TryStockpile(map, out cell, out error);

                case "water":
                    return TryTerrainFlag(map, t => t.IsWater, "水", "water", out cell, out error);

                case "ore":
                    return TryMineable(map, out cell, out error);

                case "edge_north":
                    return TryEdge(map, new IntVec3(map.Size.x / 2, 0, map.Size.z - 2), out cell);
                case "edge_south":
                    return TryEdge(map, new IntVec3(map.Size.x / 2, 0, 1), out cell);
                case "edge_east":
                    return TryEdge(map, new IntVec3(map.Size.x - 2, 0, map.Size.z / 2), out cell);
                case "edge_west":
                    return TryEdge(map, new IntVec3(1, 0, map.Size.z / 2), out cell);
            }

            error = Loc.Pick("未知的位置锚点 \"" + spot + "\"；可用：" + AnchorsHelp,
                "Unknown position anchor \"" + spot + "\"; available: " + AnchorsHelpEn);
            return false;
        }

        private static bool TryEdge(Map map, IntVec3 approx, out IntVec3 cell)
        {
            if (CellFinder.TryFindRandomCellNear(approx, map, 16, c => c.Standable(map), out cell))
            {
                return true;
            }

            cell = CellFinder.RandomEdgeCell(map);
            return true;
        }

        private static bool TryStockpile(Map map, out IntVec3 cell, out string error)
        {
            cell = IntVec3.Invalid;
            error = null;

            var cells = new List<IntVec3>();
            foreach (Zone zone in map.zoneManager.AllZones)
            {
                if (!(zone is Zone_Stockpile) || zone.cells == null) continue;
                for (int i = 0; i < zone.cells.Count && cells.Count < 96; i++)
                {
                    cells.Add(zone.cells[i]);
                }

                if (cells.Count >= 96) break;
            }

            if (cells.Count == 0)
            {
                error = Loc.Pick("地图上还没有仓储区，锚点 storage 不可用；改用 center 或 cell",
                    "There is no stockpile on this map, so the storage anchor is unavailable; "
                    + "use center or cell instead");
                return false;
            }

            cell = cells[Rand.Range(0, cells.Count)];
            return true;
        }

        private static bool TryTerrainFlag(Map map, Func<TerrainDef, bool> predicate,
            string labelZh, string labelEn, out IntVec3 cell, out string error)
        {
            cell = IntVec3.Invalid;
            error = null;

            int radius = 40;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(map.Center, radius, true))
            {
                if (!c.InBounds(map)) continue;
                TerrainDef terrain = c.GetTerrain(map);
                if (terrain != null && predicate(terrain))
                {
                    cell = c;
                    return true;
                }
            }

            error = Loc.Pick("地图中心 " + radius + " 格内找不到" + labelZh + "；改用 center 或 cell",
                "No " + labelEn + " found within " + radius + " cells of the map center; "
                + "use center or cell instead");
            return false;
        }

        private static bool TryMineable(Map map, out IntVec3 cell, out string error)
        {
            cell = IntVec3.Invalid;
            error = null;

            var found = new List<IntVec3>();
            IReadOnlyList<Thing> things = map.listerThings.AllThings;
            for (int i = 0; i < things.Count && found.Count < 32; i++)
            {
                Thing thing = things[i];
                if (thing == null || thing.def == null || !thing.def.mineable || !thing.Spawned) continue;
                found.Add(thing.Position);
            }

            if (found.Count == 0)
            {
                error = Loc.Pick("地图上没有可开采的矿，锚点 ore 不可用；改用 center 或 cell",
                    "There is no mineable ore on this map, so the ore anchor is unavailable; "
                    + "use center or cell instead");
                return false;
            }

            cell = found[Rand.Range(0, found.Count)];
            return true;
        }
    }
}
