using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Stellaris.AI
{
    // ─────────────────────────────────────────────────────────────────────────
    // pawn.hurt —— 给角色加一个状态（外伤必须落在具体部位）
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class PawnHurtHandler : IGameActionHandler
    {
        public string OpType => "pawn.hurt";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Pawn;
        public TargetPolicy Policy => TargetPolicy.Dangerous;

        public string Describe() =>
            "pawn.hurt{who=handle:p1 | colonist:名字, hediffDef=状态 DefName（外伤：Cut / Bruise / Gunshot；疾病：WoundInfection）, part=可选（Leg / Arm / Head / 左腿）, damage=外伤伤害点数（1~部位血量，如躯干 40）, severity=疾病严重度 0.05~0.95（1=死亡）} " +
            "—— 外伤用 damage（Cut/Gunshot/Bruise…），疾病用 severity（Flu/Plague/WoundInfection…）；外伤不写 part 自动挑一个；能否指向你的殖民者由设置决定";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!TryPawn(a, ctx, Policy, out Pawn pawn, out error)) return false;

            string name = a.ArgString("hediffDef");
            if (name.Length == 0) { error = "缺少 hediffDef"; return false; }

            HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail(name);
            if (def == null) { error = "不存在的 HediffDef：" + name; return false; }

            if (BodyPartPicker.IsStructural(def))
            {
                error = name + " 是身体结构类状态（缺肢 / 假肢 / 植入物），不能用 pawn.hurt 施加；"
                        + "受伤请用 Cut / Bruise / Gunshot 这类外伤";
                return false;
            }

            // severity 的语义按状态类型分开：
            //   外伤（Hediff_Injury）：Severity = 伤害点数，可以超过 1（躯干 40 点）；
            //   疾病（有 lethalSeverity）：Severity = 0..1 的病程，到 1 就死亡。
            bool injury = BodyPartPicker.IsInjury(def);
            float damage = a.ArgFloat("damage", -1f);
            float severity = a.ArgFloat("severity", -1f);

            // 同阶段"先生成、后受伤"时，校验阶段 pawn 还是 null（pending），只能验定义本身。
            BodyPartRecord part = null;
            if (pawn != null)
            {
                string partToken = a.ArgString("part");
                if (partToken.Length > 0)
                {
                    if (!BodyPartPicker.TryResolve(pawn, partToken, out part, out error)) return false;
                }
                else if (BodyPartPicker.RequiresPart(def))
                {
                    part = BodyPartPicker.PickForInjury(pawn);
                    if (part == null) { error = "目标没有可受伤的身体部位"; return false; }
                }
            }

            if (injury)
            {
                if (damage >= 0f)
                {
                    if (damage < 1f) { error = "damage 至少 1 点"; return false; }
                    if (part != null)
                    {
                        float max = BodyPartPicker.PartHitPoints(part);
                        if (damage > max)
                        {
                            error = "damage 超过 " + part.def.defName + " 的血量（上限 " + max.ToString("F0")
                                    + " 点）；要更重的伤请先确认部位";
                            return false;
                        }
                    }
                }
                else if (severity >= 0f && (severity <= 0f || severity > 1f))
                {
                    error = "外伤的 severity 表示「部位血量的比例」（0.05~1）；要精确点数请改用 damage";
                    return false;
                }
            }
            else
            {
                if (damage >= 0f)
                {
                    error = name + " 不是外伤，请用 severity（0.05~0.95），不要写 damage";
                    return false;
                }

                float value = severity < 0f ? 0.3f : severity;
                if (value <= 0f || value >= 1f)
                {
                    error = "severity 必须在 0.05..0.95（1 会直接致死，请用 pawn.kill）";
                    return false;
                }

                if (def.lethalSeverity > 0f && value >= def.lethalSeverity)
                {
                    error = name + " 的 lethalSeverity 是 " + def.lethalSeverity.ToString("F2")
                            + "，severity 到值即死亡；请把 severity 降到 "
                            + (def.lethalSeverity * 0.9f).ToString("F2") + " 以下";
                    return false;
                }
            }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.PawnEdit, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!TryPawn(a, ctx, Policy, out Pawn pawn, out error)) return false;

            HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail(a.ArgString("hediffDef"));
            if (def == null) { error = "动作所需定义已不可用"; return false; }

            BodyPartRecord part = null;
            string partToken = a.ArgString("part");
            if (partToken.Length > 0)
            {
                if (!BodyPartPicker.TryResolve(pawn, partToken, out part, out error)) return false;
            }
            else if (BodyPartPicker.RequiresPart(def))
            {
                // 关键：以前这里不传 part，生成的是 "(no body part)" 的伤，健康面板与治疗逻辑都会出错。
                part = BodyPartPicker.PickForInjury(pawn);
                if (part == null) { error = "目标没有可受伤的身体部位"; return false; }
            }

            float finalSeverity = ComputeSeverity(a, def, part, out error);
            if (error != null) return false;

            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.PawnEdit, 1, out error)) return false;

            Hediff hediff = HediffMaker.MakeHediff(def, pawn, part);
            if (hediff == null) { error = "未能创建健康状态"; return false; }

            hediff.Severity = finalSeverity;
            pawn.health.AddHediff(hediff, part);
            return true;
        }

        /// <summary>
        /// 把脚本给的 damage / severity 换算成 Hediff.Severity。
        ///
        /// 两套语义必须分开（这是 0.6.4 修的）：
        ///  * 外伤：Severity = 伤害点数，上限是部位血量（躯干 40 点），可以远超 1；
        ///    脚本写 damage 就是点数；写 severity 则按"部位血量的比例"折算。
        ///  * 疾病：Severity = 0..1 的病程，lethalSeverity 到值即死亡；这里封顶在 90%。
        /// </summary>
        private static float ComputeSeverity(PlannedAction a, HediffDef def, BodyPartRecord part, out string error)
        {
            error = null;

            if (BodyPartPicker.IsInjury(def))
            {
                float hp = BodyPartPicker.PartHitPoints(part);
                float damage = a.ArgFloat("damage", -1f);

                if (damage < 0f)
                {
                    float fraction = a.ArgFloat("severity", 0.25f);
                    if (fraction <= 0f) fraction = 0.25f;
                    damage = hp * Math.Min(1f, fraction);
                }

                // 留 5%：让"重伤"停在濒死而不是直接把部位打成缺失（那会走另一套逻辑）。
                float cap = Math.Max(1f, hp * 0.95f);
                return Math.Max(1f, Math.Min(cap, damage));
            }

            float value = a.ArgFloat("severity", 0.3f);
            float lethal = def.lethalSeverity;
            float max = lethal > 0f ? lethal * 0.9f : 0.95f;
            return Math.Max(0.05f, Math.Min(max, value));
        }

        /// <summary>
        /// 目标解析交给 <see cref="WhoResolver"/>：句柄 / 名字 / best / worst 都支持，
        /// 但"能不能落到玩家殖民者身上"由 policy 决定（这里是危险动作）。
        /// </summary>
        internal static bool TryPawn(PlannedAction a, ActionContext ctx, TargetPolicy policy,
            out Pawn pawn, out string error) =>
            WhoResolver.TryResolve(a, ctx, policy, false, out pawn, out error);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // pawn.heal —— 治疗 / 移除状态（支持按部位过滤）
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class PawnHealHandler : IGameActionHandler
    {
        public string OpType => "pawn.heal";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Pawn;
        public TargetPolicy Policy => TargetPolicy.Positive;

        public string Describe() =>
            "pawn.heal{who=handle:p1 | colonist:名字 | best:技能名, hediffDef=可选（常用：Cut / WoundInfection）, part=可选} " +
            "—— 指定状态就移除它（同一状态在多处时全部移除）；不指定就移除该角色所有负面状态";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out Pawn pawn, out error)) return false;

            string name = a.ArgString("hediffDef");
            if (name.Length > 0 && DefDatabase<HediffDef>.GetNamedSilentFail(name) == null)
            {
                error = "不存在的 HediffDef：" + name;
                return false;
            }

            if (pawn != null)
            {
                string partToken = a.ArgString("part");
                if (partToken.Length > 0 && !BodyPartPicker.TryResolve(pawn, partToken, out _, out error))
                {
                    return false;
                }
            }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.PawnEdit, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out Pawn pawn, out error)) return false;
            if (pawn.health == null || pawn.health.hediffSet == null) { error = "目标没有健康系统"; return false; }

            BodyPartRecord part = null;
            string partToken = a.ArgString("part");
            if (partToken.Length > 0 && !BodyPartPicker.TryResolve(pawn, partToken, out part, out error))
            {
                return false;
            }

            string name = a.ArgString("hediffDef");
            if (name.Length > 0)
            {
                HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail(name);
                if (def == null) { error = "动作所需定义已不可用"; return false; }

                var matches = new List<Hediff>();
                foreach (Hediff hediff in pawn.health.hediffSet.hediffs)
                {
                    if (hediff == null || hediff.def != def) continue;
                    if (part != null && hediff.Part != part) continue;
                    matches.Add(hediff);
                }

                if (matches.Count == 0) { error = "目标没有指定的健康状态"; return false; }
                if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.PawnEdit, 1, out error)) return false;

                for (int i = 0; i < matches.Count; i++) pawn.health.RemoveHediff(matches[i]);
                return true;
            }

            var toRemove = new List<Hediff>();
            foreach (Hediff hediff in pawn.health.hediffSet.hediffs)
            {
                if (hediff == null || hediff.def == null || !hediff.def.isBad) continue;
                if (part != null && hediff.Part != part) continue;
                toRemove.Add(hediff);
            }

            if (toRemove.Count == 0) { error = "目标没有可移除的负面状态"; return false; }
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.PawnEdit, 1, out error)) return false;
            for (int i = 0; i < toRemove.Count; i++) pawn.health.RemoveHediff(toRemove[i]);
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // pawn.kill
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class PawnKillHandler : IGameActionHandler
    {
        public string OpType => "pawn.kill";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Pawn;
        public TargetPolicy Policy => TargetPolicy.Dangerous;

        public string Describe() =>
            "pawn.kill{who=handle:p1 | colonist:名字} —— 让该角色死亡（真实死亡流程，会产生尸体，计入危险预算）；能否指向你的殖民者由设置决定";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out _, out error)) return false;
            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.Danger, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out Pawn pawn, out error)) return false;
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.Danger, 1, out error)) return false;

            pawn.Kill(null, null);
            if (!pawn.Dead) { error = "目标未死亡"; return false; }
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // pawn.gear —— 穿戴 / 装备
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class PawnGearHandler : IGameActionHandler
    {
        public string OpType => "pawn.gear";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Pawn;
        public TargetPolicy Policy => TargetPolicy.Neutral;

        public string Describe() =>
            "pawn.gear{who=handle:p1 | colonist:名字, thingDef=装备 DefName 或 @apparel/关键词, slot=apparel|weapon} " +
            "—— 给该角色穿戴/装备一件物品；会检查身体部位是否够用";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out Pawn pawn, out error)) return false;

            string name = a.ArgString("thingDef");
            if (name.Length == 0) { error = "缺少 thingDef"; return false; }

            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
            if (def == null) { error = "不存在的 ThingDef：" + name; return false; }

            string slot = a.ArgString("slot", "apparel").ToLowerInvariant();
            if (slot != "apparel" && slot != "weapon")
            {
                error = "slot 只能是 apparel 或 weapon";
                return false;
            }

            if (pawn != null)
            {
                if (slot == "apparel" && !ValidateApparel(pawn, def, out error)) return false;
                if (slot == "weapon" && !ValidateWeapon(pawn, def, out error)) return false;
            }
            else
            {
                // 句柄还没生成：只能验定义本身，别去碰 pawn 的衣物/装备系统。
                if (slot == "apparel" &&
                    (def.apparel == null || !typeof(Apparel).IsAssignableFrom(def.thingClass)))
                {
                    error = def.defName + " 不是可穿的衣物";
                    return false;
                }

                if (slot == "weapon" && !def.IsWeapon)
                {
                    error = def.defName + " 不是武器";
                    return false;
                }
            }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.PawnEdit, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out Pawn pawn, out error)) return false;

            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(a.ArgString("thingDef"));
            if (def == null) { error = "动作所需定义已不可用"; return false; }

            string slot = a.ArgString("slot", "apparel").ToLowerInvariant();

            if (slot == "apparel")
            {
                if (!ValidateApparel(pawn, def, out error)) return false;
                if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.PawnEdit, 1, out error)) return false;

                Apparel apparel = ThingMaker.MakeThing(def) as Apparel;
                if (apparel == null) { error = "该定义不是可穿戴物"; return false; }

                try
                {
                    pawn.apparel.Wear(apparel, false, false);
                }
                catch (Exception e)
                {
                    error = "穿戴失败：" + e.Message;
                    return false;
                }

                return true;
            }

            if (!ValidateWeapon(pawn, def, out error)) return false;
            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.PawnEdit, 1, out error)) return false;

            ThingWithComps equipment = ThingMaker.MakeThing(def) as ThingWithComps;
            if (equipment == null) { error = "该定义不是可装备物"; return false; }

            try
            {
                pawn.equipment.AddEquipment(equipment);
            }
            catch (Exception e)
            {
                error = "装备失败：" + e.Message;
                return false;
            }

            return true;
        }

        private static bool ValidateApparel(Pawn pawn, ThingDef def, out string error)
        {
            error = null;

            if (def.apparel == null || !typeof(Apparel).IsAssignableFrom(def.thingClass))
            {
                error = def.defName + " 不是可穿的衣物";
                return false;
            }

            if (pawn.apparel == null)
            {
                error = "目标没有衣物系统（可能不是人形角色）";
                return false;
            }

            if (!ApparelUtility.HasPartsToWear(pawn, def))
            {
                error = pawn.LabelShort + " 没有可穿戴 " + def.defName + " 的身体部位";
                return false;
            }

            return true;
        }

        private static bool ValidateWeapon(Pawn pawn, ThingDef def, out string error)
        {
            error = null;

            if (!def.IsWeapon)
            {
                error = def.defName + " 不是武器";
                return false;
            }

            if (pawn.equipment == null)
            {
                error = "目标没有装备系统（可能不是人形角色）";
                return false;
            }

            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // pawn.set_skill
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class PawnSetSkillHandler : IGameActionHandler
    {
        public string OpType => "pawn.set_skill";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Pawn;
        public TargetPolicy Policy => TargetPolicy.NonPlayerOnly;

        public string Describe() =>
            "pawn.set_skill{who=handle:p1 | colonist:名字, skillDef=技能 DefName, level=0~20} —— 改该角色的技能等级；能否指向你的殖民者由设置决定";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out Pawn pawn, out error)) return false;

            string name = a.ArgString("skillDef");
            if (name.Length == 0) { error = "缺少 skillDef"; return false; }

            SkillDef def = DefDatabase<SkillDef>.GetNamedSilentFail(name);
            if (def == null) { error = "不存在的 SkillDef：" + name; return false; }

            int level = a.ArgInt("level", 8);
            if (level < 0 || level > 20) { error = "level 必须在 0..20"; return false; }

            if (pawn != null)
            {
                if (pawn.skills == null)
                {
                    error = "目标没有技能系统（可能不是人形角色）";
                    return false;
                }

                SkillRecord record = pawn.skills.GetSkill(def);
                if (record == null || record.TotallyDisabled)
                {
                    error = "目标没有可用的 " + name + " 技能";
                    return false;
                }
            }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.PawnEdit, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out Pawn pawn, out error)) return false;

            SkillDef def = DefDatabase<SkillDef>.GetNamedSilentFail(a.ArgString("skillDef"));
            if (def == null || pawn.skills == null) { error = "技能定义或目标技能系统不可用"; return false; }

            SkillRecord record = pawn.skills.GetSkill(def);
            if (record == null || record.TotallyDisabled) { error = "目标没有该技能"; return false; }

            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.PawnEdit, 1, out error)) return false;
            record.Level = Math.Max(0, Math.Min(20, a.ArgInt("level", 8)));
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // pawn.join
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class PawnJoinHandler : IGameActionHandler
    {
        public string OpType => "pawn.join";
        public ActionPermission Permission => ActionPermission.Major;
        public ActionCategory Category => ActionCategory.Pawn;
        public TargetPolicy Policy => TargetPolicy.NonPlayerOnly;

        public string Describe() =>
            "pawn.join{who=handle:p1 | colonist:名字} —— 让该角色加入玩家派系（重大后果，计入角色编辑预算）";

        public bool Validate(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out Pawn pawn, out error)) return false;

            if (pawn != null && pawn.Faction == Faction.OfPlayer)
            {
                error = "该角色已经属于玩家派系";
                return false;
            }

            return ActionBudget.TryPlan(ctx, ActionBudget.Pool.PawnEdit, 1, out error);
        }

        public bool TryExecute(PlannedAction a, ActionContext ctx, out string error)
        {
            error = null;
            if (!PawnHurtHandler.TryPawn(a, ctx, Policy, out Pawn pawn, out error)) return false;

            if (!ActionBudget.TryConsume(ctx, ActionBudget.Pool.PawnEdit, 1, out error)) return false;

            pawn.SetFaction(Faction.OfPlayer);
            if (pawn.Faction != Faction.OfPlayer) { error = "加入派系失败"; return false; }
            return true;
        }
    }

    /// <summary>
    /// 身体部位选择。
    ///
    /// 为什么必须做：HediffMaker.MakeHediff(def, pawn) 不传部位时，外伤会变成
    /// "(no body part)" —— 健康面板显示异常，后续治疗 / 疼痛 / 流血计算也会踩到空引用。
    /// 外伤（Hediff_Injury）必须落在 GetNotMissingParts() 里的一具真实部位上。
    ///
    /// 默认策略：优先避开"一伤就致命"的器官（脑 / 心 / 肝 / 肺），没有安全部位时才退回全部。
    /// 想让剧情更狠，显式写 part="Brain" 即可。
    /// </summary>
    internal static class BodyPartPicker
    {
        private static readonly string[] VitalTags =
        {
            "ConsciousnessSource", "BloodPumpingSource", "BloodFiltrationSource", "BreathingSource"
        };

        /// <summary>外伤：Hediff_Injury（Severity 是伤害点数，可以超过 1）。</summary>
        public static bool IsInjury(HediffDef def) =>
            def != null && def.hediffClass != null && typeof(Hediff_Injury).IsAssignableFrom(def.hediffClass);

        /// <summary>部位血量（外伤 severity 的上限）。</summary>
        public static float PartHitPoints(BodyPartRecord part)
        {
            if (part == null || part.def == null) return 0f;
            return Math.Max(1f, part.def.hitPoints);
        }

        /// <summary>身体结构类状态（缺肢/假肢/植入物）不是"受伤"，不能通过 pawn.hurt 施加。</summary>
        public static bool IsStructural(HediffDef def)
        {
            if (def == null || def.hediffClass == null) return false;
            return typeof(Hediff_MissingPart).IsAssignableFrom(def.hediffClass)
                   || typeof(Hediff_AddedPart).IsAssignableFrom(def.hediffClass)
                   || typeof(Hediff_Implant).IsAssignableFrom(def.hediffClass);
        }

        /// <summary>
        /// 需要部位的状态：外伤（Hediff_Injury）以及"可包扎"的伤口类状态（如 WoundInfection）。
        /// 流感/瘟疫这类全身疾病是 tendable=false，保持 whole-body。
        /// </summary>
        public static bool RequiresPart(HediffDef def)
        {
            if (def == null || def.hediffClass == null) return false;
            if (IsStructural(def)) return false;
            return typeof(Hediff_Injury).IsAssignableFrom(def.hediffClass) || def.tendable;
        }

        public static bool HasAny(Pawn pawn) => Available(pawn).Count > 0;

        public static BodyPartRecord PickForInjury(Pawn pawn) => Pick(Available(pawn));

        /// <summary>part 支持 defName（Leg）、本地化标签（左腿 / left leg），不区分大小写与空格。</summary>
        public static bool TryResolve(Pawn pawn, string token, out BodyPartRecord part, out string error)
        {
            part = null;
            error = null;

            List<BodyPartRecord> parts = Available(pawn);
            if (parts.Count == 0)
            {
                error = "目标没有可选的身体部位";
                return false;
            }

            token = (token ?? "").Trim();
            if (token.Length == 0)
            {
                part = Pick(parts);
                return part != null;
            }

            var byDef = new List<BodyPartRecord>();
            var byLabel = new List<BodyPartRecord>();
            for (int i = 0; i < parts.Count; i++)
            {
                BodyPartRecord candidate = parts[i];
                if (string.Equals(candidate.def.defName, token, StringComparison.OrdinalIgnoreCase))
                {
                    byDef.Add(candidate);
                    continue;
                }

                if (MatchesLabel(candidate, token)) byLabel.Add(candidate);
            }

            if (byDef.Count > 0)
            {
                part = Pick(byDef);
                return true;
            }

            if (byLabel.Count > 0)
            {
                part = byLabel[0];
                return true;
            }

            error = "找不到身体部位 \"" + token + "\"；可用：" + ListNames(parts)
                    + "。也可以不写 part，由游戏自动挑一个。";
            return false;
        }

        private static bool MatchesLabel(BodyPartRecord part, string token)
        {
            string norm = Normalize(token);
            if (norm.Length == 0) return false;

            if (Normalize(part.Label) == norm) return true;
            if (Normalize(part.def.label) == norm) return true;

            string label = Normalize(part.Label);
            return label.Length >= 2 && (label.Contains(norm) || norm.Contains(label));
        }

        private static string Normalize(string text) =>
            string.IsNullOrEmpty(text) ? "" : text.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");

        private static List<BodyPartRecord> Available(Pawn pawn)
        {
            var list = new List<BodyPartRecord>();
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null) return list;

            foreach (BodyPartRecord part in pawn.health.hediffSet.GetNotMissingParts())
            {
                if (part == null || part.def == null) continue;
                if (pawn.health.hediffSet.PartIsMissing(part)) continue;
                list.Add(part);
            }

            return list;
        }

        private static BodyPartRecord Pick(List<BodyPartRecord> parts)
        {
            if (parts == null || parts.Count == 0) return null;

            var safe = new List<BodyPartRecord>();
            for (int i = 0; i < parts.Count; i++)
            {
                if (!IsVital(parts[i])) safe.Add(parts[i]);
            }

            List<BodyPartRecord> pool = safe.Count > 0 ? safe : parts;
            return pool[Rand.Range(0, pool.Count)];
        }

        private static bool IsVital(BodyPartRecord part)
        {
            if (part == null || part.def == null || part.def.tags == null) return false;

            for (int i = 0; i < part.def.tags.Count; i++)
            {
                BodyPartTagDef tag = part.def.tags[i];
                if (tag == null) continue;
                for (int v = 0; v < VitalTags.Length; v++)
                {
                    if (string.Equals(tag.defName, VitalTags[v], StringComparison.OrdinalIgnoreCase)) return true;
                }
            }

            return false;
        }

        private static string ListNames(List<BodyPartRecord> parts)
        {
            var names = new List<string>();
            for (int i = 0; i < parts.Count && names.Count < 10; i++)
            {
                string name = parts[i].def.defName;
                if (!names.Contains(name)) names.Add(name);
            }

            return string.Join("、", names.ToArray());
        }
    }
}