using System.Text;
using RimWorld;
using Verse;

namespace Stellaris.AI
{
    /// <summary>
    /// 内置示例事件脚本。
    ///
    /// 两处复用：
    ///  * `mock:story` 假网关拿它当"模型写的脚本"；
    ///  * 调试窗的「注入示例事件」直接注册并启动它，**完全不调模型**。
    ///
    /// 它刻意覆盖了运行时的主要路径：发信 → 选项 → 分支（触发原版事件 / 记忆）→ 定时器。
    /// </summary>
    public static class SampleEventScript
    {
        public static string Title => Loc.Pick("山谷里的光", "A Light in the Valley");

        public static string Build(string threatDefName, bool allowLong)
        {
            var lua = new StringBuilder(2048);

            string startTitle = Loc.Pick("山谷里的光", "A Light in the Valley");
            string startText = Loc.Pick("夜里，营地西侧的山谷传来低沉的轰鸣，有人看见雪地上浮起一层淡蓝色的光。",
                "That night a low rumble rolled out of the valley west of camp, and someone saw a pale blue glow rising off the snow.");
            string choicePrompt = Loc.Pick("要派人去看看吗？", "Send someone to take a look?");
            string goLabel = Loc.Pick("带人前往山谷", "Take a group to the valley");
            string stayLabel = Loc.Pick("按兵不动", "Hold position");
            string arrivedTitle = Loc.Pick("抵达山谷", "Arrival at the valley");
            string arrivedText = Loc.Pick("光来自一处塌陷的旧反应堆，冷却液还在滴落。",
                "The glow comes from a collapsed old reactor; coolant is still dripping from it.");
            string stayText = Loc.Pick("我们选择按兵不动，山谷的光在两天后熄灭了。",
                "We chose to hold position, and the light in the valley went out two days later.");
            string fadedTitle = Loc.Pick("光熄灭了", "The light went out");
            string fadedText = Loc.Pick("山谷的方向重新归于黑暗，只剩风声。",
                "The direction of the valley is dark again, with only the wind to be heard.");

            lua.Append("function run(ctx, state)\n");
            lua.Append("  local actions = {}\n");
            lua.Append("  local s = state\n");

            lua.Append("  if ctx.trigger == \"start\" then\n");
            lua.Append("    actions[#actions+1] = { opId = \"s1\", type = \"story.send_letter\", "
                       + "args = { title = \"" + startTitle + "\", text = \"" + startText + "\" } }\n");
            lua.Append("    actions[#actions+1] = { opId = \"s2\", type = \"story.show_choices\", "
                       + "args = { prompt = \"" + choicePrompt + "\", options = { "
                       + "{ id = \"go\", label = \"" + goLabel + "\" }, { id = \"stay\", label = \"" + stayLabel + "\" } } } }\n");
            lua.Append("    s.stage = \"await_choice\"\n");
            if (allowLong)
            {
                lua.Append("    return rim.aist.json_encode({ actions = actions, next_state = s, stage = \"await_choice\", "
                           + "timer = { hours = 2, signal = \"game_timer_due\" } })\n");
            }
            else
            {
                lua.Append("    return rim.aist.json_encode({ actions = actions, next_state = s, stage = \"await_choice\" })\n");
            }


            lua.Append("  elseif ctx.trigger == \"choice_selected\" then\n");
            lua.Append("    local c = ctx.payload.choice\n");
            lua.Append("    if c == \"go\" then\n");
            lua.Append("      actions[#actions+1] = { opId = \"s3\", type = \"story.send_letter\", "
                       + "args = { title = \"" + arrivedTitle + "\", text = \"" + arrivedText + "\" } }\n");
            if (!string.IsNullOrEmpty(threatDefName))
            {
                lua.Append("      actions[#actions+1] = { opId = \"s4\", type = \"incident.fire\", "
                           + "args = { defName = \"" + threatDefName + "\", points_multiplier = 0.8 } }\n");
            }

            lua.Append("    else\n");
            lua.Append("      actions[#actions+1] = { opId = \"s5\", type = \"story.remember\", "
                       + "args = { text = \"" + stayText + "\" } }\n");
            lua.Append("    end\n");
            lua.Append("    s.stage = \"done\"\n");
            lua.Append("    return rim.aist.json_encode({ actions = actions, next_state = s, done = true })\n");

            lua.Append("  elseif ctx.trigger == \"game_timer_due\" then\n");
            lua.Append("    if s.stage == \"await_choice\" then\n");
            lua.Append("      actions[#actions+1] = { opId = \"s6\", type = \"story.send_letter\", "
                       + "args = { title = \"" + fadedTitle + "\", text = \"" + fadedText + "\" } }\n");
            lua.Append("    end\n");
            lua.Append("    s.stage = \"idle\"\n");
            lua.Append("    return rim.aist.json_encode({ actions = actions, next_state = s, done = true })\n");
            lua.Append("  end\n");

            lua.Append("  return rim.aist.json_encode({ actions = actions, next_state = s })\n");
            lua.Append("end\n");

            return lua.ToString();
        }


