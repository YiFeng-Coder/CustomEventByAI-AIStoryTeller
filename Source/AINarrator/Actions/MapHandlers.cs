using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>动作参数里的小工具（坐标解析等）。</summary>
    public static class ActionArgs
    {
        public static bool TryGetCell(PlannedAction action, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            if (action?.args == null) return false;
            if (!action.args.TryGetValue("cell", out object raw)) return false;

            Dictionary<string, object> dict = PlannedAction.AsDict(raw);
            if (dict == null) return false;

            cell = new IntVec3(Int(dict, "x"), Int(dict, "y"), Int(dict, "z"));
            return true;
        }

        private static int Int(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out object value) || value == null) return 0;
            if (value is long l) return (int)l;
            if (value is int i) return i;
            if (value is double d) return (int)System.Math.Round(d);
            int.TryParse(value.ToString(), out int parsed);
            return parsed;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // map.weather
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class MapWeatherHandler : IGameActionHandler
    {
        public string OpType => "map.weather";
        public ActionPermission Permission => ActionPermission.BoundedSpawn;

        public ActionCategory Category => ActionCategory.Map;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick(
                "map.weather{weatherDef=天气 DefName} —— 把当前地图天气切换成指定天气（会自行恢复）",
                "map.weather{weatherDef=weather DefName} — switch the current map's weather to the given weather (it reverts on its own)");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.Map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }

            string name = a.ArgString("weatherDef");
            if (name.Length == 0) { error = Loc.Pick("缺少 weatherDef", "missing weatherDef"); return false; }
            if (DefDatabase<WeatherDef>.GetNamedSilentFail(name) == null)
            {
                error = Loc.Pick("不存在的 WeatherDef：" + name, "unknown WeatherDef: " + name);
                return false;
            }

            return true;
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            WeatherDef def = DefDatabase<WeatherDef>.GetNamedSilentFail(a.ArgString("weatherDef"));
            if (def == null || ctx.Map == null) { error = Loc.Pick("没有可用地图或定义", "no map or def available"); return false; }
            ctx.Map.weatherManager.TransitionTo(def);
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // map.condition
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class MapConditionHandler : IGameActionHandler
    {
        public string OpType => "map.condition";
        public ActionPermission Permission => ActionPermission.BoundedSpawn;

        public ActionCategory Category => ActionCategory.Map;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick(
                "map.condition{conditionDef=状况 DefName, hours=持续小时(≤48)} —— 添加地图状况（日蚀/毒雾/寒潮等）",
                "map.condition{conditionDef=condition DefName, hours=duration in hours (≤48)} — add a map condition (eclipse / toxic falloff / cold snap, etc.)");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.Map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }

            string name = a.ArgString("conditionDef");
            if (name.Length == 0) { error = Loc.Pick("缺少 conditionDef", "missing conditionDef"); return false; }
            if (DefDatabase<GameConditionDef>.GetNamedSilentFail(name) == null)
            {
                error = Loc.Pick("不存在的 GameConditionDef：" + name, "unknown GameConditionDef: " + name);
                return false;
            }

            int hours = a.ArgInt("hours", 6);
            if (hours < 1 || hours > 48) { error = Loc.Pick("hours 必须在 1..48", "hours must be within 1..48"); return false; }
            return true;
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            GameConditionDef def = DefDatabase<GameConditionDef>.GetNamedSilentFail(a.ArgString("conditionDef"));
            if (def == null || ctx.Map == null) { error = Loc.Pick("没有可用地图或定义", "no map or def available"); return false; }

            int hours = System.Math.Max(1, System.Math.Min(48, a.ArgInt("hours", 6)));
            GameCondition condition = GameConditionMaker.MakeCondition(def, hours * 2500);
            if (condition == null) { error = Loc.Pick("未能创建地图状况", "failed to create the map condition"); return false; }
            ctx.Map.gameConditionManager.RegisterCondition(condition);
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // map.fire
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class MapFireHandler : IGameActionHandler
    {
        public string OpType => "map.fire";
        public ActionPermission Permission => ActionPermission.Major;

        public ActionCategory Category => ActionCategory.Map;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick(
                "map.fire{spot=锚点, size=0.5~3} —— 在指定位置点火（计入危险预算）",
                "map.fire{spot=anchor, size=0.5~3} — start a fire at the given position (counts against the danger budget)");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.Map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }
            if (!ActionSpot.TryResolve(a, ctx, out _, out error)) return false;

            float size = a.ArgFloat("size", 1f);
            if (size < 0.5f || size > 3f) { error = Loc.Pick("size 必须在 0.5..3", "size must be within 0.5..3"); return false; }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Danger, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.Map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }
            if (!ActionSpot.TryResolve(a, ctx, out IntVec3 cell, out error)) return false;
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Danger, 1, out error)) return false;

            float size = System.Math.Max(0.5f, System.Math.Min(3f, a.ArgFloat("size", 1f)));
            if (!FireUtility.TryStartFireIn(cell, ctx.Map, size, null))
            {
                error = Loc.Pick("目标位置无法点火", "a fire cannot be started at that position");
                return false;
            }
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // map.damage —— 爆炸
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class MapDamageHandler : IGameActionHandler
    {
        public string OpType => "map.damage";
        public ActionPermission Permission => ActionPermission.Major;

        public ActionCategory Category => ActionCategory.Map;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick(
                "map.damage{spot=锚点, radius=1~3, damageDef=伤害 DefName} —— 在指定位置爆炸（会伤人，计入危险预算）",
                "map.damage{spot=anchor, radius=1~3, damageDef=damage DefName} — explode at the given position (it hurts people; counts against the danger budget)");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.Map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }
            if (!ActionSpot.TryResolve(a, ctx, out _, out error)) return false;

            float radius = a.ArgFloat("radius", 1.5f);
            if (radius < 1f || radius > 3f) { error = Loc.Pick("radius 必须在 1..3", "radius must be within 1..3"); return false; }

            string damage = a.ArgString("damageDef", "Bomb");
            if (DefDatabase<DamageDef>.GetNamedSilentFail(damage) == null)
            {
                error = Loc.Pick("不存在的 DamageDef：" + damage, "unknown DamageDef: " + damage);
                return false;
            }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Danger, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.Map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }
            if (!ActionSpot.TryResolve(a, ctx, out IntVec3 cell, out error)) return false;

            float radius = System.Math.Max(1f, System.Math.Min(3f, a.ArgFloat("radius", 1.5f)));
            DamageDef damage = DefDatabase<DamageDef>.GetNamedSilentFail(a.ArgString("damageDef", "Bomb"));
            if (damage == null) { error = Loc.Pick("伤害定义不可用", "the damage def is unavailable"); return false; }

            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Danger, 1, out error)) return false;
            GenExplosion.DoExplosion(cell, ctx.Map, radius, damage, null);
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // map.drop_pod
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class MapDropPodHandler : IGameActionHandler
    {
        public string OpType => "map.drop_pod";
        public ActionPermission Permission => ActionPermission.BoundedSpawn;

        public ActionCategory Category => ActionCategory.Map;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick(
                "map.drop_pod{thingDef=物品 DefName, count=1~20, spot=可选} —— 用运输舱把物资空投到地图上",
                "map.drop_pod{thingDef=item DefName, count=1~20, spot=optional} — drop supplies onto the map with a transport pod");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.Map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }

            string name = a.ArgString("thingDef");
            if (name.Length == 0) { error = Loc.Pick("缺少 thingDef", "missing thingDef"); return false; }

            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
            if (def == null)
            {
                error = Loc.Pick("不存在的 ThingDef：" + name, "unknown ThingDef: " + name);
                return false;
            }

            // 需要材质的定义不能直接 MakeThing（与 thing.spawn 同一类问题）。
            if (!SpawnSafety.CanMakeThing(def, out error)) return false;

            int count = a.ArgInt("count", 10);
            if (count < 1 || count > 20) { error = Loc.Pick("count 必须在 1..20", "count must be within 1..20"); return false; }

            if (HasLocation(a) && !ActionSpot.TryResolve(a, ctx, out _, out error)) return false;

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Spawn, count, out error);
        }

        private static bool HasLocation(PlannedAction a) =>
            a.ArgString("spot").Length > 0 || (a.args != null && a.args.ContainsKey("cell"));

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(a.ArgString("thingDef"));
            if (def == null || ctx.Map == null) { error = Loc.Pick("没有可用地图或定义", "no map or def available"); return false; }

            IntVec3 cell;
            if (HasLocation(a))
            {
                if (!ActionSpot.TryResolve(a, ctx, out cell, out error)) return false;
            }
            else
            {
                cell = DropCellFinder.TradeDropSpot(ctx.Map);
            }

            int count = System.Math.Max(1, System.Math.Min(20, a.ArgInt("count", 10)));
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Spawn, count, out error)) return false;

            var things = new List<Thing>(count);
            for (int remaining = count; remaining > 0;)
            {
                Thing thing = ThingMaker.MakeThing(def);
                thing.stackCount = System.Math.Min(System.Math.Max(1, def.stackLimit), remaining);
                things.Add(thing);
                remaining -= thing.stackCount;
            }

            DropPodUtility.DropThingsNear(cell, ctx.Map, things);
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // map.corpse —— 现场尸体（叙事核心）
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class MapCorpseHandler : IGameActionHandler
    {
        public string OpType => "map.corpse";
        public ActionPermission Permission => ActionPermission.Major;

        public ActionCategory Category => ActionCategory.Map;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick(
                "map.corpse{pawnKind=角色 DefName, spot=锚点, handle=可选句柄名} —— " +
                "在指定位置生成一具尸体（走真实死亡流程，会产生 Corpse，计入危险预算）；pawnKind 必须是普通 PawnKindDef（不能是 CreepJoiner 等特殊子类）",
                "map.corpse{pawnKind=character DefName, spot=anchor, handle=optional handle name} — " +
                "spawn one corpse at the given position (it goes through the real death pipeline, produces a Corpse, and counts against the danger budget); pawnKind must be a plain PawnKindDef (not a special subclass such as CreepJoiner)");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.Map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }

            string kindName = a.ArgString("pawnKind");
            if (kindName.Length == 0) { error = Loc.Pick("缺少 pawnKind", "missing pawnKind"); return false; }
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(kindName);
            if (kind == null)
            {
                error = Loc.Pick("不存在的 PawnKindDef：" + kindName, "unknown PawnKindDef: " + kindName);
                return false;
            }

            if (!PawnKindSafety.IsPlainPawnKind(kind))
            {
                error = PawnKindSafety.RejectSpecialPawnKind(kindName, kind);
                return false;
            }

            if (!ActionSpot.TryResolve(a, ctx, out _, out error)) return false;

            return ActionBudget.TryPlanCorpse(ctx, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(a.ArgString("pawnKind"));
            if (kind == null || ctx.Map == null) { error = Loc.Pick("没有可用地图或定义", "no map or def available"); return false; }
            if (!PawnKindSafety.IsPlainPawnKind(kind))
            {
                error = PawnKindSafety.RejectSpecialPawnKind(a.ArgString("pawnKind"), kind);
                return false;
            }
            if (!ActionSpot.TryResolve(a, ctx, out IntVec3 cell, out error)) return false;

            // 水/墙里放不下角色：先挪到最近的可站立格，避免 GenSpawn 抛异常。
            if (!cell.Standable(ctx.Map))
            {
                cell = CellFinder.RandomClosewalkCellNear(cell, ctx.Map, 8);
            }

            if (!ActionBudget.TryConsumeCorpse(ctx, out error)) return false;

            Pawn pawn;
            try
            {
                pawn = PawnGenerator.GeneratePawn(kind, null);
                PawnKindSafety.RepairGeneratedPawn(pawn);
                if (pawn == null || GenSpawn.Spawn(pawn, cell, ctx.Map) == null || !pawn.Spawned)
                {
                    error = Loc.Pick("未能生成用于制作尸体的角色", "failed to generate the pawn that would become the corpse");
                    return false;
                }
            }
            catch (Exception e)
            {
                error = Loc.Pick("生成尸体失败：" + e.Message, "failed to create the corpse: " + e.Message);
                return false;
            }

            pawn.Kill(null, null);
            Corpse corpse = pawn.Corpse;
            if (corpse == null || !corpse.Spawned) { error = Loc.Pick("尸体未生成", "no corpse was created"); return false; }
            ctx.RegisterHandle(a.ArgString("handle"), corpse);
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // map.plant
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class MapPlantHandler : IGameActionHandler
    {
        public string OpType => "map.plant";
        public ActionPermission Permission => ActionPermission.BoundedSpawn;

        public ActionCategory Category => ActionCategory.Map;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            Loc.Pick(
                "map.plant{plantDef=植物 DefName, spot=锚点, growth=0.1~1} —— 在指定位置生成植物（可指定生长度）",
                "map.plant{plantDef=plant DefName, spot=anchor, growth=0.1~1} — spawn a plant at the given position (growth is optional)");

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (ctx.Map == null) { error = Loc.Pick("没有可用地图", "no map available"); return false; }

            string name = a.ArgString("plantDef");
            if (name.Length == 0) { error = Loc.Pick("缺少 plantDef", "missing plantDef"); return false; }

            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
            if (def == null) { error = Loc.Pick("不存在的 ThingDef：" + name, "unknown ThingDef: " + name); return false; }
            if (!typeof(Plant).IsAssignableFrom(def.thingClass))
            {
                error = Loc.Pick(name + " 不是植物", name + " is not a plant");
                return false;
            }

            if (!ActionSpot.TryResolve(a, ctx, out _, out error)) return false;

            float growth = a.ArgFloat("growth", 1f);
            if (growth < 0.1f || growth > 1f) { error = Loc.Pick("growth 必须在 0.1..1", "growth must be within 0.1..1"); return false; }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Spawn, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(a.ArgString("plantDef"));
            if (def == null || ctx.Map == null) { error = Loc.Pick("没有可用地图或定义", "no map or def available"); return false; }
            if (!ActionSpot.TryResolve(a, ctx, out IntVec3 cell, out error)) return false;

            TerrainDef terrain = cell.GetTerrain(ctx.Map);
            if (terrain == null || terrain.fertility <= 0f || cell.GetEdifice(ctx.Map) != null)
            {
                error = Loc.Pick("该位置不能种植物（地形 " + (terrain != null ? terrain.defName : "?")
                        + " 肥力为 0，或被建筑占用）",
                        "cannot plant there (terrain " + (terrain != null ? terrain.defName : "?")
                        + " has 0 fertility, or the cell is occupied by a building)");
                return false;
            }

            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Spawn, 1, out error)) return false;

            try
            {
                Thing thing = ThingMaker.MakeThing(def);
                if (thing is Plant plant)
                {
                    plant.Growth = System.Math.Max(0.1f, System.Math.Min(1f, a.ArgFloat("growth", 1f)));
                }

                if (GenSpawn.Spawn(thing, cell, ctx.Map) == null || !thing.Spawned)
                {
                    error = Loc.Pick("植物生成失败", "failed to spawn the plant");
                    return false;
                }
            }
            catch (Exception e)
            {
                error = Loc.Pick("植物生成失败：" + e.Message, "failed to spawn the plant: " + e.Message);
                return false;
            }
            return true;
        }
    }
}