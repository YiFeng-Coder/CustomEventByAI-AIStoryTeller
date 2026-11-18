# AI 叙述者实施计划案 v0.3

> **v0.3 修订摘要（2026-11-17，已开工）**
>
> - **不再依赖 RimMindCore。** 上一版 §2.1 计划"直接引用 Core 的三个程序集"，但 Core 本身是玩家要额外安装的前置。改为：**只复制 Storyteller 的 7 个纯逻辑源文件**（MIT，§2.2 那张表不变，另加 `JsonRepairer.cs` 147 行），LLM 客户端与设置页自研（约 350 行）。→ 本 Mod 的前置只剩 Harmony + RimLua。
> - **工期调整**：P1 导演从 3.5 → **6 人日**（自研网关 + 供应商设置页），R1 合计 **18.5 人日**。§6 表内数字以此为准。
> - **P0/P1 已交付并编译出 DLL**：骨架 + 导演闭环共 21 个文件，`1.7\Assemblies\AINarrator.dll` 53,760 字节、0 错 0 警。见 `Docs\P0-交付说明.md`、`Docs\P1-交付说明.md`。
> - §2.1 的"依赖二进制"清单保留作**参考**：RimMind 的队列/重试/限流设计仍然值得照抄（尤其"没注册 `IModCooldown` 就完全不限流"这个坑）。