        // ─────────────────────────────────────────────────────────────────────
        // P4 演示：一次过覆盖新增的心理 / 场景 / 交互动作
        // ─────────────────────────────────────────────────────────────────────
        public static string DemoTitle => Loc.Pick("P4 动作演示", "P4 action demo");

        public static string BuildP4Demo()
        {
            string humanlike = PickPawnKind(true);
            string animal = PickPawnKind(false);
            string thought = PickThought();
            string trait = PickTrait();
            Pawn colonist = PickColonist();
            string inspiration = PickInspiration(colonist);
            string relation = PickRelation();
            string terrain = TerrainDefOf.Soil != null ? TerrainDefOf.Soil.defName : null;
            string filth = ThingDefOf.Filth_Blood != null ? ThingDefOf.Filth_Blood.defName : null;
            string wall = ThingDefOf.Wall != null ? ThingDefOf.Wall.defName : null;
            string wood = ThingDefOf.WoodLog != null ? ThingDefOf.WoodLog.defName : null;

            string demoTitle = Loc.Pick("P4 动作演示", "P4 action demo");
            string demoText = Loc.Pick("这一条脚本会把新增动作全部走一遍。",
                "This script walks through every newly added action once.");
            string objectiveText = Loc.Pick("把山谷里的那层光查清楚", "Find out what that glow in the valley is");
            string letterPrompt = Loc.Pick("这封信放在信件栏里，不弹窗。你想怎么做？",
                "This letter sits in the letter stack without a popup. What will you do?");
            string goLabel = Loc.Pick("去看看", "Go and take a look");
            string waitLabel = Loc.Pick("再等等", "Wait a while longer");
            string demoDoneTitle = Loc.Pick("演示结束", "Demo finished");
            string demoDonePrefix = Loc.Pick("你选择了：", "You chose: ");

            var lua = new StringBuilder(4096);
            lua.Append("function run(ctx, state)\n");
            lua.Append("  local actions = {}\n");
            lua.Append("  local s = state\n");
            lua.Append("  if ctx.trigger == \"start\" then\n");

            lua.Append("    actions[#actions+1] = { opId = \"d1\", type = \"story.send_letter\", args = { title = \"" + demoTitle + "\", text = \"" + demoText + "\" } }\n");
            lua.Append("    actions[#actions+1] = { opId = \"d2\", type = \"story.focus\", args = { spot = \"center\" } }\n");
            if (!string.IsNullOrEmpty(filth))
            {
                lua.Append("    actions[#actions+1] = { opId = \"d3\", type = \"map.filth\", args = { filthDef = \"" + filth + "\", spot = \"center\", count = 3, jitter = 3 } }\n");
            }

            if (!string.IsNullOrEmpty(wall) && !string.IsNullOrEmpty(wood))
            {
                lua.Append("    actions[#actions+1] = { opId = \"d4\", type = \"map.building\", args = { thingDef = \"" + wall + "\", stuff = \"" + wood + "\", spot = \"random\", count = 2 } }\n");
            }

            if (!string.IsNullOrEmpty(terrain))
            {
                lua.Append("    actions[#actions+1] = { opId = \"d5\", type = \"map.terrain\", args = { terrainDef = \"" + terrain + "\", spot = \"random\", radius = 2 } }\n");
            }

            if (!string.IsNullOrEmpty(animal))
            {
                lua.Append("    actions[#actions+1] = { opId = \"d6\", type = \"map.animal\", args = { pawnKind = \"" + animal + "\", count = 2, spot = \"edge_north\" } }\n");
            }

            if (!string.IsNullOrEmpty(humanlike))
            {
                lua.Append("    actions[#actions+1] = { opId = \"d7\", type = \"pawn.spawn\", args = { pawnKind = \"" + humanlike + "\", count = 1, handle = \"p1\" } }\n");
                lua.Append("    actions[#actions+1] = { opId = \"d8\", type = \"pawn.spawn\", args = { pawnKind = \"" + humanlike + "\", count = 1, handle = \"p2\" } }\n");
                lua.Append("    actions[#actions+1] = { opId = \"d9\", type = \"pawn.mood\", args = { who = \"handle:p1\", delta = 0.1 } }\n");
                if (!string.IsNullOrEmpty(thought))
                {
                    lua.Append("    actions[#actions+1] = { opId = \"d10\", type = \"pawn.thought\", args = { who = \"handle:p1\", thoughtDef = \"" + thought + "\" } }\n");
                }

                if (!string.IsNullOrEmpty(inspiration))
                {
                    // 原版灵感只能给殖民者；p1 是刚生成的 NPC，指向它会被校验拒绝。
                    lua.Append("    actions[#actions+1] = { opId = \"d11\", type = \"pawn.inspiration\", args = { who = \"colonist:" + colonist.LabelShort + "\", inspirationDef = \"" + inspiration + "\", replace = true } }\n");
                }

                if (!string.IsNullOrEmpty(trait))
                {
                    lua.Append("    actions[#actions+1] = { opId = \"d12\", type = \"pawn.trait\", args = { who = \"handle:p1\", traitDef = \"" + trait + "\" } }\n");
                }

                if (!string.IsNullOrEmpty(relation))
                {
                    lua.Append("    actions[#actions+1] = { opId = \"d13\", type = \"pawn.relation\", args = { who = \"handle:p1\", other = \"handle:p2\", relationDef = \"" + relation + "\" } }\n");
                }
            }

            lua.Append("    actions[#actions+1] = { opId = \"d14\", type = \"map.remove\", args = { spot = \"random\", radius = 2, only = \"plants,filth\" } }\n");
            lua.Append("    actions[#actions+1] = { opId = \"d15\", type = \"story.objective\", args = { text = \"" + objectiveText + "\", days = 3 } }\n");
            lua.Append("    actions[#actions+1] = { opId = \"d16\", type = \"story.choice_letter\", args = { prompt = \"" + letterPrompt + "\", options = { { id = \"go\", label = \"" + goLabel + "\" }, { id = \"wait\", label = \"" + waitLabel + "\" } } } }\n");

            lua.Append("    s.stage = \"await_choice\"\n");
            lua.Append("    return rim.aist.json_encode({ actions = actions, next_state = s, stage = \"await_choice\" })\n");

            lua.Append("  elseif ctx.trigger == \"choice_selected\" then\n");
            lua.Append("    actions[#actions+1] = { opId = \"d17\", type = \"story.send_letter\", args = { title = \"" + demoDoneTitle + "\", text = \"" + demoDonePrefix + "\" .. tostring(ctx.payload.choice) } }\n");
            lua.Append("    return rim.aist.json_encode({ actions = actions, next_state = s, done = true })\n");
            lua.Append("  end\n");
            lua.Append("  return rim.aist.json_encode({ actions = actions, next_state = s })\n");
            lua.Append("end\n");
            return lua.ToString();
        }

