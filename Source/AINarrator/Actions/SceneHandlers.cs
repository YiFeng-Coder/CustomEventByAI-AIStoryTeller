using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Stellaris.AI
{
    // ─────────────────────────────────────────────────────────────────────────
    // map.building —— 废墟、墙体、路障
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class MapBuildingHandler : IGameActionHandler
    {
        public string OpType => "map.building";
        public ActionPermission Permission => ActionPermission.BoundedSpawn;
        public ActionCategory Category => ActionCategory.Scene;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick("map.building{thingDef=建筑 DefName, spot=锚点, stuff=可选材质, count=1~4} —— 在锚点生成建筑（废墟/墙/路障）",
                     "map.building{thingDef=building DefName, spot=anchor, stuff=optional stuff, count=1~4} — spawns buildings at the anchor (ruins/walls/barricades)");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.Map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }

            string name = a.ArgString("thingDef");
            if (name.Length == 0) { error = Loc.Pick("缺少 thingDef", "missing thingDef"); return false; }

            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
            if (def == null) { error = Loc.Pick("不存在的 ThingDef：" + name, "unknown ThingDef: " + name); return false; }
            if (def.category != ThingCategory.Building)
            {
                error = Loc.Pick(name + " 不是建筑（category=Building）", name + " is not a building (category=Building)");
                return false;
            }

            int count = a.ArgInt("count", 1);
            if (count < 1 || count > 4) { error = Loc.Pick("count 必须在 1..4", "count must be within 1..4"); return false; }

            string stuff = a.ArgString("stuff");
            if (def.MadeFromStuff && stuff.Length == 0)
            {
                error = Loc.Pick(name + " 需要材质，请补 stuff（例如 WoodLog / Steel）",
                                 name + " requires stuff; please set stuff (e.g. WoodLog / Steel)");
                return false;
            }

            if (stuff.Length > 0)
            {
                ThingDef stuffDef = DefDatabase<ThingDef>.GetNamedSilentFail(stuff);
                if (stuffDef == null) { error = Loc.Pick("不存在的材质 ThingDef：" + stuff, "unknown stuff ThingDef: " + stuff); return false; }
                if (def.MadeFromStuff && stuffDef.stuffProps == null)
                {
                    error = Loc.Pick(stuff + " 不是可用的材质（没有 stuffProps）", stuff + " is not a usable stuff (no stuffProps)");
                    return false;
                }
            }

            if (!ActionSpot.TryResolve(a, ctx, out _, out error)) return false;
            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Spawn, count, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            Map map = ctx.Map;
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(a.ArgString("thingDef"));
            if (map == null || def == null) { error = Loc.Pick("没有可用地图或定义", "no map or def available"); return false; }
            if (!ActionSpot.TryResolve(a, ctx, out IntVec3 cell, out error)) return false;

            ThingDef stuff = def.MadeFromStuff
                ? DefDatabase<ThingDef>.GetNamedSilentFail(a.ArgString("stuff", "WoodLog"))
                : null;
            if (def.MadeFromStuff && stuff == null) { error = Loc.Pick("建筑材质不可用", "building stuff is unavailable"); return false; }

            int count = Math.Max(1, Math.Min(4, a.ArgInt("count", 1)));
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Spawn, count, out error)) return false;

            for (int i = 0; i < count; i++)
            {
                if (!TryFindSpot(def, map, cell, out IntVec3 at, out error)) return false;

                try
                {
                    Thing thing = ThingMaker.MakeThing(def, stuff);
                    if (GenSpawn.Spawn(thing, at, map) == null || !thing.Spawned)
                    {
                        error = Loc.Pick("未能生成指定建筑", "could not spawn the requested building");
                        return false;
                    }
                }
                catch (Exception e)
                {
                    error = Loc.Pick("生成建筑失败：" + e.Message, "failed to spawn the building: " + e.Message);
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 找一个"整块占地都放得下"的格子。
        /// 以前直接在锚点 GenSpawn.Spawn：多格建筑（2x2 以上）压到墙/水/别人身上会抛异常，
        /// 而那时预算已扣、前面的建筑已经落地，属于"执行到一半崩掉"。
        /// </summary>
        private static bool TryFindSpot(ThingDef def, Map map, IntVec3 near, out IntVec3 cell, out string error)
        {
            error = null;
            cell = IntVec3.Invalid;

            foreach (IntVec3 candidate in GenRadial.RadialCellsAround(near, 6f, true))
            {
                if (!CanPlace(def, map, candidate)) continue;
                cell = candidate;
                return true;
            }

            error = Loc.Pick("找不到能放下 " + def.defName + " 的空地（可能被建筑/地形挡住）",
                             "no open space for " + def.defName + " (possibly blocked by buildings or terrain)");
            return false;
        }

        private static bool CanPlace(ThingDef def, Map map, IntVec3 cell)
        {
            CellRect rect = GenAdj.OccupiedRect(cell, Rot4.North, def.Size);
            foreach (IntVec3 c in rect)
            {
                if (!c.InBounds(map)) return false;
                if (c.GetEdifice(map) != null) return false;

                TerrainDef terrain = c.GetTerrain(map);
                if (def.terrainAffordanceNeeded != null &&
                    (terrain == null || !terrain.affordances.Contains(def.terrainAffordanceNeeded)))
                {
                    return false;
                }
            }

            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // map.terrain —— 焦土、泥泞、踩出来的路
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class MapTerrainHandler : IGameActionHandler
    {
        public string OpType => "map.terrain";
        public ActionPermission Permission => ActionPermission.BoundedSpawn;
        public ActionCategory Category => ActionCategory.Scene;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick("map.terrain{terrainDef=地形 DefName, spot=锚点, radius=0~4} —— 把一片地换成另一种地形",
                     "map.terrain{terrainDef=terrain DefName, spot=anchor, radius=0~4} — converts an area of ground to another terrain");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.Map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }

            string name = a.ArgString("terrainDef");
            if (name.Length == 0) { error = Loc.Pick("缺少 terrainDef", "missing terrainDef"); return false; }
            if (DefDatabase<TerrainDef>.GetNamedSilentFail(name) == null)
            {
                error = Loc.Pick("不存在的 TerrainDef：" + name, "unknown TerrainDef: " + name);
                return false;
            }

            int radius = a.ArgInt("radius", 1);
            if (radius < 0 || radius > 4) { error = Loc.Pick("radius 必须在 0..4", "radius must be within 0..4"); return false; }

            if (!ActionSpot.TryResolve(a, ctx, out _, out error)) return false;
            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Spawn, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            Map map = ctx.Map;
            TerrainDef def = DefDatabase<TerrainDef>.GetNamedSilentFail(a.ArgString("terrainDef"));
            if (map == null || def == null) { error = Loc.Pick("没有可用地图或定义", "no map or def available"); return false; }
            if (!ActionSpot.TryResolve(a, ctx, out IntVec3 cell, out error)) return false;

            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Spawn, 1, out error)) return false;

            int radius = Math.Max(0, Math.Min(4, a.ArgInt("radius", 1)));
            foreach (IntVec3 c in GenRadial.RadialCellsAround(cell, radius, true))
            {
                if (!c.InBounds(map)) continue;
                map.terrainGrid.SetTerrain(c, def);
            }
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // map.roof —— 塌陷的坑道、洞穴入口
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class MapRoofHandler : IGameActionHandler
    {
        public string OpType => "map.roof";
        public ActionPermission Permission => ActionPermission.BoundedSpawn;
        public ActionCategory Category => ActionCategory.Scene;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick("map.roof{roofDef=屋顶 DefName 或 none, spot=锚点, radius=0~4} —— 加/拆屋顶（none = 拆掉）",
                     "map.roof{roofDef=roof DefName or none, spot=anchor, radius=0~4} — adds/removes roofs (none = remove)");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.Map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }

            string name = a.ArgString("roofDef");
            if (name.Length == 0) { error = Loc.Pick("缺少 roofDef（拆屋顶写 none）", "missing roofDef (write none to remove roofs)"); return false; }
            if (!name.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                DefDatabase<RoofDef>.GetNamedSilentFail(name) == null)
            {
                error = Loc.Pick("不存在的 RoofDef：" + name, "unknown RoofDef: " + name);
                return false;
            }

            int radius = a.ArgInt("radius", 1);
            if (radius < 0 || radius > 4) { error = Loc.Pick("radius 必须在 0..4", "radius must be within 0..4"); return false; }

            if (!ActionSpot.TryResolve(a, ctx, out _, out error)) return false;
            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Spawn, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            Map map = ctx.Map;
            if (map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }
            if (!ActionSpot.TryResolve(a, ctx, out IntVec3 cell, out error)) return false;

            string name = a.ArgString("roofDef");
            RoofDef def = name.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? null
                : DefDatabase<RoofDef>.GetNamedSilentFail(name);
            if (def == null && !name.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                error = Loc.Pick("屋顶定义不可用", "roof def is unavailable");
                return false;
            }

            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Spawn, 1, out error)) return false;

            int radius = Math.Max(0, Math.Min(4, a.ArgInt("radius", 1)));
            foreach (IntVec3 c in GenRadial.RadialCellsAround(cell, radius, true))
            {
                if (!c.InBounds(map)) continue;
                map.roofGrid.SetRoof(c, def);
            }
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // map.filth —— 血迹、灰烬（最便宜的氛围）
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class MapFilthHandler : IGameActionHandler
    {
        public string OpType => "map.filth";
        public ActionPermission Permission => ActionPermission.BoundedSpawn;
        public ActionCategory Category => ActionCategory.Scene;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick("map.filth{filthDef=污渍 DefName, spot=锚点, count=1~8, jitter=0~8} —— 血迹 / 灰烬 / 碎屑",
                     "map.filth{filthDef=filth DefName, spot=anchor, count=1~8, jitter=0~8} — blood / ash / debris");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.Map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }

            string name = a.ArgString("filthDef");
            if (name.Length == 0) { error = Loc.Pick("缺少 filthDef", "missing filthDef"); return false; }

            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
            if (def == null) { error = Loc.Pick("不存在的 ThingDef：" + name, "unknown ThingDef: " + name); return false; }
            if (!typeof(Filth).IsAssignableFrom(def.thingClass))
            {
                error = Loc.Pick(name + " 不是污渍（Filth）", name + " is not filth (Filth)");
                return false;
            }

            int count = a.ArgInt("count", 1);
            if (count < 1 || count > 8) { error = Loc.Pick("count 必须在 1..8", "count must be within 1..8"); return false; }

            if (!ActionSpot.TryResolve(a, ctx, out _, out error)) return false;
            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Spawn, count, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            Map map = ctx.Map;
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(a.ArgString("filthDef"));
            if (map == null || def == null) { error = Loc.Pick("没有可用地图或定义", "no map or def available"); return false; }
            if (!ActionSpot.TryResolve(a, ctx, out IntVec3 cell, out error)) return false;

            int count = Math.Max(1, Math.Min(8, a.ArgInt("count", 1)));
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Spawn, count, out error)) return false;

            for (int i = 0; i < count; i++)
            {
                IntVec3 at = CellFinder.RandomClosewalkCellNear(cell, map, 4);
                if (!FilthMaker.TryMakeFilth(at, map, def, 1, FilthSourceFlags.None))
                {
                    error = Loc.Pick("目标位置无法生成污渍", "could not place filth at the target");
                    return false;
                }
            }
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // map.animal —— 兽群、虫害
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class MapAnimalHandler : IGameActionHandler
    {
        public string OpType => "map.animal";
        public ActionPermission Permission => ActionPermission.BoundedSpawn;
        public ActionCategory Category => ActionCategory.Scene;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick("map.animal{pawnKind=动物 DefName（普通 PawnKindDef）, count=1~6, spot=锚点, manhunter=可选} —— 生成兽群（可设攻击状态）",
                     "map.animal{pawnKind=animal DefName (plain PawnKindDef), count=1~6, spot=anchor, manhunter=optional} — spawns a herd (can be set to attack)");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.Map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }

            string name = a.ArgString("pawnKind");
            if (name.Length == 0) { error = Loc.Pick("缺少 pawnKind", "missing pawnKind"); return false; }

            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(name);
            if (kind == null) { error = Loc.Pick("不存在的 PawnKindDef：" + name, "unknown PawnKindDef: " + name); return false; }
            if (!PawnKindSafety.IsPlainPawnKind(kind))
            {
                error = PawnKindSafety.RejectSpecialPawnKind(name, kind);
                return false;
            }
            if (kind.race == null || kind.race.race == null || !kind.race.race.Animal)
            {
                error = Loc.Pick(name + " 不是动物；生成人请用 pawn.spawn", name + " is not an animal; use pawn.spawn to spawn people");
                return false;
            }

            int count = a.ArgInt("count", 2);
            if (count < 1 || count > 6) { error = Loc.Pick("count 必须在 1..6", "count must be within 1..6"); return false; }

            if (!ActionSpot.TryResolve(a, ctx, out _, out error)) return false;
            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Spawn, count, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            Map map = ctx.Map;
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(a.ArgString("pawnKind"));
            if (map == null || kind == null) { error = Loc.Pick("没有可用地图或定义", "no map or def available"); return false; }
            if (!PawnKindSafety.IsPlainPawnKind(kind))
            {
                error = PawnKindSafety.RejectSpecialPawnKind(a.ArgString("pawnKind"), kind);
                return false;
            }
            if (!ActionSpot.TryResolve(a, ctx, out IntVec3 cell, out error)) return false;

            int count = Math.Max(1, Math.Min(6, a.ArgInt("count", 2)));
            bool manhunter = a.ArgBool("manhunter", false);
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Spawn, count, out error)) return false;

            for (int i = 0; i < count; i++)
            {
                IntVec3 at = CellFinder.RandomClosewalkCellNear(cell, map, 8);

                try
                {
                    Pawn animal = PawnGenerator.GeneratePawn(kind, null);
                    PawnKindSafety.RepairGeneratedPawn(animal);
                    if (animal == null || GenSpawn.Spawn(animal, at, map) == null || !animal.Spawned)
                    {
                        error = Loc.Pick("未能生成指定动物", "could not spawn the requested animal");
                        return false;
                    }

                    // 猎杀状态失败不该让整段脚本作废：动物已经生成，记为警告即可。
                    if (manhunter && animal.mindState?.mentalStateHandler?.TryStartMentalState(
                            MentalStateDefOf.Manhunter, Loc.Pick("被剧情激怒", "provoked by the story"), true) != true)
                    {
                        Verse.Log.Warning(AINarratorMod.LogPrefix + "manhunter state failed for " + kind.defName);
                    }
                }
                catch (Exception e)
                {
                    error = Loc.Pick("生成动物失败：" + e.Message, "failed to spawn the animal: " + e.Message);
                    return false;
                }
            }
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // map.remove —— 清出空地（只删植物 / 污渍 / 杂物）
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class MapRemoveHandler : IGameActionHandler
    {
        public string OpType => "map.remove";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Scene;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick("map.remove{spot=锚点, radius=1~3, only=plants,filt,items} —— 清除一片区域里的植物/污渍/杂物（不动建筑与人）",
                     "map.remove{spot=anchor, radius=1~3, only=plants,filt,items} — clears plants/filth/items in an area (leaves buildings and people alone)");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.Map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }

            int radius = a.ArgInt("radius", 1);
            if (radius < 1 || radius > 3) { error = Loc.Pick("radius 必须在 1..3", "radius must be within 1..3"); return false; }

            if (!ActionSpot.TryResolve(a, ctx, out _, out error)) return false;
            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Danger, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            Map map = ctx.Map;
            if (map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }
            if (!ActionSpot.TryResolve(a, ctx, out IntVec3 cell, out error)) return false;

            int radius = Math.Max(1, Math.Min(3, a.ArgInt("radius", 1)));
            string only = a.ArgString("only", "plants,filth").ToLowerInvariant();
            bool plants = only.Contains("plant");
            bool filth = only.Contains("filth") || only.Contains("filt");
            bool items = only.Contains("item");

            var doomed = new List<Thing>();
            foreach (IntVec3 c in GenRadial.RadialCellsAround(cell, radius, true))
            {
                if (!c.InBounds(map)) continue;

                List<Thing> things = c.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing thing = things[i];
                    if (thing == null || thing is Pawn || thing.def == null) continue;

                    bool isPlant = thing is Plant;
                    bool isFilth = thing is Filth;
                    bool isItem = thing.def.category == ThingCategory.Item;

                    if ((plants && isPlant) || (filth && isFilth) || (items && isItem))
                    {
                        doomed.Add(thing);
                    }
                }
            }

            if (doomed.Count == 0) { error = Loc.Pick("目标范围内没有对应的清除对象", "nothing to remove within the target area"); return false; }
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Danger, 1, out error)) return false;
            foreach (Thing thing in doomed)
            {
                if (!thing.Destroyed) thing.Destroy(DestroyMode.Vanish);
            }
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // map.collapse —— 矿难、地道塌方
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class MapCollapseHandler : IGameActionHandler
    {
        public string OpType => "map.collapse";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Scene;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick("map.collapse{spot=锚点, radius=1~2} —— 拆掉一片屋顶并造成塌方伤害（危险预算，会伤人）",
                     "map.collapse{spot=anchor, radius=1~2} — removes roofs in an area and deals collapse damage (danger budget; can hurt people)");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.Map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }

            int radius = a.ArgInt("radius", 1);
            if (radius < 1 || radius > 2) { error = Loc.Pick("radius 必须在 1..2", "radius must be within 1..2"); return false; }

            if (!ActionSpot.TryResolve(a, ctx, out _, out error)) return false;
            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Danger, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            Map map = ctx.Map;
            if (map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }
            if (!ActionSpot.TryResolve(a, ctx, out IntVec3 cell, out error)) return false;

            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Danger, 1, out error)) return false;

            int radius = Math.Max(1, Math.Min(2, a.ArgInt("radius", 1)));
            foreach (IntVec3 c in GenRadial.RadialCellsAround(cell, radius, true))
            {
                if (!c.InBounds(map)) continue;
                map.roofGrid.SetRoof(c, null);
            }

            GenExplosion.DoExplosion(cell, map, radius, DamageDefOf.Crush, null);
            return true;
        }
    }
}