- 替代：`计划案_AI叙述者_v0.1.md`（v0.1 含大量 Stellaris 多行星内容，已按你的指示全部剔除）
- 对应输入：《Stellaris_AI_Storyteller_开发指导_v0.1.docx》（仅保留与「叙述者本体」相关的部分）
- 目标目录：`Mods\AIIncidents`（当前只有 `Docs\` 与 `_work\`）
- 游戏版本：RimWorld **1.7.5011 rev24**（Waylith / .NET 10）
- 代码基线：**RimMind 2.0.0**（已取到源码，支持 1.7 + net10.0）、**RimLua 0.1.0-m0**（`Mods\AIStory`）
- 文档状态：**已开工；P0、P1 已交付并产出 DLL，P2 未开始**

---

## 0. 一页纸结论

1. **RimMind 现在真的能复用了。** 上一轮 GitHub 不通、只能"照设计重写"；这一轮 `codeload.github.com` 可达，已把 Core 与 Storyteller（各 2.0.0）源码取回本机。关键发现：**两者都已支持 1.7 / net10.0**，MIT（`Copyright (c) 2026 mcocdaa`），《指导》点名的 5 个类**全部真实存在**。→ 上一版计划里最贵的「自研 LLM 网关」直接归零。
2. **范围按你的指示收窄到「只有叙述者」。** 全部 Stellaris 内容（多行星、外部星系清单、遗址生成、跨 World 引用、1.6→1.7 移植）已从计划中删除。本 Mod 是**独立 Mod**，只依赖 Harmony + RimMind-Core + RimLua，跑在原版单 World / 多 Map 上。→ 省掉约 20 人日。
3. **供应商与预算不由我们做。** RimMind-Core 自带供应商/模型/API Key 的设置页与预算调度，玩家在那里选。我们只额外提供「叙事节奏」类设置（事件间隔、张力、每日调用上限、冷却），并且**不写死任何厂商**。
4. **我们真正要写的只有三块**：叙述者骨架（`StorytellerDef` + comp）、导演决策（一次调用决定"休整 / 原版事件 / 写剧情"）、**Lua 事件运行时**（AI 现场写 Lua，RimLua 沙箱执行，动作白名单落地）。第三块是本项目的核心增量，也是 RimMind 完全没有的能力。
5. **R1 = P0 + P1 + P2 ≈ 16 人日**，交付即「AI 叙述者能自主决定何时出事、能选原版事件、能现场写多阶段 Lua 剧情并产生真实后果」。
6. **构建已解决（此前结论有误，已更正）。** 早先我以为"环境禁止 csc 写文件"，实际是**便携 SDK 的宿主坏了**：它初始化首次运行状态时写不了自己的哨兵文件，之后凡是它要写的路径全被拒。改用系统 `C:\Program Files\dotnet` 的宿主 + 系统 csc，只从便携 SDK 借 `net10.0` 引用包，`csc` 正常产出 DLL。P0/P1 的产物已经编出来了（`1.7\Assemblies\AINarrator.dll`，53,760 字节，0 错 0 警）。详见 §1.4。

---

## 1. 本轮实测事实

### 1.1 网络（相比上一轮的关键变化）

| 端点 | 结果 | 说明 |
|---|---|---|
| `github.com:443` | ❌ 超时 | DNS 被解析到 127.0.0.1 |
| `api.github.com` | ❌ http=000 | 同上 |
| **`codeload.github.com`** | ✅ **http=200** | 源码压缩包通道可用，已成功下载两个仓库 |
| `api.nuget.org` | ✅ 200（跳转到 `nuget.azure.cn` 镜像） | 可还原 NuGet 包 |

已取回：`D:\RimWorldMods\RimMind\Core`、`D:\RimWorldMods\RimMind\Storyteller`（完整源码树）。

### 1.2 RimMind 2.0.0 的可复用性（已逐项核对源码）

| 项 | 事实 | 证据 |
|---|---|---|
| 许可 | **MIT**，`Copyright (c) 2026 mcocdaa` | `Core\LICENSE`、`Storyteller\LICENSE` 首行 |
| 版本 | 2.0.0，`supportedVersions` = **1.6 + 1.7** | 两个 `About\About.xml` |
| 构建目标 | `GameVersion != 1.6` 时 `TargetFramework=net10.0` | `RimMindCore.csproj:6-7` |
| 公开面 | `RimMindAPI.ApiVersion = 2`，静态门面 + 24 个 partial | `Core\Source\RimMindAPI.cs:37`、`Presentation\Api\RimMindAPI.*.cs` |
| 请求 | `Send(envelope, Action<Result<LlmResponse,RimMindError>>)` / `SendAsync` | `RimMindAPI.Request.cs` |
| 信封 | `Messages / SystemAugmentations / JsonSchema / MaxTokens / Temperature / Priority / ExpireAtTicks / MaxRetryCount / Tools` | `Domain\Llm\LlmRequestEnvelope.cs:7-39` |
| 程序集 | `0_RimMindDomain` / `1_RimMindApplication` / `2_RimMindCore`，引用方式 `Private=false`（不随包分发） | `RimMindCore.csproj:12`、`RimMindStoryteller.csproj:30-43` |
| 5 个关键类 | `StorytellerRequestCoordinator`(239) / `StorytellerResponseParserPure`(207) / `StorytellerMemory`(360) / `TensionMath`(61) / `RimMindIncidentSelector`(73) **全部存在** | `Storyteller\Source\**` |

### 1.3 RimMind 的实现要点（决定我们怎么接）

- **队列是被 tick 驱动的**：请求在后台 `Task.Run` 跑，结果进 `RequestCompletionInbox`，由 `AgentBusGameComponent.GameComponentTick` 在主线程 `Drain`。→ **主菜单里回调永不落地**，必须判断 `Current.Game != null`。
- `SendAsync` 的续体在线程池（`RunContinuationsAsynchronously`）→ **await 之后不能碰 Verse**。因此我们统一用**回调式 `Send`**，天然在主线程。
- 超时 30s、并发 4、重试默认 2 次这些都在 Core 里；**但没有限流**——限流只在注册了 `IModCooldown` 时生效（`QueueCircuitBreaker.cs:33-50`）。
- `ExpireAtTicks` **只在显式设置时**才做过期丢弃；不设就永不过期。我们必须自己设。
- **`Priority` 语义与直觉相反**：`Low(0)` 先派发、`Immediate(4)` 最后（`RequestQueue.cs:147-148,210`）。→ 我们一律用默认 `Normal`，不做优先级调度。
- 若 `Messages` 非空，Core **跳过**它自己的 6 层上下文构建（`ContextBuildMiddleware.cs:43-49`）。→ 我们本来就要求快照是"我们自己算好的、有上限的、Lua 友好的"，所以走这条路，完全不碰它的上下文引擎。
- **RimMind 的选事件根本没有候选表**：它把 `defName` 交给模型自由生成，再靠 `DefDatabase.GetNamedSilentFail` 兜（`RimMindIncidentSelector.cs:22-69`）；防重复只靠 prompt 里"最近 5 条"的文本（`StorytellerMemory.cs:147-159`）。→ 这正是 Steam 高赞长评骂的「事件极端不均、只会重复刷野外营地」的机制来源。**我们补这块**（§4.5）。

### 1.4 构建环境（已解决，原结论作废）

第一版结论写的是"csc 无法创建任何文件"，**这是错的**。真实原因：**便携 SDK 的宿主坏了**——它启动时要往用户目录写首次运行的哨兵文件，写不进去，之后凡是它发起的写操作都被拒（表现为 `CS2012`、MSBuild 建临时目录失败）。换成系统宿主就没问题。

| 测试 | 结果 |
|---|---|
| 便携 `dotnet.exe`（SDK 10.0.402）跑 `dotnet new console` | ❌ 一个文件都没建出来 |
| 系统 `dotnet.exe`（SDK 9.0.304）跑 `dotnet new console` | ✅ 正常创建并 restore |
| 系统宿主 + 系统 `csc.dll` + 便携 SDK 的 `net10.0` 引用包 | ✅ **产出 53,760 字节 DLL，0 错 0 警** |
| 系统 csc 输出到工作区任意路径 | ✅ 允许 |

结论：**用系统宿主 + 系统 csc，只从便携 SDK 借 net10 引用包**，写进 `tools/build.ps1`。产物已验证：类型名（`StorytellerComp_AINarrator`、`Patch_IncidentWorker_TryExecute`、`DirectorLoop`…）与引用（`Assembly-CSharp`、`0Harmony`、`System.Text.Json`、`System.Net.Http`）全部就位。

---

## 2. 复用账本

### 2.1 A 类：直接依赖二进制（不复制、不打包）

| 能力 | RimMind 提供 | 我们省掉的 |
|---|---|---|
| 异步请求队列（分桶/优先级/冷却/熔断） | `RequestQueue` + `RequestSubmissionService` | 整个调度器 |
| 重试与超时 | `RetryMiddleware`、30s 超时 | 重试策略与取消 |
| 供应商 / 模型 / API Key / 设置页 | `RimMindCoreSettings` | **整个 Provider 层与设置 UI**（正好是你说的"留给玩家自己选"） |
| JSON 修复与解析 | `RimMindAPI.Prompt.Serialize/Deserialize/TryDeserialize/ExtractString` | 自写解析器 |
| 日志脱敏 | `RimMindAPI.Diagnostics.SanitizeForLog` | 自写脱敏 |
| 主线程投递与快照 | `RimMindAPI.MainThread.Post`、`MainThreadSnapshot.GetAsync` | 线程边界工具 |
| 事件总线 | `RimMindAPI.GameEvents.Subscribe<T>/Publish<T>` | 自写信号总线（可直接当事件状态机的信号源） |
| 叙事记忆（软依赖） | `RimMindAPI.Memory.AddNarratorMemory/GetRecentNarrations`，缺模块时走 `NullMemoryBridge` 返回空 | 可选增强，不阻塞 |

依赖声明：`modDependencies` = `brrainz.harmony` + `mcocdaa.RimMindCore`，`loadAfter` 同序。**不打包任何 RimMind dll**。

### 2.2 B 类：复制进我们工程（MIT，保留版权头）

按《指导》§11.1 的要求建立代码来源清单。以下文件**不引用任何 RimMind 类型**，复制后只依赖 Newtonsoft.Json 与 Verse：

| 文件 | 行数 | 复制后需要改什么 |
|---|---|---|
| `Storyteller\Source\Memory\TensionMath.cs` | 61 | 内联 `RimMindTime` 常量（60000/2500） |
| `Storyteller\Source\Storyteller\IncidentSelectionPolicy.cs` | 31 | 无 |
| `Storyteller\Source\Memory\IncidentResponse.cs` | 26 | 无 |
| `Storyteller\Source\Storyteller\StorytellerRequestState.cs` | 119 | 改命名空间 |
| `Storyteller\Source\Storyteller\StorytellerResponseParserPure.cs` | 207 | 可改为调 `RimMindAPI.Prompt`，或连 `JsonRepairer.cs`(147) 一起复制 |
| `Storyteller\Source\Memory\IncidentHistoryRecord.cs` | 42 | 改 2 个翻译 key |
| `Storyteller\Source\Memory\StorytellerPersistenceCodec.cs` | 59 | **改 Scribe 节点名**（避免与 RimMind 同名组件节点冲突） |

合计 ≈545–692 行。**收益：叙事节奏、候选策略、回复解析这三块不用从零设计。**

### 2.3 C 类：必须自己写

| 模块 | 为什么不能复用 |
|---|---|
| `StorytellerComp_*`（我们的） | RimMind 的 comp 只吐 `FiringIncident`，我们要吐「事件实例 + Lua 计划」 |
| 请求协调器 | RimMind 的是 `internal`，且流程绑死 incident |
| 记忆/状态组件 | `StorytellerMemory` 强耦合 `RimMindStorytellerMod.Settings`（4 处直取），且是 `WorldComponent` 单例；我们要存事件实例，结构完全不同 |
| **候选事件表** | RimMind **没有**（见 §1.3），必须自建 |
| **Lua 事件运行时** | RimLua 只有执行器，没有事件语义 |
| 动作层 / 幂等 / 状态机 / 存档 | RimMind 没有 |

### 2.4 RimLua：继续零改动复用

结论同 v0.1，且已核对到源码级：

- 沙箱：`Preset_HardSandbox + ErrorHandling + Metatables`（`LuaRuntime.cs:83`），`math/string/table/bit32` 可用，`io/os/require/load/dofile/coroutine` 为 nil。
- 调用：`LuaRuntime.CreateScript/InstallGlobals/RunChunk/CallFunction(script, fn, args, wall, slice)` 全 public。
- 参数：`LuaValueConverter.FromClr` 递归支持 `IDictionary`/`IEnumerable`/数值/字符串/布尔 → `Dictionary<string,object>` 树可安全传入。
- 返回：`CallScript` 只回字符串 → 用我们注册的 `rim.aist.json_encode(Table)` 拿结构化结果（`Table` 参数受支持，`ToClr` 直接取 `value.Table`）。
- 预算：自己传 `wall/slice`，不受 RimLua 全局设置约束；`Slices × slice` 即指令数估计。
- **不改一行 RimLua**。

---

## 3. 范围裁决

| 做（R1） | 不做（明确出局） |
|---|---|
| 独立 Mod，`StorytellerDef` + 自己的 `StorytellerComp` | 任何 Stellaris 集成（多行星、外部清单、遗址生成、跨 World） |
| 一次调用决定"休整 / 原版事件 / 写新剧情" | 供应商、模型、Key、预算 UI（归 RimMind-Core） |
| 自建候选事件表 + 去重 + 张力节奏 | 通用场景/自由几何生成 |
| Lua 多阶段事件（信件、选项、分支、计时器） | Pawn 深度设计（技能/背景/关系/遗体）——列为 R2 可选 |
| 动作白名单（8 个）+ 幂等 + 状态机 + 存档 | 逐 Tick 指挥 Pawn、自定义战斗算法 |
| 设置：节奏、张力、冷却、每日上限、调试日志 | 任何硬编码的厂商默认值 |

---

## 4. 系统设计

### 4.1 目录与程序集

```
Mods\AIIncidents\
  About\About.xml                     # packageId: Feng.AINarrator（待定，见 §10）
  LoadFolders.xml
  Defs\StorytellerDefs\Stellaris_AINarrator.xml
  Defs\StorytellerDefs\Stellaris_AINarrator_Fallback.xml
  Languages\ChineseSimplified\...
  1.7\Assemblies\AINarrator.dll
  Source\AINarrator\
    AINarrator.csproj                 # net10.0；引 Assembly-CSharp/Waylith/UnityStubs/RimLua/RimMind 三程序集
    Core\          # Ids / Result / Json / Log / VersionGuard
    RimMind\       # 适配层：EnvelopeFactory、RequestBridge、ProviderRegistrar、Cooldown
    Director\      # DirectorLoop、BudgetPolicy、TensionMath(复制)、ProposalParser
    Context\       # SnapshotBuilder、SnapshotDto、CandidateTable、RecentHistory
    Events\        # EventInstance、StateMachine、SignalBus、IdempotencyLog、TimerWheel
    Lua\           # LuaStageRunner、LuaScriptCache、LuaJsonBridge
    Actions\       # IActionHandler、ActionRegistry、Handlers\*
    Persistence\   # NarratorWorldComponent（JSON 整包）
    UI\            # SettingsTab、Dialog_NarrativeChoice、Window_NarratorDebug
    Patches\       # Harmony: Incident 执行记录、Site/Map 进入、Pawn 死亡
  Tests\AINarrator.Harness\           # net10.0 离线验证器（不引用 Verse）
  Docs\                                 # ARCHITECTURE / ACTION_REGISTRY / SAVE_FORMAT / PLAN
  Licenses\                             # RimMind-MIT.txt + THIRD_PARTY_NOTICES.md（含复制文件清单）
  tools\build.ps1                       # 照抄 AIStory\tools\build.ps1 的 csc+rsp 方案
