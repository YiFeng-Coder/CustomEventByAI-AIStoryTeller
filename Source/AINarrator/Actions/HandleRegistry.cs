using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace Stellaris.AI
{
    /// <summary>脚本里声明的一个句柄：Lua 只看到名字，C# 侧映射到真实 Thing。</summary>
    public class HandleRecord
    {
        public string name = "";
        public string thingId = "";
        public int mapId = -1;
    }

    /// <summary>校验期记录"本阶段第几条生成动作声明了哪个句柄"，只用于错误诊断。</summary>
    internal sealed class PlannedHandleInfo
    {
        public string Name = "";
        public string ProducerType = "";
        public int ActionNumber;
    }

    /// <summary>
    /// 句柄层：让 Lua 能"指向"它之前生成出来的东西，而不必持有对象引用。
    ///
    /// 约定：
    ///  * 句柄由**脚本自己声明**（args.handle = "p1"），在生成类动作执行时登记；
    ///  * 句柄随事件实例存档（HandleRecord 存在 EventInstanceRecord 里），读档后照样能解析；
    ///  * 解析失败一律返回 false，由调用方给出可读错误，绝不猜。
    /// </summary>
    public static class HandleRegistry
    {
        public static void Register(ActionContext context, string name, Thing thing)
        {
            if (context == null || context.Instance == null) return;
            if (string.IsNullOrEmpty(name) || thing == null) return;

            EventInstanceRecord instance = context.Instance;

            for (int i = 0; i < instance.handles.Count; i++)
            {
                if (instance.handles[i].name != name) continue;

                instance.handles[i].thingId = thing.ThingID;
                instance.handles[i].mapId = context.Map != null ? context.Map.uniqueID : -1;
                return;
            }

            instance.handles.Add(new HandleRecord
            {
                name = name,
                thingId = thing.ThingID,
                mapId = context.Map != null ? context.Map.uniqueID : -1
            });

            if (instance.handles.Count > 32) instance.handles.RemoveAt(0);
        }

        public static bool TryResolve(ActionContext context, string name, out Thing thing)
        {
            HandleRecord record = FindRecord(context, name);
            thing = record == null ? null : FindById(record.thingId, context.Map);
            return thing != null;
        }

        private static HandleRecord FindRecord(ActionContext context, string name)
        {
            if (context == null || context.Instance == null || string.IsNullOrEmpty(name)) return null;
            List<HandleRecord> handles = context.Instance.handles;
            for (int i = 0; i < handles.Count; i++)
            {
                if (handles[i].name == name) return handles[i];
            }

            return null;
        }

        internal static void RecordPlannedCreation(ActionContext context, PlannedAction action)
        {
            string name = DeclaredHandle(action);
            if (name.Length > 0) context.PlannedHandles[name] = action.type;
        }

        /// <summary>扫描整阶段，记录所有生成动作声明的句柄；用于"引用在声明之前/名字写错"的诊断。</summary>
        internal static Dictionary<string, PlannedHandleInfo> IndexPlanHandles(List<PlannedAction> actions,
            out List<PlannedHandleInfo> withoutHandle)
        {
            var index = new Dictionary<string, PlannedHandleInfo>(StringComparer.Ordinal);
            withoutHandle = new List<PlannedHandleInfo>();
            if (actions == null) return index;

            for (int i = 0; i < actions.Count; i++)
            {
                PlannedAction action = actions[i];
                if (!IsSpawnProducer(action)) continue;

                string name = DeclaredHandle(action);
                if (name.Length == 0)
                {
                    withoutHandle.Add(new PlannedHandleInfo
                    {
                        Name = "",
                        ProducerType = action.type,
                        ActionNumber = i + 1
                    });
                    continue;
                }

                // 同名声明多次时保留最早一条："引用发生在声明之前"要看的是最早位置。
                if (!index.ContainsKey(name))
                {
                    index[name] = new PlannedHandleInfo
                    {
                        Name = name,
                        ProducerType = action.type,
                        ActionNumber = i + 1
                    };
                }
            }

            return index;
        }

        private static bool IsSpawnProducer(PlannedAction action) =>
            action != null && (action.type == "pawn.spawn" || action.type == "thing.spawn" ||
                               action.type == "map.corpse");

        /// <summary>只有生成类动作会声明句柄；名字来自 args.handle。</summary>
        internal static string DeclaredHandle(PlannedAction action)
        {
            if (!IsSpawnProducer(action)) return "";
            return (action.ArgString("handle") ?? "").Trim();
        }

        /// <summary>校验阶段可以解析已通过校验的生成动作，并用 null 表示对象还未实际生成。</summary>
        internal static bool TryResolveReference(ActionContext context, string name, bool requirePawn,
            out Thing thing, out string error)
        {
            thing = null;
            error = null;

            if (string.IsNullOrEmpty(name))
            {
                error = Loc.Pick("缺少句柄名，请填写 handle:名字",
                    "Missing handle name; use handle:<name>");
                return false;
            }

            // 同名的生成动作会覆盖原登记，校验也应优先使用刚通过校验的声明。
            if (context != null && context.Validating && context.PlannedHandles != null &&
                context.PlannedHandles.TryGetValue(name, out string producer))
            {
                if (!requirePawn || producer == "pawn.spawn") return true;
                error = Loc.Pick($"句柄 \"{name}\" 由 {producer} 声明，角色动作需要角色句柄"
                                 + DeclarationHint(name, requirePawn),
                    $"Handle \"{name}\" is declared by {producer}; this pawn action needs a pawn handle"
                    + DeclarationHint(name, requirePawn));
                return false;
            }

            // 校验期也必须回退到实例里已登记的句柄：choice_selected / timer 这些
            // 后续阶段引用前一阶段生成的角色时，priorHandles 只覆盖"预演"这一条路，
            // 真正执行时靠的是 instance.handles。
            HandleRecord record = FindRecord(context, name);
            if (record == null)
            {
                if (context != null && context.Validating)
                {
                    // 校验期没找到时，把"整个阶段声明了哪些句柄"写进错误本身：
                    // 这样日志不用再猜是漏了生成动作，还是名字写错了。
                    error = MissingHandleMessage(context, name, requirePawn);
                }
                else
                {
                    error = Loc.Pick($"句柄 \"{name}\" 在本动作前没有可用的声明"
                                     + RegisteredHandles(context) + DeclarationHint(name, requirePawn),
                        $"Handle \"{name}\" is not declared before this action"
                        + RegisteredHandles(context) + DeclarationHint(name, requirePawn));
                }

                return false;
            }

            thing = FindById(record.thingId, context.Map);
            if (thing == null)
            {
                error = Loc.Pick($"句柄 \"{name}\" 已登记，当前地图上找不到该对象（可能已离开或被销毁）",
                    $"Handle \"{name}\" is registered, but that object is not on this map"
                    + " (it may have left or been destroyed)");
                return false;
            }

            if (requirePawn && !(thing is Pawn))
            {
                error = Loc.Pick($"句柄 \"{name}\" 指向的对象类型为 {thing.GetType().Name}，角色动作需要角色句柄"
                                 + DeclarationHint(name, requirePawn),
                    $"Handle \"{name}\" points to a {thing.GetType().Name}; this pawn action needs a pawn handle"
                    + DeclarationHint(name, requirePawn));
                return false;
            }

            return true;
        }

        /// <summary>
        /// 校验期找不到句柄时的可读错误：先判断大小写/顺序，再列出本阶段所有声明。
        /// 目的：从日志一眼区分"漏了生成动作"与"生成时用了另一个名字"。
        /// </summary>
        private static string MissingHandleMessage(ActionContext context, string name, bool requirePawn)
        {
            Dictionary<string, string> before = context != null ? context.PlannedHandles : null;
            Dictionary<string, PlannedHandleInfo> all = context != null ? context.PlanHandleIndex : null;

            string wrongCase = FindCaseMismatch(before, all, name);
            if (wrongCase != null)
            {
                return Loc.Pick($"句柄 \"{name}\" 在本动作前没有可用的声明；本阶段声明的是 \"{wrongCase}\""
                                + " —— 句柄区分大小写，请把动作里的名字改成完全一致",
                    $"Handle \"{name}\" is not declared before this action; this stage declares \"{wrongCase}\""
                    + " — handle names are case-sensitive, so make the action use that exact name");
            }

            if (all != null && all.TryGetValue(name, out PlannedHandleInfo later) &&
                later.ActionNumber >= (context != null ? context.ValidatingActionNumber : 0))
            {
                int current = context != null ? context.ValidatingActionNumber : 0;
                return Loc.Pick($"句柄 \"{name}\" 要到第 {later.ActionNumber} 条 {later.ProducerType} 才声明，"
                                + $"而当前正在校验第 {current} 条动作；请把生成动作移到引用之前",
                    $"Handle \"{name}\" is only declared at action {later.ActionNumber} ({later.ProducerType}), "
                    + $"but action {current} is being validated now; move the spawn action before the reference");
            }

            var sb = new StringBuilder();
            sb.Append(Loc.Pick("句柄 \"", "Handle \"")).Append(name)
              .Append(Loc.Pick("\" 在本动作前没有可用的声明", "\" is not declared before this action"));

            string beforeText = DescribeBefore(before);
            string laterText = DescribeLater(before, all);
            string noHandleText = DescribeWithoutHandle(context != null ? context.SpawnActionsWithoutHandle : null);

            if (beforeText.Length > 0)
                sb.Append(Loc.Pick("；本动作前已声明：", "; declared before this action: ")).Append(beforeText);
            if (laterText.Length > 0)
                sb.Append(Loc.Pick("；本阶段稍后才声明：", "; declared later in this stage: ")).Append(laterText);
            if (noHandleText.Length > 0)
                sb.Append(Loc.Pick("；另有生成动作没写 args.handle：", "; spawn actions missing args.handle: "))
                  .Append(noHandleText);

            if (beforeText.Length == 0 && laterText.Length == 0 && noHandleText.Length == 0)
            {
                sb.Append(Loc.Pick("：本动作前没有任何句柄，本阶段也没有生成动作",
                    ": there are no handles before this action, and this stage has no spawn actions"));
            }

            sb.Append(DeclarationHint(name, requirePawn));
            return sb.ToString();
        }

        private static string FindCaseMismatch(Dictionary<string, string> before,
            Dictionary<string, PlannedHandleInfo> all, string name)
        {
            if (before != null)
            {
                foreach (string key in before.Keys)
                {
                    if (!string.Equals(key, name, StringComparison.Ordinal) &&
                        string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return key;
                }
            }

            if (all != null)
            {
                foreach (string key in all.Keys)
                {
                    if (!string.Equals(key, name, StringComparison.Ordinal) &&
                        string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return key;
                }
            }

            return null;
        }

        private static string DescribeBefore(Dictionary<string, string> before)
        {
            if (before == null || before.Count == 0) return "";

            var names = new List<string>(before.Keys);
            names.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder();
            int shown = 0;
            for (int i = 0; i < names.Count && shown < 6; i++)
            {
                if (shown > 0) sb.Append(Loc.Pick("、", ", "));
                if (Loc.IsChinese)
                {
                    sb.Append(names[i]).Append("（").Append(before[names[i]]).Append('）');
                }
                else
                {
                    sb.Append(names[i]).Append(" (").Append(before[names[i]]).Append(')');
                }

                shown++;
            }

            if (names.Count > shown)
            {
                sb.Append(Loc.Pick(" 等 " + names.Count + " 个", " (" + names.Count + " total)"));
            }

            return sb.ToString();
        }

        private static string DescribeLater(Dictionary<string, string> before,
            Dictionary<string, PlannedHandleInfo> all)
        {
            if (all == null || all.Count == 0) return "";

            var names = new List<string>(all.Keys);
            names.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder();
            int shown = 0;
            for (int i = 0; i < names.Count && shown < 6; i++)
            {
                string key = names[i];
                if (before != null && before.ContainsKey(key)) continue;

                PlannedHandleInfo info = all[key];
                if (shown > 0) sb.Append(Loc.Pick("、", ", "));
                if (Loc.IsChinese)
                {
                    sb.Append(key).Append("（第 ").Append(info.ActionNumber).Append(" 条 ")
                      .Append(info.ProducerType).Append('）');
                }
                else
                {
                    sb.Append(key).Append(" (action ").Append(info.ActionNumber).Append(", ")
                      .Append(info.ProducerType).Append(')');
                }

                shown++;
            }

            return sb.ToString();
        }

        private static string DescribeWithoutHandle(List<PlannedHandleInfo> withoutHandle)
        {
            if (withoutHandle == null || withoutHandle.Count == 0) return "";

            var sb = new StringBuilder();
            int shown = 0;
            for (int i = 0; i < withoutHandle.Count && shown < 6; i++)
            {
                PlannedHandleInfo info = withoutHandle[i];
                if (shown > 0) sb.Append(Loc.Pick("、", ", "));
                if (Loc.IsChinese)
                {
                    sb.Append("第 ").Append(info.ActionNumber).Append(" 条 ").Append(info.ProducerType);
                }
                else
                {
                    sb.Append("action ").Append(info.ActionNumber).Append(' ').Append(info.ProducerType);
                }

                shown++;
            }

            if (withoutHandle.Count > shown)
            {
                sb.Append(Loc.Pick(" 等 " + withoutHandle.Count + " 个", " (" + withoutHandle.Count + " total)"));
            }
            return sb.ToString();
        }

        /// <summary>执行期找不到声明时，列出本事件已登记过的句柄名。</summary>
        private static string RegisteredHandles(ActionContext context)
        {
            if (context == null || context.Instance == null ||
                context.Instance.handles == null || context.Instance.handles.Count == 0) return "";

            var names = new List<string>();
            for (int i = 0; i < context.Instance.handles.Count; i++)
            {
                string handle = context.Instance.handles[i] != null ? context.Instance.handles[i].name : null;
                if (!string.IsNullOrEmpty(handle)) names.Add(handle);
            }

            if (names.Count == 0) return "";
            names.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder(Loc.Pick("；本事件已声明的句柄：", "; handles already declared in this event: "));
            for (int i = 0; i < names.Count && i < 6; i++)
            {
                if (i > 0) sb.Append(Loc.Pick("、", ", "));
                sb.Append(names[i]);
            }

            if (names.Count > 6)
            {
                sb.Append(Loc.Pick(" 等 " + names.Count + " 个", " (" + names.Count + " total)"));
            }

            return sb.ToString();
        }

        private static string DeclarationHint(string name, bool requirePawn) => requirePawn
            ? Loc.Pick(
                $"。请在本动作前用 pawn.spawn{{handle=\"{name}\"}} 声明角色，或引用前置阶段已生成的角色句柄",
                $". Declare a pawn before this action with pawn.spawn{{handle=\"{name}\"}}, "
                + "or reference a pawn handle created in an earlier stage")
            : Loc.Pick(
                $"。请在本动作前用生成动作的 args.handle=\"{name}\" 声明句柄，或引用前置阶段已生成的对象",
                $". Declare the handle before this action with args.handle=\"{name}\" on a spawn action, "
                + "or reference an object created in an earlier stage");

        /// <summary>按 ThingID 在地图上找（先找物品，再找 Pawn）。找不到返回 null。</summary>
        public static Thing FindById(string thingId, Map map)
        {
            if (string.IsNullOrEmpty(thingId) || map == null) return null;

            if (map.listerThings != null && map.listerThings.AllThings != null)
            {
                IReadOnlyList<Thing> things = map.listerThings.AllThings;
                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i] != null && things[i].ThingID == thingId) return things[i];
                }
            }

            if (map.mapPawns != null && map.mapPawns.AllPawnsSpawned != null)
            {
                IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    if (pawns[i] != null && pawns[i].ThingID == thingId) return pawns[i];
                }
            }

            return null;
        }
    }

    /// <summary>
    /// 动作预算。按**事件实例**累计，防止一段脚本把地图塞满或把殖民地炸平。
    /// 数值刻意保守：脚本不是用来做系统级批量操作的。
    /// </summary>
    public static class ActionBudget
    {
        public const int MaxSpawnPerInstance = 40;     // 物品 / Pawn / 尸体 / 空投总量
        public const int MaxDangerPerInstance = 3;     // 爆炸 / 点火 / 杀人 / 崩溃 / 平台删除
        public const int MaxPawnEditPerInstance = 8;   // 加状态 / 治疗 / 换装 / 改技能 / 改派系 / 特质
        public const int MaxSocialPerInstance = 6;     // 想法 / 心情 / 灵感 / 关系
        public const int MaxWorldPerInstance = 2;      // 派系 / 研究 / 任务（世界层）

        public enum Pool
        {
            Spawn = 0,
            Danger = 1,
            PawnEdit = 2,
            Social = 3,
            World = 4
        }

        internal const int PoolCount = (int)Pool.World + 1;

        public static bool TryPlan(ActionContext context, Pool pool, int amount, out string error) =>
            TryApply(context, pool, amount, context.PlannedBudget, false, out error);

        public static bool TryConsume(ActionContext context, Pool pool, int amount, out string error) =>
            TryApply(context, pool, amount, null, true, out error);

        public static bool TryPlanCorpse(ActionContext context, out string error) =>
            TryPlan(context, Pool.Spawn, 1, out error) && TryPlan(context, Pool.Danger, 1, out error);

        public static bool TryConsumeCorpse(ActionContext context, out string error)
        {
            if (!TryApply(context, Pool.Spawn, 1, null, false, out error) ||
                !TryConsume(context, Pool.Danger, 1, out error))
                return false;

            context.Instance.spentSpawn++;
            return true;
        }

        private static bool TryApply(ActionContext context, Pool pool, int amount, int[] planned,
            bool consume, out string error)
        {
            error = null;
            EventInstanceRecord instance = context.Instance;
            if (instance == null)
            {
                error = Loc.Pick("缺少事件实例，无法校验预算", "No event instance; cannot check the action budget");
                return false;
            }

            switch (pool)
            {
                case Pool.Spawn:
                    return Apply(ref instance.spentSpawn, MaxSpawnPerInstance, "生成", "spawn",
                        pool, amount, planned, consume, out error);
                case Pool.Danger:
                    return Apply(ref instance.spentDanger, MaxDangerPerInstance, "危险", "danger",
                        pool, amount, planned, consume, out error);
                case Pool.PawnEdit:
                    return Apply(ref instance.spentPawnEdit, MaxPawnEditPerInstance, "角色编辑", "pawn edit",
                        pool, amount, planned, consume, out error);
                case Pool.Social:
                    return Apply(ref instance.spentSocial, MaxSocialPerInstance, "社交", "social",
                        pool, amount, planned, consume, out error);
                case Pool.World:
                    return Apply(ref instance.spentWorld, MaxWorldPerInstance, "世界", "world",
                        pool, amount, planned, consume, out error);
                default:
                    error = Loc.Pick("未知的预算池", "Unknown budget pool");
                    return false;
            }
        }

        private static bool Apply(ref int spent, int cap, string labelZh, string labelEn, Pool pool, int amount,
            int[] planned, bool consume, out string error)
        {
            error = null;
            int reserved = planned == null ? 0 : planned[(int)pool];
            if (amount <= 0 || (long)spent + reserved + amount > cap)
            {
                error = Loc.Pick(
                    labelZh + "预算不足（已用 " + spent + "，本阶段计划 " + reserved
                    + "，本动作需要 " + amount + "，上限 " + cap + "）",
                    labelEn + " budget exceeded (used " + spent + ", planned this stage " + reserved
                    + ", this action needs " + amount + ", cap " + cap + ")");
                return false;
            }

            if (consume) spent += amount;
            if (planned != null) planned[(int)pool] += amount;
            return true;
        }
    }
}
