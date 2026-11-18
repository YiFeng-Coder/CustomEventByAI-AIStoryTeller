using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>
    /// 给模型看的"合法 Def 名单"。
    ///
    /// 为什么必须给：模型不知道我们的 Def 命名，凭空写就会被整段拒。
    /// 名单全部在运行时从 DefDatabase 现取，不会像硬编码清单那样过期。
    ///
    /// P4 起：名单**按动作分组裁剪**。42 个动作全带名单会把提示词顶到 10k 字，
    /// 而其中大半是"这次根本不会用到"的类别。
    /// </summary>
    public static class ActionCatalog
    {
        private const int MaxItemNames = 14;

        public static string ThingList(int max = MaxItemNames) => Collect(
            DefDatabase<ThingDef>.AllDefs,
            d => d != null && d.category == ThingCategory.Item && d.BaseMarketValue >= 1f && !d.IsCorpse,
            max);

        public static string PawnKindList(int max = 8) => Collect(
            DefDatabase<PawnKindDef>.AllDefs,
            d => d != null && d.race != null && d.race.race != null && d.race.race.Humanlike,
            max);

        public static string WeatherList(int max = 10) => Collect(
            DefDatabase<WeatherDef>.AllDefs,
            d => d != null,
            max);

        public static string ConditionList(int max = 12) => Collect(
            DefDatabase<GameConditionDef>.AllDefs,
            d => d != null,
            max);

        public static string DamageList(int max = 10) => Collect(
            DefDatabase<DamageDef>.AllDefs,
            d => d != null,
            max);

        public static string PlantList(int max = 10) => Collect(
            DefDatabase<ThingDef>.AllDefs,
            d => d != null && typeof(Plant).IsAssignableFrom(d.thingClass) && d.plant != null,
            max);

        public static string HediffList(int max = 12) => Collect(
            DefDatabase<HediffDef>.AllDefs,
            d => d != null && d.isBad && d.hediffClass != null,
            max);

        public static string SkillList(int max = 12) => Collect(
            DefDatabase<SkillDef>.AllDefs,
            d => d != null,
            max);

        public static string ResearchList(int max = 15) => Collect(
            DefDatabase<ResearchProjectDef>.AllDefs,
            d => d != null && !d.IsFinished,
            max);

        public static string QuestList(int max = 10) => Collect(
            DefDatabase<QuestScriptDef>.AllDefs,
            d => d != null && d.root != null,
            max);

        public static string FactionList(int max = 10)
        {
            var sb = new StringBuilder(256);
            int count = 0;

            foreach (Faction faction in Find.FactionManager.AllFactions)
            {
                if (faction == null || faction.IsPlayer || faction.def == null) continue;
                sb.Append(faction.def.defName).Append(' ');
                if (++count >= max) break;
            }

            return sb.ToString().Trim();
        }

        public static string ApparelList(int max = 10) => Collect(
            DefDatabase<ThingDef>.AllDefs,
            d => d != null && typeof(Apparel).IsAssignableFrom(d.thingClass),
            max);

        public static string WeaponList(int max = 10) => Collect(
            DefDatabase<ThingDef>.AllDefs,
            d => d != null && d.IsWeapon && d.equipmentType == EquipmentType.Primary,
            max);

        // ── P4 新增名单 ────────────────────────────────────────────────────

        public static string BuildingList(int max = 12) => Collect(
            DefDatabase<ThingDef>.AllDefs,
            d => d != null && d.category == ThingCategory.Building && d.building != null
                 && !d.mineable && !d.building.isNaturalRock,
            max);

        public static string TerrainList(int max = 12) => Collect(
            DefDatabase<TerrainDef>.AllDefs,
            d => d != null && d.natural,
            max);

        public static string RoofList(int max = 8) => Collect(
            DefDatabase<RoofDef>.AllDefs,
            d => d != null,
            max);

        public static string FilthList(int max = 8) => Collect(
            DefDatabase<ThingDef>.AllDefs,
            d => d != null && typeof(Filth).IsAssignableFrom(d.thingClass),
            max);

        public static string ThoughtList(int max = 12) => Collect(
            DefDatabase<ThoughtDef>.AllDefs,
            d => d != null && d.IsMemory,
            max);

        public static string MentalStateList(int max = 10) => Collect(
            DefDatabase<MentalStateDef>.AllDefs,
            d => d != null,
            max);

        public static string TraitList(int max = 12) => Collect(
            DefDatabase<TraitDef>.AllDefs,
            d => d != null,
            max);

        public static string RelationList(int max = 10) => Collect(
            DefDatabase<PawnRelationDef>.AllDefs,
            d => d != null,
            max);

        public static string InspirationList(int max = 12) => Collect(
            DefDatabase<InspirationDef>.AllDefs,
            d => d != null,
            max);

        private static string Collect<T>(IEnumerable<T> source, Func<T, bool> predicate, int max)
            where T : Def
        {
            var sb = new StringBuilder(512);
            int count = 0;

            foreach (T def in source)
            {
                if (!predicate(def)) continue;

                sb.Append(def.defName).Append(' ');
                if (++count >= max) break;
            }

            return sb.ToString().Trim();
        }

        /// <summary>把当前这次请求用得上的名单拼成提示词里的一段。</summary>
        public static string BuildDefSection(ActionCategoryMask mask = ActionCategoryMask.All)
        {
            var sb = new StringBuilder(1536);

            bool wants(ActionCategory category) => (mask & (ActionCategoryMask)(1 << (int)category)) != 0;

            bool spawn = wants(ActionCategory.Spawn);
            bool map = wants(ActionCategory.Map);
            bool pawn = wants(ActionCategory.Pawn);
            bool social = wants(ActionCategory.Social);
            bool scene = wants(ActionCategory.Scene);
            bool world = wants(ActionCategory.World);

            if (spawn || scene) sb.Append(Loc.Pick("物品(thing.spawn / story.reward / map.drop_pod)：", "Items (thing.spawn / story.reward / map.drop_pod): ")).Append(ThingList()).Append('\n');
            if (scene) sb.Append(Loc.Pick("建筑(map.building)：", "Buildings (map.building): ")).Append(BuildingList()).Append('\n');
            if (spawn || scene) sb.Append(Loc.Pick("角色与动物(pawn.spawn / map.corpse / map.animal)：", "Characters and animals (pawn.spawn / map.corpse / map.animal): ")).Append(PawnKindList()).Append('\n');
            if (map) sb.Append(Loc.Pick("天气(map.weather)：", "Weather (map.weather): ")).Append(WeatherList()).Append('\n');
            if (map) sb.Append(Loc.Pick("状况(map.condition)：", "Conditions (map.condition): ")).Append(ConditionList()).Append('\n');
            if (map) sb.Append(Loc.Pick("伤害类型(map.damage)：", "Damage types (map.damage): ")).Append(DamageList()).Append('\n');
            if (map) sb.Append(Loc.Pick("植物(map.plant)：", "Plants (map.plant): ")).Append(PlantList()).Append('\n');
            if (pawn) sb.Append(Loc.Pick("负面状态(pawn.hurt / pawn.heal)：", "Negative conditions (pawn.hurt / pawn.heal): ")).Append(HediffList()).Append('\n');
            if (pawn) sb.Append(Loc.Pick("技能(pawn.set_skill / best:技能)：", "Skills (pawn.set_skill / best:skill): ")).Append(SkillList()).Append('\n');
            if (pawn) sb.Append(Loc.Pick("衣物(pawn.gear slot=apparel)：", "Apparel (pawn.gear slot=apparel): ")).Append(ApparelList()).Append('\n');
            if (pawn) sb.Append(Loc.Pick("武器(pawn.gear slot=weapon)：", "Weapons (pawn.gear slot=weapon): ")).Append(WeaponList()).Append('\n');
            if (world) sb.Append(Loc.Pick("研究(research.grant)：", "Research (research.grant): ")).Append(ResearchList()).Append('\n');
            if (world) sb.Append(Loc.Pick("任务模板(quest.start)：", "Quest templates (quest.start): ")).Append(QuestList()).Append('\n');
            if (world) sb.Append(Loc.Pick("派系(faction.goodwill)：", "Factions (faction.goodwill): ")).Append(FactionList()).Append('\n');
            if (social) sb.Append(Loc.Pick("想法(pawn.thought)：", "Thoughts (pawn.thought): ")).Append(ThoughtList()).Append('\n');
            if (social) sb.Append(Loc.Pick("精神状态(pawn.mental)：", "Mental states (pawn.mental): ")).Append(MentalStateList()).Append('\n');
            if (social) sb.Append(Loc.Pick("特质(pawn.trait)：", "Traits (pawn.trait): ")).Append(TraitList()).Append('\n');
            if (social) sb.Append(Loc.Pick("关系(pawn.relation)：", "Relations (pawn.relation): ")).Append(RelationList()).Append('\n');
            if (social) sb.Append(Loc.Pick("灵感(pawn.inspiration)：", "Inspirations (pawn.inspiration): ")).Append(InspirationList()).Append('\n');
            if (scene) sb.Append(Loc.Pick("地形(map.terrain)：", "Terrain (map.terrain): ")).Append(TerrainList()).Append('\n');
            if (scene) sb.Append(Loc.Pick("屋顶(map.roof，写 none 表示拆掉)：", "Roofs (map.roof; write none to remove): ")).Append(RoofList()).Append('\n');
            if (scene) sb.Append(Loc.Pick("污渍(map.filth)：", "Filth (map.filth): ")).Append(FilthList()).Append('\n');

            if (sb.Length == 0) return "";

            return Loc.Pick(
                "\n【可用的 DefName 名单】（只能从这些名字里选，写错会导致整段脚本被拒）\n",
                "\n【Available DefNames】 (choose only from these names; a typo rejects the whole script)\n") + sb;
        }
    }
}