```

程序集名 `AINarrator.dll`，根命名空间 `Stellaris.AI`（沿用《指导》§1.3 的 `Stellaris.*` 约定，DefName 全部 `Stellaris_` 前缀）。

### 4.2 线程与数据流

```
[主线程] StorytellerComp.MakeIntervalIncidents(target)
   ├─ 到点且队列非空 → yield FiringIncident（原版事件）
   └─ 触发条件满足 → TryDispatch(target)                 ← 只投递，不等待

[主线程] GameComponentTick
   ├─ SnapshotBuilder（纯 DTO，<1ms，有上限）
   ├─ EnvelopeFactory → RimMindAPI.Send(env, OnResult)   ← 回调保证在主线程
   └─ TimerWheel / 事件状态机推进

[后台] RimMind-Core 队列（30s 超时 / 重试 2 / 熔断）

[主线程] OnResult
   ├─ 校验 epoch / mapId / expire → 过期即弃
   ├─ ProposalParser（复制来的 ParserPure）→ 意图
   ├─ 原版事件 → 入待发射队列（带 fireAtTick）
   └─ 写剧情 → 校验 Lua → 编译 → 执行首阶段 → 生成 EventInstance
```

三条不变量：
1. 后台线程只碰 `string` 与 DTO；任何 `Find.*` 只在主线程。
2. 每次请求带 `ExpireAtTicks`（= now + 2×间隔），回来时 epoch 不符直接丢弃。
3. 所有副作用带 `eventInstanceId + opId`，重复执行被幂等表拒绝。

### 4.3 叙述者接入

```xml
<StorytellerDef ParentName="BaseStoryteller">
  <defName>Stellaris_AINarrator</defName>
  <label>AI 叙述者</label>
  <comps>
    <li Class="Stellaris.AI.StorytellerComp_AINarrator">
      <props><mtbDays>1.5</mtbDays></props>
    </li>
  </comps>
