using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace Stellaris.AI
{
    /// <summary>
    /// 一条可查询的 Def。索引里只存渲染/匹配需要的字段，不把 Def 对象交给 Lua。
    /// </summary>
    public sealed class DefEntry
    {
        public string DefName = "";
        public string Label = "";
        public string Description = "";
        public string Mod = "";
        public string Facts = "";
        public string Category = "";
        public float Numeric;
        public Def Def;
        public IncidentCandidate Incident;

        internal string NDef = "";
        internal string NLabel = "";
        internal string NDesc = "";

        public static DefEntry FromDef(Def def, string facts)
        {
            if (def == null || string.IsNullOrEmpty(def.defName)) return null;

            string label = Clean(def.label, def.defName, 80);
            string desc = Clean(def.description, "", 400);

            var entry = new DefEntry
            {
                DefName = def.defName,
                Label = label,
                Description = desc,
                Mod = ModOf(def),
                Facts = facts ?? "",
                Def = def
            };
            entry.Normalize();
            return entry;
        }

        public static DefEntry FromIncident(IncidentCandidate candidate)
        {
            if (candidate == null || string.IsNullOrEmpty(candidate.DefName)) return null;

            Def def = candidate.Def;
            var entry = new DefEntry
            {
                DefName = candidate.DefName,
                Label = def != null ? Clean(def.label, candidate.DefName, 80) : candidate.DefName,
                Description = def != null ? Clean(def.description, "", 400) : "",
                Mod = def != null ? ModOf(def) : "",
                Facts = Loc.Pick("类别 " + candidate.Category + " · 最低 " + candidate.MinPoints.ToString("F0") + " 点",
                    "category " + candidate.Category + " · min " + candidate.MinPoints.ToString("F0") + " points"),
                Category = candidate.Category,
                Numeric = candidate.MinPoints,
                Def = def,
                Incident = candidate
            };
            entry.Normalize();
            return entry;
        }

        private void Normalize()
        {
            NDef = NormalizeText(DefName);
            NLabel = NormalizeText(Label);
            NDesc = NormalizeText(Description);
        }

        public static string NormalizeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
        }

        private static string ModOf(Def def)
        {
            try
            {
                return def != null && def.modContentPack != null && def.modContentPack.Name != null
                    ? def.modContentPack.Name
                    : "";
            }
            catch
            {
                return "";
            }
        }

        private static string Clean(string text, string fallback, int max)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback ?? "";

            var sb = new StringBuilder(Math.Min(text.Length, max + 8));
            for (int i = 0; i < text.Length && sb.Length <= max; i++)
            {
                char c = text[i];
                if (c == '\r' || c == '\n' || c == '\t')
                {
                    sb.Append(' ');
                }
                else if (!char.IsControl(c))
                {
                    sb.Append(c == '|' ? '/' : c);
                }
            }

            string result = sb.ToString().Trim();
            if (result.Length > max) result = result.Substring(0, max).Trim() + "…";
            return result.Length > 0 ? result : (fallback ?? "");
        }
    }

    /// <summary>
    /// 一类可查询的 Def。kind 是固定白名单，模型不能创造新类别。
    /// </summary>
    public sealed class DefKind
    {
        public string Id;
        public string Label;
        public ActionCategoryMask Categories;
        public bool Cacheable = true;
        public Func<List<DefEntry>> Build;
        public Func<DefEntry, bool> RuntimeFilter;
    }

    /// <summary>
    /// 固定的 Def 类别表。只收当前动作库里真的有参数会用到、并且能安全展示的类别。
    /// </summary>
    public static class DefKindRegistry
    {
        private static readonly List<DefKind> _all = new List<DefKind>();

        public static IReadOnlyList<DefKind> All => _all;

        public static int Count => _all.Count;

        static DefKindRegistry()
        {
            // ── 生成 / 物品 ────────────────────────────────────────────────
            _all.Add(Defs<ThingDef>(
                "item", Loc.Pick("物品", "items"), ActionCategoryMask.Spawn,
                d => d.category == ThingCategory.Item && d.BaseMarketValue >= 1f && !d.IsCorpse,
                d => Loc.Pick("市场价 " + d.BaseMarketValue.ToString("F0") + " · 堆叠 " + d.stackLimit,
                    "market value " + d.BaseMarketValue.ToString("F0") + " · stack " + d.stackLimit)));

            _all.Add(Defs<PawnKindDef>(
                "pawnKind", Loc.Pick("人形角色", "humanlike pawn kinds"),
                ActionCategoryMask.Spawn | ActionCategoryMask.Pawn,
                d => d.race != null && d.race.race != null && d.race.race.Humanlike &&
                     d.GetType() == typeof(PawnKindDef),
                d => Loc.Pick("人形", "humanlike")));

            _all.Add(Defs<PawnKindDef>(
                "animal", Loc.Pick("动物", "animals"), ActionCategoryMask.Scene | ActionCategoryMask.Spawn,
                d => d.race != null && d.race.race != null && !d.race.race.Humanlike &&
                     d.GetType() == typeof(PawnKindDef),
                d => Loc.Pick("非人形", "non-humanlike")));

            _all.Add(Defs<ThingDef>(
                "building", Loc.Pick("建筑", "buildings"), ActionCategoryMask.Scene,
                d => d.category == ThingCategory.Building && d.building != null
                     && !d.mineable && !d.building.isNaturalRock,
                d => Loc.Pick("建筑", "building")));

            _all.Add(Defs<TerrainDef>(
                "terrain", Loc.Pick("地形", "terrain types"), ActionCategoryMask.Scene,
                d => d.natural,
                d => Loc.Pick("地形", "terrain")));

            _all.Add(Defs<RoofDef>(
                "roof", Loc.Pick("屋顶", "roof types"), ActionCategoryMask.Scene,
                d => true,
                d => Loc.Pick("屋顶", "roof")));

            _all.Add(Defs<ThingDef>(
                "filth", Loc.Pick("污渍", "filth types"), ActionCategoryMask.Scene,
                d => typeof(Filth).IsAssignableFrom(d.thingClass),
                d => Loc.Pick("污渍", "filth")));

            // ── 地图 / 环境 ────────────────────────────────────────────────
            _all.Add(Defs<WeatherDef>(
                "weather", Loc.Pick("天气", "weather types"), ActionCategoryMask.Map,
                d => true,
                d => Loc.Pick("天气", "weather")));

            _all.Add(Defs<GameConditionDef>(
                "condition", Loc.Pick("地图状况", "map conditions"), ActionCategoryMask.Map,
                d => true,
                d => Loc.Pick("地图状况", "map condition")));

            _all.Add(Defs<DamageDef>(
                "damage", Loc.Pick("伤害类型", "damage types"), ActionCategoryMask.Map,
                d => true,
                d => Loc.Pick("伤害类型", "damage type")));

            _all.Add(Defs<ThingDef>(
                "plant", Loc.Pick("植物", "plants"), ActionCategoryMask.Map,
                d => typeof(Plant).IsAssignableFrom(d.thingClass) && d.plant != null,
                d => Loc.Pick("植物", "plant")));

            // ── 角色 / 社会 ────────────────────────────────────────────────
            _all.Add(Defs<HediffDef>(
                "hediff", Loc.Pick("负面状态", "negative hediffs"), ActionCategoryMask.Pawn,
                d => d.isBad && d.hediffClass != null,
                d => Loc.Pick("负面状态", "negative hediff")));

            _all.Add(Defs<SkillDef>(
                "skill", Loc.Pick("技能", "skills"), ActionCategoryMask.Pawn,
                d => true,
                d => Loc.Pick("技能", "skill")));

            _all.Add(Defs<ThingDef>(
                "apparel", Loc.Pick("衣物", "apparel"), ActionCategoryMask.Pawn,
                d => typeof(Apparel).IsAssignableFrom(d.thingClass),
                d => Loc.Pick("衣物", "apparel")));

            _all.Add(Defs<ThingDef>(
                "weapon", Loc.Pick("武器", "weapons"), ActionCategoryMask.Pawn,
                d => d.IsWeapon && d.equipmentType == EquipmentType.Primary,
                d => Loc.Pick("武器", "weapon")));

            _all.Add(Defs<ThoughtDef>(
                "thought", Loc.Pick("想法", "thoughts"), ActionCategoryMask.Social,
                d => d.IsMemory,
                d => Loc.Pick("记忆类想法", "memory thought")));

            _all.Add(Defs<MentalStateDef>(
                "mentalState", Loc.Pick("精神状态", "mental states"), ActionCategoryMask.Social,
                d => true,
                d => Loc.Pick("精神状态", "mental state")));

            _all.Add(Defs<TraitDef>(
                "trait", Loc.Pick("特质", "traits"), ActionCategoryMask.Social,
                d => true,
                d => Loc.Pick("特质", "trait")));

            _all.Add(Defs<PawnRelationDef>(
                "relation", Loc.Pick("关系", "relations"), ActionCategoryMask.Social,
                d => true,
                d => Loc.Pick("关系", "relation")));

            _all.Add(Defs<InspirationDef>(
                "inspiration", Loc.Pick("灵感", "inspirations"), ActionCategoryMask.Social,
                d => true,
                d => Loc.Pick("灵感", "inspiration")));

            // ── 世界 ───────────────────────────────────────────────────────
            _all.Add(Defs<ResearchProjectDef>(
                "research", Loc.Pick("研究", "research projects"), ActionCategoryMask.World,
                d => true,
                d => Loc.Pick("研究", "research"),
                e => e.Def is ResearchProjectDef r && !r.IsFinished));

            _all.Add(Defs<QuestScriptDef>(
                "quest", Loc.Pick("任务模板", "quest templates"), ActionCategoryMask.World,
                d => d.root != null,
                d => Loc.Pick("任务模板", "quest template")));

            _all.Add(new DefKind
            {
                Id = "faction",
                Label = Loc.Pick("派系", "factions"),
                Categories = ActionCategoryMask.World,
                Cacheable = false,
                Build = BuildFactions
            });

            _all.Add(new DefKind
            {
                Id = "incident",
                Label = Loc.Pick("原版事件", "vanilla incidents"),
                Categories = ActionCategoryMask.Narrative,
                Cacheable = false,
                Build = BuildIncidents
            });
        }

        public static DefKind Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < _all.Count; i++)
            {
                if (string.Equals(_all[i].Id, id, StringComparison.OrdinalIgnoreCase)) return _all[i];
            }

            return null;
        }

        public static List<DefKind> ForMask(ActionCategoryMask mask)
        {
            var list = new List<DefKind>();
            for (int i = 0; i < _all.Count; i++)
            {
                DefKind kind = _all[i];
                if (kind.Id == "incident") continue; // 导演/编剧另有候选表，不放进目录
                if ((kind.Categories & mask) == 0) continue;
                list.Add(kind);
            }

            return list;
        }

        public static string ValidIds()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _all.Count; i++)
            {
                if (sb.Length > 0) sb.Append('/');
                sb.Append(_all[i].Id);
            }

            return sb.ToString();
        }

        private static DefKind Defs<T>(string id, string label, ActionCategoryMask categories,
            Func<T, bool> filter, Func<T, string> facts,
            Func<DefEntry, bool> runtimeFilter = null)
            where T : Def
        {
            return new DefKind
            {
                Id = id,
                Label = label,
                Categories = categories,
                RuntimeFilter = runtimeFilter,
                Build = () =>
                {
                    var list = new List<DefEntry>();
                    foreach (T def in DefDatabase<T>.AllDefs)
                    {
                        try
                        {
                            if (def == null) continue;
                            if (filter != null && !filter(def)) continue;

                            DefEntry entry = DefEntry.FromDef(def, facts != null ? facts(def) : "");
                            if (entry != null) list.Add(entry);
                        }
                        catch (Exception e)
                        {
                            // 单个坏 Def 不能拖垮整类；日志只在 debug 下详细，避免刷屏。
                            if (AINarratorMod.Settings != null && AINarratorMod.Settings.debugLogging)
                            {
                                Verse.Log.Warning(AINarratorMod.LogPrefix + "DefIndex skip " + id + ": "
                                                  + e.GetType().Name + ": " + e.Message);
                            }
                        }
                    }

                    return list;
                }
            };
        }

        private static List<DefEntry> BuildFactions()
        {
            var list = new List<DefEntry>();
            foreach (Faction faction in Find.FactionManager.AllFactions)
            {
                try
                {
                    if (faction == null || faction.IsPlayer || faction.def == null) continue;

                    string label = string.IsNullOrEmpty(faction.def.label)
                        ? faction.def.defName
                        : faction.def.label;

                    var entry = new DefEntry
                    {
                        DefName = faction.def.defName,
                        Label = label,
                        Description = faction.def.description ?? "",
                        Mod = faction.def.modContentPack != null ? faction.def.modContentPack.Name : "",
                        Facts = Loc.Pick("派系", "faction")
                    };

                    entry.NDef = DefEntry.NormalizeText(entry.DefName);
                    entry.NLabel = DefEntry.NormalizeText(entry.Label);
                    entry.NDesc = DefEntry.NormalizeText(entry.Description);
                    list.Add(entry);
                }
                catch
                {
                    // 派系在载入过程中可能短暂不完整，跳过即可。
                }
            }

            return list;
        }

        private static List<DefEntry> BuildIncidents()
        {
            var list = new List<DefEntry>();
            IReadOnlyList<IncidentCandidate> all = CandidateTable.All;
            for (int i = 0; i < all.Count; i++)
            {
                DefEntry entry = DefEntry.FromIncident(all[i]);
                if (entry != null) list.Add(entry);
            }

            return list;
        }
    }
}
