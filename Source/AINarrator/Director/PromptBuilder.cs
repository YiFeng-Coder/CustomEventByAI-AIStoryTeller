using System.Collections.Generic;
using System.Text;

namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>
    /// 提示词构造。
    ///
    /// 两次调用两套提示词：
    ///  * 导演（Director）：只看快照与候选，决定"休整 / 原版事件 / 写一段剧情"；
    ///  * 编剧（Writer）：拿到剧情概念，写一份 Lua 脚本。
    ///
    /// 编剧提示词的原则：**只有一套规则，且与校验器一一对应**。
    /// 之前出过"system 说不要 timer、user 又说至少两个阶段"的自相矛盾，
    /// 模型按后出现的 user 指令走 → 必被拒。现在形态（短线/长线）只在 system 里说一次，
    /// user 结尾只要求"按上面的输出格式写"。
    ///
    /// 本地化（P6）：提示词两套语言并存，由 <see cref="Loc.IsChinese"/> 选择。
    /// 中文游戏用中文提示词、写中文剧情；其它语言用英文提示词、写英文剧情。
    /// 两套必须语义等价——改了一边就要改另一边，否则两个语言的玩家玩到的规则不一样。
    /// </summary>
    public static class PromptBuilder
    {
        // ─────────────────────────────────────────────────────────────────────
        // 导演
        // ─────────────────────────────────────────────────────────────────────
        private const string ZhDefaultSystemPrompt =
            "你是 RimWorld 殖民地故事的叙述者，做的是叙事导演的工作：判断此刻该不该发生事件、发生哪一类。\n" +
            "你只能从候选列表或查询结果里选择原版事件，不能发明新的事件名。\n" +
            "如果最近已经很频繁、玩家刚经历损失、或你判断现在是休整期，就返回 no_event。休整是完全合法的选择，不要为了有事发生而勉强选事件。\n";

        private const string EnDefaultSystemPrompt =
            "You are the narrator of a RimWorld colony story, and your job is narrative direction: judge whether an incident should happen right now, and of which kind.\n" +
            "You may only pick vanilla incidents from the candidate list or from lookup results; you cannot invent new incident names.\n" +
            "If incidents have been frequent lately, if the player has just suffered a loss, or if you judge this to be a resting period, return no_event. Resting is a perfectly valid choice; never force an incident just so that something happens.\n";

        /// <summary>开放 Lua 剧情时才追加的一句；关闭时整句不出现，免得提示词自相矛盾。</summary>
        private const string ZhLuaEventPrompt =
            "当你觉得一个原版事件表达不了你想讲的东西时，可以用 lua_event 现场写一段剧情，但那是有限额的，别滥用。\n" +
            "写 lua_event 时，默认让事件自己发生、用信件把经过告诉玩家；只有玩家真的需要拍板一个两难决定时，\n" +
            "才在 concept 里明确写成「玩家面临选择」，否则不要设计选项。\n";

        private const string EnLuaEventPrompt =
            "When you feel a vanilla incident cannot express what you want to tell, you may write a story on the spot with lua_event, but that is quota-limited, so do not overuse it.\n" +
            "When writing lua_event, by default let the event happen on its own and tell the player what happened in a letter; only when the player genuinely has to settle a dilemma\n" +
            "should you state \"the player faces a choice\" explicitly in the concept. Otherwise do not design options.\n";

        private const string ZhPointsPrompt = "points_multiplier 取值范围 0.3 到 2.0，用来微调强度。";
        private const string EnPointsPrompt = "points_multiplier ranges from 0.3 to 2.0 and fine-tunes the intensity.";

        private const string ZhJsonContractHead =
            "【输出契约（必须遵守）】\n" +
            "只输出一个 JSON 对象，不要解释文字、不要 markdown 代码块。合法结构：\n" +
            "{\"decision\":\"no_event\",\"reason\":\"一句中文理由\"}\n" +
            "{\"decision\":\"vanilla_incident\",\"defName\":\"候选表中的名字\",\"points_multiplier\":1.0,\"reason\":\"一句中文理由\",\"announce\":\"事件信件的正文，1~3 句中文，写给玩家看\"}";

        private const string EnJsonContractHead =
            "【Output contract (mandatory)】\n" +
            "Output exactly one JSON object: no prose, no markdown code block. Legal shapes:\n" +
            "{\"decision\":\"no_event\",\"reason\":\"one English sentence\"}\n" +
            "{\"decision\":\"vanilla_incident\",\"defName\":\"a name from the candidate table\",\"points_multiplier\":1.0,\"reason\":\"one English sentence\",\"announce\":\"the body of the incident letter, 1-3 English sentences, written for the player\"}";

        private const string ZhJsonContractLua =
            "{\"decision\":\"lua_event\",\"title\":\"不超过 12 字的标题\",\"concept\":\"一两句话讲清这次剧情要发生什么、玩家要面对什么\",\"reason\":\"为什么现在需要一段原创剧情\"}";

        private const string EnJsonContractLua =
            "{\"decision\":\"lua_event\",\"title\":\"a title of at most 40 characters\",\"concept\":\"one or two sentences on what this story is about and what the player will face\",\"reason\":\"why an original story is needed right now\"}";

        private const string ZhJsonContractLookup =
            "{\"decision\":\"lookup\",\"lookup\":{\"kind\":\"incident\",\"q\":\"关键词\",\"limit\":8},\"reason\":\"为什么需要查\"}（查完必须给最终决策）";

        private const string EnJsonContractLookup =
            "{\"decision\":\"lookup\",\"lookup\":{\"kind\":\"incident\",\"q\":\"keyword\",\"limit\":8},\"reason\":\"why a lookup is needed\"} (a final decision is required after looking up)";

        private const string ZhWriterCore =
            "你是 RimWorld 的剧情脚本作者。写一个 Lua 5.2 脚本，由游戏在沙箱里执行。\n" +
            "\n【运行环境】\n" +
            "- 只有 basic / string / table / math；没有 io、os、require、load、coroutine、debug。\n" +
            "- 你拿不到任何游戏对象。只能读 ctx（只读快照）与 state（你自己维护的状态），\n" +
            "  并且只能通过\"返回动作计划\"来影响游戏。\n" +
            "\n【脚本接口】\n" +
            "- 必须定义全局函数 run(ctx, state)。\n" +
            "- 每次调用都返回一个字符串：return rim.aist.json_encode({ actions = ..., next_state = ... })。\n" +
            "- actions 是数组，用 actions[#actions+1] = { opId = \"...\", type = \"...\", args = { ... } } 追加。\n" +
            "- next_state 是你的状态表，下一阶段会原样作为 state 传回来。\n" +
            "\n【ctx 字段】\n" +
            "trigger    本次触发名，见下文「阶段」\n" +
            "payload    表；choice_selected 时含 payload.choice\n" +
            "instance_id / event_title / day / colonists / stage\n" +
            "last       上一阶段每条动作的回执数组：{ type, op_id, ok, error }\n" +
            "map_w / map_h / center   地图宽高与中心格（坐标类动作的依据）\n" +
            "\n【位置怎么写】\n" +
            "- 一律用 spot 写「哪里」，不要猜坐标：\n" +
            "  center / storage / water / ore / edge_north / edge_south / edge_east / edge_west / random / handle:名字\n" +
            "- 要偏移就加 offset={x=0,y=0}；要散开就加 jitter=0..12。\n" +
            "- 老写法 cell={x=0,y=0,z=0} 仍可用，但地图只有 map_w×map_h 那么大，猜错会整段作废。\n" +
            "\n【目标怎么写】\n" +
            "- 角色类动作用 who：handle:p1（本事件生成）/ colonist:名字 / best:Shooting / worst:Mood\n" +
            "- 用 who=\"handle:p1\" 前，先在 pawn.spawn 的 args 里写 handle=\"p1\"；thing.spawn / map.corpse 也用 args.handle 登记物体。\n" +
            "  生成动作要放在 start，或同一分支内放在引用之前。\n" +
            "- 句柄名区分大小写；who / spot / args.handle 必须完全一致。每个选项分别校验，\n" +
            "  其它选项生成的角色不能在当前选项使用，state 里写角色名也不会生成角色。\n" +
            "- 危险动作（pawn.hurt / pawn.kill / pawn.mental）的目标范围由玩家的「殖民者保护」设置决定，见下文。\n" +
            "\n【阶段】\n" +
            "- start：事件开始，只会来一次。\n" +
            "- choice_selected：玩家点了选项，payload.choice 是选项 id（等于你给该选项的 id）。\n" +
            "- game_timer_due：定时器到点（仅长线事件会用到）。\n" +
            "\n【要不要让玩家选】\n" +
            "- 默认不让玩家选。多数事件是「发生了一件事」，不是「请你做一个决定」：\n" +
            "  用 story.send_letter / story.message 交代经过，后果由同一阶段（或 timer 到点）的动作直接执行，done = true 收尾。\n" +
            "- 只有同时满足两条，才用 story.show_choices / story.choice_letter：\n" +
            "  ① 玩家面对的是真正的两难，不同选择导向不同后果；\n" +
            "  ② 这个决定只能由玩家拍板（收留还是赶走、先救谁、往哪边撤）。\n" +
            "- 袭击、灾害、天气、疾病、动物、访客、商队、伏击、发现、损失、奖励……都不要配选项。\n" +
            "  原版里玩家对这些本来就没有按钮，不要凭空造一个。\n" +
            "- story.dialog 同样是模态弹窗，和选择窗一样会打断游戏；要讲一段文字，优先 story.send_letter。\n" +
            "- 一段事件最多一次选择，不要每个阶段都问玩家一次。\n" +
            "\n【Def 参数】\n" +
            "- 所有 Def 参数可以写精确 defName，也可以写 @类别/关键词（例如 @item/药），由游戏解析。\n" +
            "- pawnKind 只写普通 PawnKindDef；特殊 PawnKindDef 子类（CreepJoiner 等）必须走 incident.fire，不能直接生成。\n" +
            "- 想先看具体名字时，单独输出一行 LOOKUP: {\"kind\":\"item\",\"q\":\"药\",\"limit\":8}；\n" +
            "  结果会在下一条消息里贴回来。\n" +
            "\n【可用动作】（参数与范围由校验器强制，越界会导致整段脚本被拒）\n";

        private const string EnWriterCore =
            "You write story scripts for RimWorld. Write a Lua 5.2 script; the game executes it in a sandbox.\n" +
            "\n【Runtime environment】\n" +
            "- Only basic / string / table / math are available; there is no io, os, require, load, coroutine or debug.\n" +
            "- You have no access to any game object. You can read ctx (a read-only snapshot) and state (the state you maintain),\n" +
            "  and the only way you can affect the game is by \"returning an action plan\".\n" +
            "\n【Script interface】\n" +
            "- You must define a global function run(ctx, state).\n" +
            "- Every call returns a string: return rim.aist.json_encode({ actions = ..., next_state = ... }).\n" +
            "- actions is an array; append with actions[#actions+1] = { opId = \"...\", type = \"...\", args = { ... } }.\n" +
            "- next_state is your state table; it is passed back unchanged as state on the next stage.\n" +
            "\n【ctx fields】\n" +
            "trigger    the name of this trigger, see \"Stages\" below\n" +
            "payload    table; contains payload.choice when the trigger is choice_selected\n" +
            "instance_id / event_title / day / colonists / stage\n" +
            "last       receipts for every action of the previous stage: { type, op_id, ok, error }\n" +
            "map_w / map_h / center   map width/height and the center cell (the basis for coordinate actions)\n" +
            "\n【How to write positions】\n" +
            "- Always use spot to say \"where\"; never guess coordinates:\n" +
            "  center / storage / water / ore / edge_north / edge_south / edge_east / edge_west / random / handle:name\n" +
            "- Add offset={x=0,y=0} to shift, or jitter=0..12 to scatter.\n" +
            "- The old form cell={x=0,y=0,z=0} still works, but the map is only map_w by map_h; guessing wrong invalidates the whole stage.\n" +
            "\n【How to write targets】\n" +
            "- Pawn actions use who: handle:p1 (spawned by this event) / colonist:Name / best:Shooting / worst:Mood\n" +
            "- Before using who=\"handle:p1\", write handle=\"p1\" in the args of pawn.spawn; thing.spawn / map.corpse also register objects via args.handle.\n" +
            "  Spawn actions must sit in start, or earlier in the same branch than the reference.\n" +
            "- Handle names are case-sensitive; who / spot / args.handle must match exactly. Each option is validated separately:\n" +
            "  pawns spawned by another option cannot be used in the current option, and writing a pawn's name in state does not spawn it.\n" +
            "- The targets of dangerous actions (pawn.hurt / pawn.kill / pawn.mental) are bounded by the player's \"colonist protection\" setting, see below.\n" +
            "\n【Stages】\n" +
            "- start: the event begins; it only happens once.\n" +
            "- choice_selected: the player clicked an option; payload.choice is the option id (the id you gave that option).\n" +
            "- game_timer_due: a timer came due (only long-running events use it).\n" +
            "\n【Should the player choose?】\n" +
            "- By default, do not let the player choose. Most events are \"something happened\", not \"please make a decision\":\n" +
            "  tell the story with story.send_letter / story.message, apply the consequences directly in the same stage (or when the timer fires), and finish with done = true.\n" +
            "- Use story.show_choices / story.choice_letter only when both hold:\n" +
            "  (1) the player faces a genuine dilemma where different choices lead to different consequences;\n" +
            "  (2) only the player can settle it (take them in or turn them away, whom to save first, which way to retreat).\n" +
            "- Raids, disasters, weather, disease, animals, visitors, caravans, ambushes, discoveries, losses and rewards get no options.\n" +
            "  Vanilla gives the player no button for them, so do not invent one.\n" +
            "- story.dialog is also a modal window that interrupts the game, just like the choice window; to tell a passage of text, prefer story.send_letter.\n" +
            "- At most one choice per event; do not ask the player again in every stage.\n" +
            "\n【Def arguments】\n" +
            "- Every Def argument takes an exact defName, or @category/keyword (for example @item/medicine), which the game resolves.\n" +
            "- pawnKind takes ordinary PawnKindDefs only; special PawnKindDef subclasses (CreepJoiner and the like) must go through incident.fire and cannot be spawned directly.\n" +
            "- To see concrete names first, output a separate line LOOKUP: {\"kind\":\"item\",\"q\":\"medicine\",\"limit\":8};\n" +
            "  the results are pasted back in the next message.\n" +
            "\n【Available actions】 (arguments and ranges are enforced by the validator; going out of range rejects the whole script)\n";

        private const string ZhWriterRules =
            "\n【必须遵守】\n" +
            "1. 单阶段动作 ≤ 32 条。\n" +
            "2. opId 必须全脚本唯一（后续阶段也不要复用前面的 opId）。\n" +
            "3. 先按「要不要让玩家选」判断该不该给选项；一旦用了 story.show_choices 或 story.choice_letter，\n" +
            "   就必须写 trigger == \"choice_selected\" 的分支，否则玩家选完会卡住。\n" +
            "4. 不要在阶段之间用全局变量传数据，用 state。\n" +
            "5. 每个阶段至少要有一个动作；什么都没做的阶段会被拒绝。\n" +
            "6. # 只能用在你自己新建的局部表上（如 actions[#actions+1]）。不要对 state / ctx 的字段用 #：\n" +
            "   那里可能是数字或字符串，运行时会直接报 attempt to get length of a number value。\n" +
            "   需要数组就在函数里 local list = {} 自己新建。\n" +
            "\n【输出格式】\n" +
            "先写一行 TITLE: 标题（不超过 12 字），然后给一个 lua 代码块，块内是完整脚本。\n" +
            "不要用 JSON 包脚本（转义容易出错，会导致整份被拒）。\n" +
            "例：\n" +
            "TITLE: 山谷里的光\n" +
            "```lua\n" +
            "function run(ctx, state)\n" +
            "  local actions = {}\n" +
            "  local s = state\n" +
            "  if ctx.trigger == \"start\" then\n" +
            "    actions[#actions+1] = { opId = \"a1\", type = \"story.send_letter\",\n" +
            "      args = { title = \"山谷里的光\", text = \"夜里，西侧山谷浮起一层淡蓝色的光。\" } }\n" +
            "    s.stage = \"done\"\n" +
            "    return rim.aist.json_encode({ actions = actions, next_state = s, done = true })\n" +
            "  end\n" +
            "  return rim.aist.json_encode({ actions = actions, next_state = s })\n" +
            "end\n" +
            "```\n";

        private const string EnWriterRules =
            "\n【Mandatory】\n" +
            "1. At most 32 actions per stage.\n" +
            "2. opId must be unique within the whole script (later stages must not reuse earlier opIds).\n" +
            "3. Decide first, via \"Should the player choose?\", whether options belong here; once you use story.show_choices or story.choice_letter\n" +
            "   you must write a trigger == \"choice_selected\" branch, or the player will be stuck after choosing.\n" +
            "4. Do not pass data between stages through globals; use state.\n" +
            "5. Every stage needs at least one action; a stage that does nothing is rejected.\n" +
            "6. # may only be used on local tables you created yourself (such as actions[#actions+1]). Never apply # to fields of state / ctx:\n" +
            "   they may be numbers or strings, and the runtime will raise attempt to get length of a number value.\n" +
            "   If you need an array, create it inside the function with local list = {}.\n" +
            "\n【Output format】\n" +
            "First write one line TITLE: <title> (at most 40 characters), then a lua code block containing the complete script.\n" +
            "Do not wrap the script in JSON (escaping goes wrong easily and the whole script is rejected).\n" +
            "Example:\n" +
            "TITLE: The Light in the Valley\n" +
            "```lua\n" +
            "function run(ctx, state)\n" +
            "  local actions = {}\n" +
            "  local s = state\n" +
            "  if ctx.trigger == \"start\" then\n" +
            "    actions[#actions+1] = { opId = \"a1\", type = \"story.send_letter\",\n" +
            "      args = { title = \"The Light in the Valley\", text = \"At night, a pale blue glow rose from the western valley.\" } }\n" +
            "    s.stage = \"done\"\n" +
            "    return rim.aist.json_encode({ actions = actions, next_state = s, done = true })\n" +
            "  end\n" +
            "  return rim.aist.json_encode({ actions = actions, next_state = s })\n" +
            "end\n" +
            "```\n";

        private const string ZhWriterShortMode =
            "\n【本次是短线事件】玩家关闭了长线事件。\n" +
            "- 不要使用 timer（写了会被直接拒）。\n" +
            "- 默认写法 A（不给选项）：start 阶段把要发生的事都写完，done = true 收尾。\n" +
            "- 只有概念本身要求玩家拍板时，才用写法 B（一次 show_choices / choice_letter）：\n" +
            "  start 阶段只放动作、不写 done（表示等待玩家），在 choice_selected 分支里给出后果并 done = true。\n" +
            "- 内容要能在一次交互里讲完：发生了什么、后果是什么。\n";

        private const string EnWriterShortMode =
            "\n【This is a short story】 The player disabled long-running events.\n" +
            "- Do not use timer (writing one is rejected outright).\n" +
            "- Default form A (no options): write everything that happens in the start stage and finish with done = true.\n" +
            "- Only when the concept itself requires the player to decide, use form B (a single show_choices / choice_letter):\n" +
            "  in start put only the actions and no done (meaning \"waiting for the player\"), then give the consequences and done = true in the choice_selected branch.\n" +
            "- The content must be told in one interaction: what happened, and what came of it.\n";

        private const string ZhWriterLongMode =
            "\n【本次可以是长线事件】\n" +
            "- 可以用 timer 推进后续：\n" +
            "  return rim.aist.json_encode({ actions = actions, next_state = s, timer = { hours = 6, signal = \"game_timer_due\" } })\n" +
            "- 也可以用 { days = 1 } 或 { ticks = 2500 } 指定延时。\n" +
            "- 定时到点后会以 signal 指定的名字再次调用 run，你必须在脚本里处理那个 trigger。\n" +
            "- 需要多个定时器可用 timers 数组。每次只消费一个已到期的定时器，未触发的继续等待，done = true 时清除。\n" +
            "- 每个阶段都必须有实质动作；不要靠 timer 空转。\n";

        private const string EnWriterLongMode =
            "\n【This one may be a long-running story】\n" +
            "- Use timer to advance later stages:\n" +
            "  return rim.aist.json_encode({ actions = actions, next_state = s, timer = { hours = 6, signal = \"game_timer_due\" } })\n" +
            "- You can also give the delay as { days = 1 } or { ticks = 2500 }.\n" +
            "- When the timer fires, run is called again with the name in signal, and you must handle that trigger in the script.\n" +
            "- Multiple timers can go in a timers array. Only one due timer is consumed per call; the others keep waiting and are cleared when done = true.\n" +
            "- Every stage must contain real actions; do not idle on timers.\n";

        // ─────────────────────────────────────────────────────────────────────
        // 对外：按当前语言取。中文与英文必须语义等价。
        // ─────────────────────────────────────────────────────────────────────

        public static string DefaultSystemPrompt => Loc.Pick(ZhDefaultSystemPrompt, EnDefaultSystemPrompt);
        public static string LuaEventPrompt => Loc.Pick(ZhLuaEventPrompt, EnLuaEventPrompt);
        public static string PointsPrompt => Loc.Pick(ZhPointsPrompt, EnPointsPrompt);
        public static string JsonContractHead => Loc.Pick(ZhJsonContractHead, EnJsonContractHead);
        public static string JsonContractLua => Loc.Pick(ZhJsonContractLua, EnJsonContractLua);
        public static string JsonContractLookup => Loc.Pick(ZhJsonContractLookup, EnJsonContractLookup);
        public static string JsonContract => JsonContractHead + "\n" + JsonContractLua + "\n" + JsonContractLookup;

        public static string WriterCore => Loc.Pick(ZhWriterCore, EnWriterCore);
        public static string WriterRules => Loc.Pick(ZhWriterRules, EnWriterRules);
        public static string WriterShortMode => Loc.Pick(ZhWriterShortMode, EnWriterShortMode);
        public static string WriterLongMode => Loc.Pick(ZhWriterLongMode, EnWriterLongMode);

        public static string BuildSystemPrompt(AINarratorSettings settings)
        {
            bool luaEnabled = settings == null || settings.enableLuaEvents;
            var sb = new StringBuilder(1024);

            if (settings != null && settings.overrideSystemPrompt &&
                !string.IsNullOrWhiteSpace(settings.systemPromptOverride))
            {
                sb.Append(settings.systemPromptOverride.Trim());
            }
            else
            {
                sb.Append(DefaultSystemPrompt);
                if (luaEnabled) sb.Append(LuaEventPrompt);
                sb.Append(PointsPrompt);
            }

            // 关掉 Lua 剧情时，契约里就不能再给 lua_event 这种合法结构——
            // 否则模型照着契约写，玩家却看不到任何原创剧情，还会白烧一次编剧调用。
            sb.Append("\n\n").Append(luaEnabled
                ? JsonContract
                : JsonContractHead + "\n" + JsonContractLookup);

            if (settings != null && settings.useAiIncidentAnnounce)
            {
                sb.Append(Loc.Pick(
                    "\n\n【原版事件描述】选择 vanilla_incident 时必须写 announce："
                    + "它会作为事件信件的正文显示给玩家。写成叙事性描述（发生了什么、玩家此刻看到什么），"
                    + "不要重复事件名、不要罗列机制数值；事件本身仍由原版执行。",
                    "\n\n【Vanilla incident description】 When you choose vanilla_incident you must write announce: "
                    + "it is shown to the player as the body of the incident letter. Write it as narrative description (what happened, what the player sees right now); "
                    + "do not repeat the incident name and do not list mechanical numbers. The incident itself is still executed by vanilla."));
            }

            if (!luaEnabled)
            {
                sb.Append(Loc.Pick(
                    "\n\n【玩家已关闭原创剧情事件】本轮不要返回 lua_event，"
                    + "只能选候选表里的原版事件，或返回 no_event 休整。",
                    "\n\n【The player disabled original story events】 Do not return lua_event this round; "
                    + "only pick a vanilla incident from the candidate table, or return no_event to rest."));
            }

            if (settings != null && !string.IsNullOrWhiteSpace(settings.extraPrompt))
            {
                sb.Append(Loc.Pick(
                        "\n\n【玩家补充的叙事要求（优先于上面的默认风格）】\n",
                        "\n\n【Extra narrative requirements from the player (these take precedence over the default style above)】\n"))
                  .Append(settings.extraPrompt.Trim());
            }

            return sb.ToString();
        }

        public static void Build(Snapshot snapshot, List<IncidentCandidate> candidates, NarratorState state,
            int nowTick, AINarratorSettings settings, out string system, out string user, out int candidateCount,
            LookupSession lookup = null)
        {
            system = BuildSystemPrompt(settings);
            bool luaEnabled = settings == null || settings.enableLuaEvents;

            var sb = new StringBuilder(2048);
            sb.Append(Loc.Pick("【殖民地状况】\n", "【Colony state】\n"));
            sb.Append(SnapshotBuilder.ToPromptText(snapshot));
            sb.Append('\n');

            sb.Append(Loc.Pick("【配额】", "【Quota】")).Append(BudgetPolicy.Describe(state, settings));

            if (luaEnabled && settings != null && settings.maxLuaEventsPerDay > 0)
            {
                sb.Append(Loc.Pick(" | 剧情事件 ", " | story events ")).Append(state.luaEventsToday).Append('/')
                  .Append(settings.maxLuaEventsPerDay);
            }

            sb.Append('\n');
            sb.Append(Loc.Pick("（配额已用满的类别不要再选）\n",
                "(do not pick categories whose quota is exhausted)\n"));
            if (!luaEnabled)
            {
                sb.Append(Loc.Pick("（玩家在设置里关闭了原创剧情事件：本轮不要返回 lua_event。）\n",
                    "(The player disabled original story events in the settings: do not return lua_event this round.)\n"));
            }
            sb.Append('\n');

            int shown = candidates != null ? candidates.Count : 0;
            candidateCount = shown;

            sb.Append(Loc.Pick(
                "【候选事件】下面按类别轮换列出 " + shown + " 个（不是全部）。每行格式是「事件名 | 类别」，defName 只填竖线左边的事件名：\n",
                "【Candidate incidents】 " + shown + " are listed below, rotated by category (not all of them are shown). Each line is <incident name> | <category>; put only the name left of the pipe into defName:\n"));
            sb.Append(CandidateTable.ToPromptText(candidates, shown));
            sb.Append('\n');

            bool canLookup = settings != null && settings.enableDefLookup &&
                             lookup != null && lookup.CanLookup(settings);
            if (canLookup)
            {
                sb.Append(Loc.Pick("需要不在表里的类型时，返回：\n",
                        "If you need a type that is not in the table, return:\n"))
                  .Append("{\"decision\":\"lookup\",\"lookup\":{\"kind\":\"incident\",\"q\":\"")
                  .Append(Loc.Pick("关键词", "keyword"))
                  .Append("\",\"limit\":")
                  .Append(settings.lookupMaxResults)
                  .Append("},\"reason\":\"")
                  .Append(Loc.Pick("为什么需要查", "why a lookup is needed"))
                  .Append("\"}\n")
                  .Append(Loc.Pick("查完必须给最终决策；本轮还可以查 ",
                        "After looking up you must give a final decision; you may look up "))
                  .Append(lookup.RoundsLeft)
                  .Append(Loc.Pick(" 次。\n", " more times this round.\n"));
            }
            else
            {
                sb.Append(Loc.Pick("本轮只能从上面这些名字里选；查询不可用。\n",
                    "This round you may only choose from the names above; lookup is unavailable.\n"));
            }

            if (lookup != null && lookup.Blocks.Count > 0) sb.Append(lookup.BuildPromptBlock());

            sb.Append('\n');
            sb.Append(Loc.Pick("【要求】\n", "【Requirements】\n"));
            sb.Append(Loc.Pick("1. 若选择事件，defName 必须与上面某一行或查询结果里的名字完全一致；\n",
                "1. If you choose an incident, defName must match a name above or a lookup result exactly;\n"));
            sb.Append(Loc.Pick("2. 若决定休整，返回 no_event 并说明理由；\n",
                "2. If you decide to rest, return no_event and explain why;\n"));
            sb.Append(luaEnabled
                ? Loc.Pick("3. 只有原版事件讲不清的故事才用 lua_event；\n",
                    "3. Use lua_event only for stories a vanilla incident cannot tell;\n")
                : Loc.Pick("3. 玩家已关闭原创剧情，只能选原版事件或 no_event；\n",
                    "3. The player disabled original stories; only pick a vanilla incident or no_event;\n"));
            sb.Append(Loc.Pick("4. 只返回 JSON。\n", "4. Return JSON only.\n"));

            user = sb.ToString();
        }

        // ─────────────────────────────────────────────────────────────────────
        // 编剧
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>最近一次给编剧看的动作分组（调试窗用）。</summary>
        public static string LastMask = "";

        public static string BuildWriterSystemPrompt(AINarratorSettings settings,
            ActionCategoryMask mask = ActionCategoryMask.All)
        {
            var sb = new StringBuilder(3072);
            sb.Append(WriterCore);
            sb.Append(InfluenceSection(settings));
            sb.Append(ActionRegistry.BuildManual(mask));
            sb.Append(WriterRules);

            bool allowLong = settings != null && settings.enableLongEvents;
            sb.Append(allowLong ? WriterLongMode : WriterShortMode);

            if (settings != null && !string.IsNullOrWhiteSpace(settings.extraPrompt))
            {
                sb.Append(Loc.Pick("\n【玩家补充的叙事要求（优先于上面的默认风格）】\n",
                        "\n【Extra narrative requirements from the player (these take precedence over the default style above)】\n"))
                  .Append(settings.extraPrompt.Trim()).Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>
        /// 玩家对"AI 能碰我的殖民者到什么程度"的设置，必须原样告诉模型。
        /// 写在提示词里只是告知；真正的墙在 WhoResolver 的校验里。
        /// </summary>
        private static string InfluenceSection(AINarratorSettings settings)
        {
            int value = settings != null ? settings.colonistInfluence : AINarratorSettings.InfluenceEverything;

            switch (value)
            {
                case AINarratorSettings.InfluenceNever:
                    return Loc.Pick(
                        "\n【殖民者保护】玩家选了「不允许」：所有角色动作都不能指向你的殖民者，who 只能用 handle:...（本事件生成的角色）。\n",
                        "\n【Colonist protection】 The player chose \"None\": no pawn action may target your colonists; who may only be handle:... (pawns spawned by this event).\n");

                case AINarratorSettings.InfluencePsychology:
                    return Loc.Pick(
                        "\n【殖民者保护】玩家选了「仅心理层」：pawn.thought / mood / relation / trait / set_skill 可以写 who=colonist:名字；"
                        + "pawn.hurt / pawn.kill / pawn.mental 仍只能用 handle:...。\n",
                        "\n【Colonist protection】 The player chose \"Psychology only\": pawn.thought / mood / relation / trait / set_skill may use who=colonist:Name; "
                        + "pawn.hurt / pawn.kill / pawn.mental may still only use handle:....\n");

                default:
                    return Loc.Pick(
                        "\n【殖民者保护】玩家选了「全部」：pawn.hurt / pawn.kill / pawn.mental 也可以用 who=colonist:名字 指向你的殖民者；"
                        + "但每次事件最多 3 次危险动作（危险预算），且目标必须存在、活着。\n",
                        "\n【Colonist protection】 The player chose \"Everything\": pawn.hurt / pawn.kill / pawn.mental may also point at your colonists with who=colonist:Name; "
                        + "but there are at most 3 dangerous actions per event (danger budget), and the target must exist and be alive.\n");
            }
        }

        /// <summary>第二次调用：让模型按概念写 Lua。</summary>
        public static void BuildWriter(Snapshot snapshot, string title, string concept,
            List<IncidentCandidate> candidates, AINarratorSettings settings,
            out string system, out string user, string previousScript = null, string previousError = null,
            LookupSession lookup = null)
        {
            // 动作手册按剧情关键词裁剪：常驻 23 个（0.5.0 的老动作），心理/场景/交互/世界按需出现。
            ActionCategoryMask mask = ActionSelector.Choose(title, concept, settings);
            LastMask = ActionSelector.Describe(mask);

            system = BuildWriterSystemPrompt(settings, mask);

            bool allowLong = settings != null && settings.enableLongEvents;

            var sb = new StringBuilder(2000);
            sb.Append(Loc.Pick("【这次要写的剧情】\n", "【The story to write this time】\n"));
            sb.Append(Loc.Pick("标题：", "Title: "))
              .Append(string.IsNullOrEmpty(title) ? Loc.Pick("未命名", "untitled") : title).Append('\n');
            sb.Append(Loc.Pick("概念：", "Concept: ")).Append(concept ?? "").Append('\n');
            sb.Append(Loc.Pick("形态：", "Form: "))
              .Append(allowLong
                  ? Loc.Pick("长线（可用 timer）", "long-running (timers allowed)")
                  : Loc.Pick("短线（不要 timer）", "short (no timer)"))
              .Append('\n');
            sb.Append(Loc.Pick(
                "默认：不给玩家选项，让事件自己发生；只有概念明确要求玩家拍板一个两难决定时，"
                + "才用 show_choices / choice_letter。\n",
                "Default: give the player no options and let the event happen on its own; use show_choices / choice_letter only when the concept explicitly asks the player to settle a dilemma.\n"));

            if (candidates.Count > 0)
            {
                sb.Append(Loc.Pick(
                        "\n【incident.fire 可用的原版事件名】优先照抄竖线左边的事件名作为 defName。"
                        + "如果没有适合剧情的事件，用信件或其它合适的动作写出后果。\n",
                        "\n【Vanilla incident names usable with incident.fire】 Prefer copying the name left of the pipe as defName. "
                        + "If no incident fits the story, write the consequences with a letter or another suitable action.\n"))
                  .Append(CandidateTable.ToPromptText(candidates, 15));
            }
            else
            {
                sb.Append(Loc.Pick(
                    "\n【原版事件】当前没有可触发的原版事件。不要使用 incident.fire，用信件或其它合适的动作写出后果。\n",
                    "\n【Vanilla incidents】 No vanilla incident can be fired right now. Do not use incident.fire; write the consequences with a letter or another suitable action.\n"));
            }

            if (settings != null && settings.legacyDefLists)
            {
                sb.Append(ActionCatalog.BuildDefSection(mask));
            }
            else
            {
                sb.Append(DefIndex.BuildCatalog(mask, lookup, settings));
            }

            sb.Append(Loc.Pick("\n【殖民地状况（写台词与后果时参考）】\n",
                "\n【Colony state (reference it when writing dialogue and consequences)】\n"));
            sb.Append(SnapshotBuilder.ToPromptText(snapshot));

            if (!string.IsNullOrEmpty(previousError))
            {
                sb.Append(Loc.Pick("\n【上一版被拒绝了】\n原因：", "\n【The previous version was rejected】\nReason: "))
                  .Append(previousError).Append('\n');
                if (!string.IsNullOrEmpty(previousScript))
                {
                    sb.Append(Loc.Pick("上一版脚本：\n```lua\n", "Previous script:\n```lua\n"))
                      .Append(previousScript).Append("\n```\n");
                }

                sb.Append(Loc.Pick(
                    "请修正报错的分支并重新给出完整脚本。若错误提到句柄或角色找不到，"
                    + "先检查本分支的生成动作：要么补上 args.handle（与 who 完全一致，区分大小写），"
                    + "要么把生成动作移到引用之前；确实不需要该角色时再删去该动作。"
                    + "缺少可用事件时，也可以删去该动作，用其它有效动作表现后果。\n",
                    "Fix the branch the error points at and return the complete script again. If the error mentions a handle or a pawn that cannot be found, "
                    + "check this branch's spawn actions first: either add args.handle (exactly matching who, case-sensitive), "
                    + "or move the spawn action before the reference; delete the action only if that pawn really is not needed. "
                    + "If no usable incident exists, you can also drop that action and express the consequences with other valid actions.\n"));
            }

            sb.Append(Loc.Pick("\n请按上面的输出格式写：先一行 TITLE，再一个 lua 代码块。\n",
                "\nWrite in the output format above: one TITLE line, then one lua code block.\n"));

            user = sb.ToString();
        }
    }
}