</StorytellerDef>
```

`MakeIntervalIncidents` 的顺序**照抄 RimMind 已被验证的形状**（惰性迭代器、零阻塞）：

1. 只处理带 `Map_PlayerHome` 标签的 `Map` 目标；
2. 有到点的待发事件 → `yield` 一个就 `break`；
3. 有在途请求 → 直接返回；
4. 上次失败距今 < 2500 tick → 返回；
5. `RimMindAPI.IsConfigured()` 为假 → 返回（玩家还没配 Key）；
6. 设置里关了自动触发 → 返回；
7. `RimMindAPI.ShouldSkipStorytellerIncident()`（其他 RimMind 模组请求静默）→ 返回；
8. `Rand.MTBEventOccurs` → 投递请求。

另出 `Stellaris_AINarrator_Fallback`：把原版 `RandomMain` 的 comp 与我们的 comp 并列，给「API 挂了但玩家不想空窗」用；由设置项在开局选。我们的 comp 内部也有健康检查（连续 N 次失败 → 停 1 游戏日），逻辑抄 `StorytellerComp_RimMindFallback` 的思路。

### 4.4 一次叙事 = 最多两次调用

**第 1 次（Director）**：输入快照 + **候选表**（§4.5）+ 张力 + 最近历史，输出三选一。

```json
{"decision":"no_event","reason":"殖民地刚经历袭击，进入休整期","rest_until_days":2}
{"decision":"vanilla_incident","defName":"RaidEnemy","points_multiplier":0.8,
 "reason":"...","announce":"..."}
{"decision":"lua_event","title":"反应堆残响","concept":"废弃反应堆里的幸存者",
 "stages_hint":["信号","进入","抉择"],"budget":{"threat":1.0,"reward":0.6}}
