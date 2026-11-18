using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace Stellaris.AI
{
    /// <summary>动作执行时的上下文。全部是主线程对象，禁止传给 Lua。</summary>
    public sealed class ActionContext
    {
        public Map Map;
        public NarratorState State;
        public EventDefinitionRecord Definition;
        public EventInstanceRecord Instance;
        public int Now;

        /// <summary>校验时可引用前置阶段预演通过或本阶段前面动作已校验的句柄。</summary>
        public bool Validating;

        internal Dictionary<string, string> PlannedHandles;
        internal int[] PlannedBudget;

        /// <summary>本阶段所有带 handle 的生成动作（含尚未轮到的），仅用于校验期诊断。</summary>
        internal Dictionary<string, PlannedHandleInfo> PlanHandleIndex;

        /// <summary>本阶段没写 args.handle 的生成动作，仅用于校验期诊断。</summary>
        internal List<PlannedHandleInfo> SpawnActionsWithoutHandle;

        /// <summary>校验器当前正在检查第几条动作（1 起），仅用于校验期诊断。</summary>
        internal int ValidatingActionNumber;

        public bool TryResolve(string handle, out Thing thing) =>
            HandleRegistry.TryResolve(this, handle, out thing);

        public void RegisterHandle(string handle, Thing thing) =>
            HandleRegistry.Register(this, handle, thing);
    }

    public enum ActionPermission
    {
        /// <summary>P0：只读 / 叙事（信件、选项、记忆、张力）。</summary>
        Narrative = 0,

        /// <summary>P1：有界生成（事件、物品、Pawn）。</summary>
        BoundedSpawn = 1,

        /// <summary>P2：重大后果（大面积破坏、永久关系变更）。</summary>
        Major = 2
    }

    /// <summary>
    /// 动作分组。剧情关键词决定提示词展示范围；玩家的分组开关也约束运行时。
    /// </summary>
    public enum ActionCategory
    {
        Narrative = 0,
        Spawn = 1,
        Map = 2,
        Pawn = 3,
        Social = 4,
        Scene = 5,
        Interactive = 6,
        World = 7
    }

    [Flags]
    public enum ActionCategoryMask
    {
        None = 0,
        Narrative = 1,
        Spawn = 2,
        Map = 4,
        Pawn = 8,
        Social = 16,
        Scene = 32,
        Interactive = 64,
        World = 128,
        All = 255
    }

    /// <summary>
    /// 目标限制。决定"这个动作能不能指向玩家的殖民者"。
    /// 写进 C# 的 Validate —— 提示词只是建议，这里才是墙。
    /// </summary>
    public enum TargetPolicy
    {
        /// <summary>正向动作：对任何人都是好事，玩家殖民者也允许（治疗、灵感）。</summary>
        Positive = 0,

        /// <summary>中性动作：玩家殖民者默认允许；属于"心理层"的还受设置开关约束。</summary>
        Neutral = 1,

        /// <summary>只允许指向非玩家派系的角色（特质、关系、改技能、加入）。</summary>
        NonPlayerOnly = 2,

        /// <summary>
        /// 危险动作（伤害 / 击杀 / 崩溃）。
        /// 默认只允许句柄；玩家把「AI 对你的殖民者的影响」设为「全部」后，也可以指向殖民者。
        /// </summary>
        Dangerous = 3
    }

    public interface IGameActionHandler
    {
        string OpType { get; }

        ActionPermission Permission { get; }

        /// <summary>提示词分组（见 <see cref="ActionCategory"/>）。</summary>
        ActionCategory Category { get; }

        /// <summary>能不能指向玩家殖民者（见 <see cref="TargetPolicy"/>）。</summary>
        TargetPolicy Policy { get; }

        /// <summary>
        /// 给模型看的规格：参数名、取值范围、语义。
        /// 这段文字会被拼进编剧提示词——**和校验逻辑写在同一个类里**，
        /// 所以改参数时不可能忘记同步手册（手册就是从校验器所在类型上取的）。
        /// </summary>
        string Describe();

        /// <summary>
        /// 纯校验：不产生任何副作用。返回 false 时整阶段作废（不会执行任何一条动作）。
        /// </summary>
        bool Validate(PlannedAction action, ActionContext context, out string error);

        /// <summary>执行动作。目标或游戏接口拒绝执行时，返回 false 和错误原因。</summary>
        bool TryExecute(PlannedAction action, ActionContext context, out string error);
    }

    /// <summary>
    /// 动作白名单。Lua 只能提出这里注册过的 type，权限与参数范围由 C# 硬编码，
    /// 脚本无法自称高权限。未注册的 type 一律拒绝。
    /// </summary>
    public static class ActionRegistry
    {
        private static readonly Dictionary<string, IGameActionHandler> Handlers =
            new Dictionary<string, IGameActionHandler>();

        public static void Register(IGameActionHandler handler)
        {
            if (handler == null || string.IsNullOrEmpty(handler.OpType)) return;
            Handlers[handler.OpType] = handler;
        }

        public static IReadOnlyDictionary<string, IGameActionHandler> All => Handlers;

        public static ActionCategoryMask MaskOf(ActionCategory category) =>
            (ActionCategoryMask)(1 << (int)category);

        public static int CountIn(ActionCategoryMask mask)
        {
            int n = 0;
            foreach (var pair in Handlers)
            {
                if ((mask & MaskOf(pair.Value.Category)) != 0) n++;
            }

            return n;
        }

        /// <summary>
        /// 动作手册：直接由已注册的 handler 生成，供编剧提示词使用。
        /// 手册与校验是同一份代码，天然不会漂移。
        /// </summary>
        public static string BuildManual(ActionCategoryMask mask = ActionCategoryMask.All)
        {
            var sb = new StringBuilder(3072);
            foreach (var pair in Handlers)
            {
                if ((mask & MaskOf(pair.Value.Category)) == 0) continue;
                sb.Append("  ").Append(pair.Value.Describe()).Append('\n');
            }

            return sb.ToString();
        }

        public static bool TryGet(string opType, out IGameActionHandler handler)
        {
            handler = null;
            return !string.IsNullOrEmpty(opType) && Handlers.TryGetValue(opType, out handler);
        }

        /// <summary>注册内置动作。由 Mod 构造函数调用一次。</summary>
        public static void RegisterBuiltins()
        {
            Register(new StorySendLetterHandler());
            Register(new StoryShowChoicesHandler());
            Register(new StoryRememberHandler());
            Register(new IncidentFireHandler());
            Register(new ThingSpawnHandler());
            Register(new PawnSpawnHandler());

            // B1 叙事与提示
            Register(new StoryMessageHandler());
            Register(new StoryDialogHandler());
            Register(new StoryHighlightHandler());
            Register(new StoryRewardHandler());

            // B2 地图与氛围
            Register(new MapWeatherHandler());
            Register(new MapConditionHandler());
            Register(new MapFireHandler());
            Register(new MapDamageHandler());
            Register(new MapDropPodHandler());
            Register(new MapCorpseHandler());
            Register(new MapPlantHandler());

            // B3 角色（全部走句柄）
            Register(new PawnHurtHandler());
            Register(new PawnHealHandler());
            Register(new PawnKillHandler());
            Register(new PawnGearHandler());
            Register(new PawnSetSkillHandler());
            Register(new PawnJoinHandler());

            // B4 任务 / 派系 / 研究 / 世界
            Register(new FactionGoodwillHandler());
            Register(new ResearchGrantHandler());
            Register(new QuestStartHandler());

            // P4 · 心理与关系（人心）
            Register(new PawnThoughtHandler());
            Register(new PawnMoodHandler());
            Register(new PawnInspirationHandler());
            Register(new PawnTraitHandler());
            Register(new PawnRelationHandler());
            Register(new PawnMentalHandler());

            // P4 · 场景与破坏（现场）
            Register(new MapBuildingHandler());
            Register(new MapTerrainHandler());
            Register(new MapRoofHandler());
            Register(new MapFilthHandler());
            Register(new MapAnimalHandler());
            Register(new MapRemoveHandler());
            Register(new MapCollapseHandler());

            // P4 · 交互与呈现
            Register(new StoryObjectiveHandler());
            Register(new StoryFocusHandler());
            Register(new StoryChoiceLetterHandler());
        }

        /// <summary>
        /// 全量校验。任一动作非法 → 整个阶段作废，绝不"执行一半"。
        /// </summary>
        public static bool ValidateAll(IEnumerable<PlannedAction> actions, ActionContext context, out string error) =>
            ValidateAll(actions, context, null, out _, out error);

        internal static bool ValidateAll(IEnumerable<PlannedAction> actions, ActionContext context,
            Dictionary<string, string> priorHandles, out Dictionary<string, string> validatedHandles, out string error)
        {
            error = null;
            validatedHandles = null;
            if (actions == null)
            {
                validatedHandles = priorHandles;
                return true;
            }

            // 先物化：校验途中要按"整阶段"扫描一遍句柄声明，不能把一个只能枚举一次的
            // 迭代器消耗掉。
            var list = actions as List<PlannedAction> ?? new List<PlannedAction>(actions);

            bool wasValidating = context.Validating;
            int[] previousBudget = context.PlannedBudget;
            var previousHandles = context.PlannedHandles;
            var previousIndex = context.PlanHandleIndex;
            var previousWithoutHandle = context.SpawnActionsWithoutHandle;
            int previousActionNumber = context.ValidatingActionNumber;
            context.Validating = true;
            context.PlannedHandles = priorHandles == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(priorHandles, StringComparer.Ordinal);
            context.PlanHandleIndex = HandleRegistry.IndexPlanHandles(list, out List<PlannedHandleInfo> withoutHandle);
            context.SpawnActionsWithoutHandle = withoutHandle;
            context.PlannedBudget = new int[ActionBudget.PoolCount];
            try
            {
                if (!ValidateActions(list, context, out error)) return false;
                validatedHandles = context.PlannedHandles;
                return true;
            }
            finally
            {
                context.PlannedBudget = previousBudget;
                context.PlannedHandles = previousHandles;
                context.PlanHandleIndex = previousIndex;
                context.SpawnActionsWithoutHandle = previousWithoutHandle;
                context.ValidatingActionNumber = previousActionNumber;
                context.Validating = wasValidating;
            }
        }

        private static bool ValidateActions(List<PlannedAction> actions, ActionContext context, out string error)
        {
            error = null;
            for (int i = 0; i < actions.Count; i++)
            {
                int count = i + 1;
                PlannedAction action = actions[i];
                if (count > 32)
                {
                    error = "单阶段动作数超过 32 条上限";
                    return false;
                }

                if (action == null) continue;

                if (!TryGet(action.type, out IGameActionHandler handler))
                {
                    error = "未注册的动作类型：" + action.type;
                    return false;
                }

                if (!ActionSelector.IsEnabled(handler.Category, AINarratorMod.Settings))
                {
                    error = action.type + " 所属动作组已在设置中关闭";
                    return false;
                }

                context.ValidatingActionNumber = count;
                if (!handler.Validate(action, context, out string itemError))
                {
                    string op = string.IsNullOrEmpty(action.opId) ? "" : "（opId=\"" + action.opId + "\"）";
                    error = $"第 {count} 条动作{op} {action.type} 校验失败：{itemError}";
                    return false;
                }

                HandleRegistry.RecordPlannedCreation(context, action);
            }

            return true;
        }
    }
}