        private static string PickPawnKind(bool humanlike)
        {
            foreach (PawnKindDef def in DefDatabase<PawnKindDef>.AllDefs)
            {
                if (def == null || def.race == null || def.race.race == null) continue;
                bool ok = humanlike ? def.race.race.Humanlike : def.race.race.Animal;
                if (ok && PawnKindSafety.IsPlainPawnKind(def)) return def.defName;
            }

            return null;
        }

        private static string PickThought()
        {
            foreach (ThoughtDef def in DefDatabase<ThoughtDef>.AllDefs)
            {
                if (def == null || !def.IsMemory) continue;
                if (def.thoughtClass != null && def.thoughtClass.Name.Contains("Social")) continue;
                return def.defName;
            }

            return null;
        }

        private static string PickTrait()
        {
            foreach (TraitDef def in DefDatabase<TraitDef>.AllDefs)
            {
                if (def != null) return def.defName;
            }

            return null;
        }

        private static Pawn PickColonist()
        {
            Map map = Find.AnyPlayerHomeMap;
            if (map == null || map.mapPawns == null) return null;

            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn != null && !pawn.Dead) return pawn;
            }

            return null;
        }

        private static string PickInspiration(Pawn pawn)
        {
            if (pawn == null) return null;

            foreach (InspirationDef def in DefDatabase<InspirationDef>.AllDefs)
            {
                if (def == null || def.Worker == null) continue;
                if (!def.Worker.InspirationCanOccur(pawn)) continue;
                return def.defName;
            }

            return null;
        }

        private static string PickRelation()
        {
            PawnRelationDef def = DefDatabase<PawnRelationDef>.GetNamedSilentFail("Rival");
            if (def != null) return def.defName;

            foreach (PawnRelationDef candidate in DefDatabase<PawnRelationDef>.AllDefs)
            {
                if (candidate != null) return candidate.defName;
            }

            return null;
        }

        /// <summary>挑一个当前真能触发的威胁类候选，没有就返回 null。</summary>
        public static string PickThreatDefName()
        {
            System.Collections.Generic.IReadOnlyList<IncidentCandidate> all = CandidateTable.All;
            for (int i = 0; i < all.Count; i++)
            {
                if (BudgetPolicy.IsThreat(all[i].Category)) return all[i].DefName;
            }

            return all.Count > 0 ? all[0].DefName : null;
        }
    }
}