```

**第 2 次（EventWriter）**：只在 `lua_event` 时触发，输出事件包（阶段表 + Lua 源码 + 元数据）。校验失败**只允许一次修复**，再失败即作废本次提案（写日志 + 进入休整）。

这样单次叙事的 API 成本是 1–2 次调用，且 `no_event` 是**一等公民**——直接对着《指导》§4.1「禁止每次响应都必须发生事件」落地，也正好避开 RimMind 最大的体验问题。

### 4.5 候选表：我们相对 RimMind 的硬改进

启动时（`StaticConstructorOnStartup`）扫 `DefDatabase<IncidentDef>.AllDefs`，生成：

```
候选 = { defName, category(ThreatSmall/ThreatBig/Misc/FactionArrival/...),
         targetTags, worker 类型, minThreatPoints, maxThreatPoints,
         needsFaction, pointsCurveHint }
```

过滤规则：`targetTags` 含 `Map_PlayerHome`、`worker` 在白名单内（排除 `IncidentWorker_Quest`/`IncidentWorker_ShipChunkDrop` 等由其他系统驱动的）、难度与当前 `StorytellerUtility.DefaultThreatPointsNow` 可匹配。

Prompt 里**只给这张表的 defName**；模型返回的 `defName` 不在表内 → 直接判非法（而不是 `GetNamedSilentFail` 兜底）。这就把「幻觉事件」变成不可能，也把「总刷同一类」用显式配额压掉：

- 每类别滚动窗口（最近 10 个事件）配额：`ThreatBig ≤ 3`、`Misc ≤ 4`；
- 同一 `defName` 30 游戏日内不得重复；
- 连续 3 次 `Threat*` 后强制 `no_event` 或 `Misc`。

这些配额是**代码强制**，不是靠提示词求模型自觉——这是 RimMind 那套最缺的一层。

### 4.6 Lua 事件运行时（核心增量）

一次事件 = 一个 `EventDefinition`（脚本 + 阶段表）→ 若干 `EventInstance`（具体某次发生）。每阶段**独立执行、独立存档**，禁止保存挂起的协程（RimLua 本来也禁 `coroutine`）。

Lua 契约（AI 生成物的唯一形状）：

```lua
-- ctx: 只读快照表；state: 本事件局部状态表
function run(ctx, state)
  local actions = {}
  local s = state
  if ctx.trigger == "start" then
    actions[#actions+1] = { opId = "a1", type = "story.send_letter",
      args = { title = "异常信号", text = "轨道扫描发现地下信号。" } }
    s.stage = "await_entry"
  elseif ctx.trigger == "site_entered" and s.stage == "await_entry" then
    actions[#actions+1] = { opId = "a2", type = "story.show_choices",
      args = { prompt = "舱门后仍有微弱的电流声。", options = {
        { id = "cool", label = "尝试冷却" }, { id = "leave", label = "撤离" } } } }
    s.stage = "await_choice"
  end
  return rim.aist.json_encode({ actions = actions, next_state = s })
end
```

C# 侧执行（全部已核对可行）：

```csharp
Script script = LuaScriptCache.Get(scriptHash, source);          // 内存编译，不落盘
var args = new object[] { ctxDict, stateDict };                  // Dictionary 树
LuaCallResult r = LuaRuntime.CallFunction(script, "run", args, 25, 2000);  // 25ms / 2000 指令每片
if (!r.Ok) => 阶段失败（不落任何副作用）
StagePlan plan = JsonSerializer.Deserialize<StagePlan>(r.ReturnText);
```

四道闸（RimLua 自己没有的）：源码 ≤64KB、单阶段动作 ≤32、`next_state` ≤32KB、墙钟 25ms（超时即失败）。加上 RimLua 的每片 2000 指令，`Slices` 可反推总量。

### 4.7 存档

`NarratorWorldComponent : WorldComponent`，单包 JSON（**节点名要与 RimMind 的 `StorytellerMemory` 区分开**）：

```json
{ "schemaVersion": 1, "epoch": 42,
  "director": { "tension": 0.42, "nextEligibleTick": 380000, "failStreak": 0 },
  "defs": [ {"id":"evt_def_3","title":"反应堆残响","hash":"sha1:...","source":"function run(...)"} ],
  "instances": [ {"id":"evt_0007","defId":"evt_def_3","stage":"await_choice",
                  "state":{...},"executed":["a1","a2"],"timers":[{"at":385000,"signal":"game_timer_due"}]} ],
  "log": [ {"t":380000,"kind":"outcome","text":"..."} ] }
```

约束：单包 ≤512KB；`log` 保留最近 200 条 + 滚动摘要；**不存 API Key**（在 RimMind-Core 设置里）；**不存 Lua VM**（读档后按 source 重编译）。

---

## 5. 契约

### 5.1 动作注册表 v1（白名单，8 个）

| opType | 权限 | 参数 | 预算 | 失败处理 |
|---|---|---|---|---|
| `story.send_letter` | P0 | title≤80, text≤1200, letterDef | 1/阶段 | 跳过并记日志 |
| `story.show_choices` | P0 | prompt≤600, options[2..4]{id,label≤40} | 1/阶段 | 回退为普通信件 |
| `story.remember` | P0 | key, text≤400, tags | 8/阶段 | 忽略 |
| `story.set_tension` | P0 | delta∈[-0.5,0.5] | 1/阶段 | 忽略 |
| `incident.fire` | P1 | incidentDefName(必须在候选表), points_multiplier∈[0.3,2.0], targetKind | 1/阶段 | 拒绝，转 `Failed` |
| `pawn.spawn` | P1 | pawnKindDef, factionHandle, cell, count≤4 | PawnEditBudget | 部分失败即回滚 |
| `thing.spawn` | P1 | thingDef, count≤20, qualityRange | RewardBudget | 同上 |
| `map.damage_area` | P2 | rect, damageDef, points | DangerBudget | 拒绝 |

白名单外一律拒；权限与预算由 C# 硬编码，Lua 无法自称高权限。

### 5.2 事件状态机

| 状态 | 进入条件 | 下一步 |
|---|---|---|
| `Scheduled` | 导演批准、写入 `fireAtTick` | 到点 → `Ready` |
| `Ready` | 到点且目标（map/pawn）仍合法 | 校验 → `Validating` |
| `Validating` | Schema / 权限 / 预算 / 引用全通过 | 通过 → `Active`；失败 → `Rejected` |
| `Active` | 动作逐个执行（幂等） | 需等待 → `Waiting`；结束 → `Completed`/`Failed` |
| `Waiting` | 等 timer / 玩家选择 / Pawn 死亡 | 信号到 → `Active`；超时 → `Failed` |
| `Rejected`/`Completed`/`Failed` | 终态 | 写记忆与审计 |

### 5.3 设置项（我方可配，其余归 RimMind-Core）

`enableAutoTrigger` / `mtbDays`(1.5) / `maxLuaEventsPerDay`(3) / `maxApiCallsPerDay`(40) / `minIntervalMinutes`(30) / `tensionDecayPerDay`(0.03) / `enableFallbackStoryteller` / `debugLogging` / `maxLogRecords`(200)。

供应商、模型、温度、超时、并发**全部来自 RimMind-Core 的设置页**，我们不复制、不覆盖、不写默认值。

---

## 6. WBS 与验收

| 阶段 | 交付物 | 验收标准（尽量可自动判定） | 人日 |
|---|---|---|---|
| **P0 骨架** | SDK 工程 + `build.ps1` + 两个 `StorytellerDef` + 空 comp + `NarratorWorldComponent` + 设置页 + 日志前缀 + 版本守卫（读 `RimMindAPI.ApiVersion`，不符则禁用并提示） | ① 游戏能起、能选到叙述者、无红字；② `build.ps1` 一条命令出 dll（**需你那边跑一次**）；③ 缺 RimMind-Core 时给出可读提示而不是崩 | 2.5 |
| **P1 导演** | 自研 ILlmGateway + EnvelopeFactory + SnapshotBuilder + 候选表 + 配额 + ProposalParser（复制）+ 张力（复制）+ 待发队列 + 原版事件落地 + 失效/失败退避 | ① 能产出 `no_event` 且不报错；② 只能选中候选表内的事件（构造 20 个幻觉 defName，全部被拒）；③ 断网/401/429 不崩不卡帧；④ 同一事件不重复；⑤ 类别配额生效（脚本化注入 20 次决策） | 3.5 |
| **P2 Lua 事件运行时** | `LuaStageRunner` + `json_encode` 桥 + `ActionRegistry` + 校验器 + 幂等表 + 状态机 + 计时器 + 存档 + `Dialog_NarrativeChoice` | ① 不给任何专用 C# 事件类，AI 自写的三阶段事件跑通；② 死循环/超预算/越权/坏 JSON 全部被拒且游戏继续；③ 每阶段存读档后续接；④ 重复 signal 只生效一次；⑤ 选项分支正确落地 | 10 |
| **P2 收尾** | 文档：`ACTION_REGISTRY.md`、`SAVE_FORMAT.md`、`THIRD_PARTY_NOTICES.md`、复制文件来源清单 | 许可与来源可追溯 | 含在 P2 |

**R1 = 18.5 人日**（2.5 + 6 + 10）。 之后的可选扩展（不在本次范围）：

| 可选 | 内容 | 人日 |
|---|---|---|
| R2-a | Pawn 设计器（技能/背景/关系/遗体，把 `pawn.spawn` 升级为 `PawnBlueprint`） | 5 |
| R2-b | 场景生成（原版 `Site` + 自写 `GenStep`，读冻结蓝图） | 8 |
| R2-c | 长期叙事弧（跨事件的伏笔与回收，`story.remember` 升级为线索图） | 4 |

> 人日口径：熟悉 RimWorld Mod + C# 的开发者的净工作日；以 AI 结对推进，日历天数约 0.4–0.6×。

---

## 7. 里程碑

- **M1（P0 结束，约 2.5 人日）**：选 AI 叙述者 → 无红字 → 日志里能看到 `RimMindAPI.ApiVersion` 与候选表条数。
- **M2（P1 结束，约 8.5 人日）**：AI 自主决定「休整 / 发一次原版袭击」，debug 窗口能看到完整 prompt、候选表、决策理由、配额状态。
- **M3（P2 结束，约 16 人日）**：一次 AI 现场写的三阶段剧情，含信件、选项、分支、跨存读档续接。

---

## 8. 风险登记

| # | 风险 | 概率/影响 | 对策 |
|---|---|---|---|
| R1 | **本会话无法编译**（§1.4 已实证） | 确定/高 | 源码 + `build.ps1` + 验收清单交付；P0 验收要求你（或另一个允许写出的会话）跑一次构建并把日志给我。若你的环境同样被挡，改用 VS/Rider 手工构建 |
| R2 | RimMind-Core API 仍在收敛（`ApiVersion=2`，门面上 34+ 个 `[Obsolete]`） | 中/中 | 启动读 `ApiVersion` 校验；所有调用收敛到 `RimMind/` 一个目录；锁 2.0.0 并在 About 里声明依赖版本 |
| R3 | `Priority` 语义反直觉、`ExpireAtTicks` 不设就不过期 | 中/中 | 只用 `Normal`；每个请求强制设 `ExpireAtTicks` |
| R4 | 回调只在游戏内 tick 才 drain | 中/低 | 请求前判 `Current.Game`；回调里再判一次 epoch |
| R5 | 复制的类与 RimMind 同名 → Scribe 节点冲突 | 中/低 | 全部改类名 + 改 Scribe key（`StorytellerPersistenceCodec` 已列入必改项） |
| R6 | RimLua 是 0.1.0-m0，public 运行时可能变 | 中/中 | 5 个调用点集中在 `Lua/`；加冒烟测试；pin 版本 |
| R7 | RimLua 无总指令数/内存上限 | 中/中 | 我们的四道闸（源码 64KB / 动作 32 / 状态 32KB / 25ms） |
| R8 | 模型幻觉 defName / 越权动作 | 高/中 | 候选表白名单 + 动作白名单 + 数值范围，全部代码强制 |
| R9 | 调用成本（玩家自己配 Key，花的是玩家的钱） | 中/中 | `IModCooldown`（否则 Core 完全不限流）+ 每日上限 + 最小间隔 + 默认 `mtbDays=1.5` |
| R10 | 存档膨胀 | 低/中 | 单包 512KB、日志滚动、脚本按 hash 去重 |
| R11 | 与 RimMind-Storyteller 同时启用 | 中/低 | 两者是不同 `StorytellerDef`，互斥选择；文档里说明不要同时选 |

---

## 9. 许可与合规

| 组件 | 许可 | 义务 |
|---|---|---|
| RimMind-Core / Storyteller | **MIT**（`Copyright (c) 2026 mcocdaa`） | ① 复制文件保留版权头；② `Licenses/RimMind-MIT.txt` 放原文；③ `THIRD_PARTY_NOTICES.md` 记录**每个复制文件的来源路径、行数、改动点**；④ 不暗示原作者背书 |
| MoonSharp | BSD-3-Clause | 由 RimLua 分发，我们不打包 |
| RimLua | 自有 | 只引用 `Private=false`，不打包 |
| Newtonsoft.Json | MIT | 不打包（RimMind-Core 自带） |

依赖声明策略：`modDependencies` 写 `brrainz.harmony` + `mcocdaa.RimMindCore`；**不**硬依赖 `mcocdaa.RimMindMemory`（代码里走 `NullMemoryBridge` 降级）。

---

## 10. 待确认（都很小，可默认执行）

| # | 决策 | 默认 |
|---|---|---|
| D1 | Mod 身份：目录 `AIIncidents`、`packageId=Feng.AINarrator`、显示名「AI 叙述者」、程序集 `AINarrator.dll` | 按此执行，你否掉再改 |
| D2 | 命名空间用 `Stellaris.AI`（沿用《指导》§1.3）还是改 `RimNarrator.*` | 用 `Stellaris.AI` + DefName `Stellaris_` 前缀 |
| D3 | 选项弹窗走原生 `Dialog_NodeTree` 还是自绘 Window | 自绘 `Dialog_NarrativeChoice`（能显示更长的叙事文本、可存读档） |
| D4 | 每日调用上限默认 40 次是否合适 | 40，可配 |

---

## 11. 审核通过后的第一批动作（P0）

1. `AINarrator.csproj`（net10.0）+ `tools\build.ps1`（照抄 RimLua 的 csc+rsp 方案，SDK 指 `PioneerFrameworkLite\.zcode\dotnet10`）。
2. `About.xml`（依赖 Harmony + `mcocdaa.RimMindCore` + `Feng.RimLua`）+ `LoadFolders.xml`。
3. 两个 `StorytellerDef` + `StorytellerComp_AINarrator`（只 yield 空集合）+ `StorytellerCompProperties`。
4. `NarratorWorldComponent`（空 Scribe）+ 设置页（节奏类字段）+ `VersionGuard`。
5. 候选表扫描器 + 启动日志（打印候选条数与 `RimMindAPI.ApiVersion`）。
6. 构建冒烟（**需要你跑**）→ 游戏内验证选叙述者无红字 → 回传 `Player.log`。
