# Stellaris AI Storyteller 实施计划案 v0.1

- 对应输入：`Stellaris_AI_Storyteller_开发指导_v0.1.docx`（下称《指导》）
- 本机代码基线：RimLua 0.1.0-m0（`Mods\AIStory`）、Stellaris 1.6（`Mods\Stellaris`、`RimStellaris`、`Stellaris_Codex`）
- 目标目录：`Mods\AIIncidents`（当前为空目录，尚无任何文件）
- 游戏版本：RimWorld **1.7.5011 rev24**（Waylith / .NET 10，`Assembly-CSharp.dll` 已确认）
- 文档状态：**待审核，未开工**。审核通过后才进入 P0。

---

## 0. 一页纸结论

1. **能复用的大头不是 RimMind，而是 RimLua。** RimLua 已经把 MoonSharp 硬沙箱、C#→Lua 调用、API 注册表、切片指令预算、脚本状态存读档全做完了。我们因此**不需要集成 MoonSharp、不需要自己写沙箱**，Lua 层只剩约 400–600 行胶水。这是本项目最大的一笔节省。
2. **RimMind 目前无法复用，且这是本计划最大的不确定项。** 本机无 RimMind 源码；本机到 GitHub / raw.githubusercontent 的网络被阻断（实测超时），只有搜索服务可用；公开资料**无法证实**《指导》里点名的 `StorytellerRequestCoordinator` / `StorytellerResponseParserPure` / `StorytellerMemory` / `TensionMath` 这几个类的存在，也**无法证实 MIT 许可**。→ 需要你提供源码包（见 §10 D1）。
3. **Stellaris 的可复用点在「地图/站点生成」和「跨世界清单」，不在叙事。** 全库 grep 确认：Stellaris 三个目录里**没有任何** LLM/HTTP/Lua/StorytellerComp/ChoiceLetter/Quest 代码。但它有现成的遗址生成链、房间布局器、跨行星外部清单，这些是 M3/M5 的实货。
4. **建议把《指导》的 M0–M6 重切成 R1（可玩第一版）+ 后续扩展。** R1 = 骨架 + 导演 + 原版事件 + Lua 多阶段事件 + 参数化遗址，约 **24 人日**；《指导》§7 的「自由几何求解器」（12–16 人日）单独立项、不进 R1。理由见 §3-B。
5. **有两处《指导》与工程现实冲突，必须改**：跨行星记忆不能放 `GameComponent`（每颗行星是独立存档文件，见 §3-A）；`RimLua` 的 `CallScript` 只回字符串，拿不回表（已用零改动方案绕过，见 §4.4）。

---

## 1. 核对过的现状

以下每条都是我在本机实际验证过的，不是从文档抄的。

| # | 事实 | 证据 |
|---|---|---|
| 1 | 目标目录 `Mods\AIIncidents` 完全为空，无工程、无 Def、无 git | 递归列目录计数为 0 |
| 2 | 游戏为 1.7.5011 rev24；托管 DLL 在游戏根目录（**没有** `RimWorldWin64_Data\Managed`） | `Version.txt`；根目录含 `Assembly-CSharp.dll`/`Waylith.dll`/`UnityStubs.dll`/`coreclr.dll` |
| 3 | 运行时可引用 `System.Text.Json.dll`(1.87MB)、`System.Net.Http.dll`(1.74MB) | 游戏根目录存在，随 `RimWorld.deps.json` 装载 |
| 4 | 1.7 的叙述者家族完整，含 `StorytellerComp`/`StorytellerCompProperties` 与全部 `*_MakeIntervalIncidents`；另有 `StorytellerUtility`、`StorytellerUtilityPopulation`、`StorytellerStaticUpdate`、`StorytellerTick` | 从 `Assembly-CSharp.dll` 元数据串提取 |
| 5 | 1.7 的叙事/任务积木齐全：`ChoiceLetter`、`Dialog_NodeTree`、`PawnGroupMakerUtility`、`TryExecuteWorker`、`DefaultThreatPointsNow`、`FiringIncident`、`MapGeneratorDef` | 同上 |
| 6 | 1.7 的 `GenStep` 家族含 `GenStep_AncientComplex`、`GenStep_AncientReactor`、`GenStep_ConditionCauser` 等 | 同上 |
| 7 | RimLua 已实现：硬沙箱、`[LuaModule]`/`[LuaFunction]`/`[LuaGlobal]` 注册、`CallScript`、切片指令预算、脚本 state 存读档、Lua 控制台 | `AIStory\Docs\EXTENSION_API.md`、`CALL_LUA_AND_APIDUMP.md` |
| 8 | RimLua 硬沙箱 = `Preset_HardSandbox` + `ErrorHandling` + `Metatables`；`math/string/table/bit32` 可用，`io/os/require/load/dofile/coroutine` 为 nil | `Source\RimLua\Lua\LuaRuntime.cs:83`；`Docs\research\RESEARCH-moonsharp.md:63` |
| 9 | RimLua **没有**：游戏对象 API、事件/定时器、命令层、源码大小上限、总指令数上限、内存上限、带行号的错误 | 同上，逐项 grep 确认 |
| 10 | Stellaris 主体只有 1.6；`Stellaris.csproj` 是 net472 旧式工程，`RimWorldWin64_Data\Managed` 与 5 个 UnityEngine HintPath **全部悬空**，在 1.7 上编不过 | `Mods\Stellaris\Source\Stellaris\Stellaris.csproj:2,12` |
| 11 | `RimStellaris` 与 `Stellaris_Codex` 是同一份代码的两份拷贝（csproj 同 SHA256、dll 同 hash）；实质主体 207 个编译单元 / 66,659 行 | 子代理比对 |
| 12 | Stellaris 三个目录 + 三个 AIGC 快照目录，`HttpClient`/`WebRequest`/`OpenAI`/`JsonConvert`/`MoonSharp` **0 命中** | 全量 grep |
| 13 | 每颗行星 = 独立存档文件：旅行时 `SaveGame(current)` → `LoadGame(target)` 或 `new Game()`+`GenerateWorld` | `PlanetTravel\PlanetWorldTravelService.cs:223,306,454` |
| 14 | 新建 World 时会 `new Storyteller(storytellerDef, difficultyDef)`，叙述者实例每颗行星重建 | 同上 `:457` |
| 15 | 跨行星权威数据在**外部文件**，不在存档内：`Config/Stellaris/Stellaris_PlanetWorldManifest_<campaignId>.xml` | `PlanetWorldManifest.cs:256` |
| 16 | 本机无 `net10.0` 系统 SDK（系统只有 9.0.304/8.0/9.0 ref pack），但 PioneerFrameworkLite 内带便携 SDK 10.0.13 | `PioneerFrameworkLite\.zcode\dotnet10\packs\...\10.0.13\ref\net10.0`，RimLua 的 `.build\rimlua.rsp` 正是引它 |
| 17 | 网络：`github.com:443` 不通，无代理；`web_search` 可用 | `Test-NetConnection` = False |

---

## 2. 复用账本

### 2.1 RimLua —— 最大一笔复用（省 ≈1,500–2,000 行 + 全部 MoonSharp 风险）

| 能力 | RimLua 现状 | 对我们 |
|---|---|---|
| MoonSharp 宿主 + 硬沙箱 | 完成 | **直接用**，不重复集成 |
| 指令切片 + 墙钟预算 | 5000 指令/片、50ms 墙钟（可配） | 用 `LuaRuntime.CallFunction` 直接传自己的预算 |
| C#→Lua 调用 | `CallScript(id, fn, args)` 主线程 | 受限于「只回字符串」，已设计绕过（§4.4） |
| API 注册/编解码 | 特性扫描、白名单类型、递归 `IDictionary`/`IEnumerable` 转换 | **直接用**，`Dictionary` 树可安全进出 Lua |
| 脚本状态持久化 | 单包 JSON 存 `GameComponent`，深度 16 / 4096 节点 / 256KB | 只借其「整包 JSON + 上限」模式，不用它的 state 语义 |
| 编译/加载/热重载 | `CreateScript`/`InstallGlobals`/`RunChunk` 均 public | **直接用**，脚本可完全在内存执行，不落盘 |
| 构建范式 | `tools/build.ps1`：csc + rsp 直编，绕开 MSBuild | **照抄**，只需改路径 |
| 离线 harness + 游戏内冒烟 | `LuaSmokeTests` 51/51、`quicktest` 报告 | **照抄范式**给我们的校验器/预算器做回归 |

> 结论：**不需要改 RimLua 一行代码。** 我们只用它的 public 运行时（`LuaRuntime`、`LuaScriptLoader`）与注册接口，脚本源码走内存、返回值走自有 JSON 通道。

### 2.2 Stellaris —— 三处实货 + 一条硬约束

| 价值 | 资产 | 位置 | 用途 |
|---|---|---|---|
| 高 | `PlanetIdentityUtility` 稳定 ID（`{name}_{defName}_{Guid10}`） | `PlanetIdentityUtility.cs:25` | 跨星球引用的一切 key |
| 高 | `PlanetWorldManifest` + 原子写（temp/replace） | `PlanetWorldManifest.cs:256` | 跨行星记忆的唯一可靠落点（§3-A） |
| 高 | `SiteUtility.GenerateSite` → `SiteMaker.MakeSite` → `RegisterSite` → 信件 | `Utilities\SiteUtility.cs:138-199` | `site.create` 动作的执行后端 |
| 高 | `GenStep_ArchaeologicalSite` 六段式生成（清场/铺地/破壳/内墙/碎石/战利品/威胁/去雾） | `GenSteps\GenStep_ArchaeologicalSite.cs:33-63` | Tier 1 遗址生成器 |
| 中 | `ClosedTerraformingBiodomeLayout`(822)/`BiodomeFacilityBuilder`(655)/`ExtremeFactionBaseLayout` | `Biodome/`、`FactionBases/` | 房间切分与通路连接的可抄实现 |
| 中 | `ArchaeologySiteRecord` 通用 flag/counter 状态机 | `StellarisArchaeologyManager.cs:143,295` | 事件持久状态容器（比新造组件安全） |
| 中 | `BiodomeSalvageTeamPlanner` 三段式（纯规则规划器 + `IncidentWorker` + `MapComponent` 排程） | `IncidentWorker_BiodomeSalvageTeam.cs` | 「AI 事件触发器」的骨架，纯规则部分可离线单测 |
| 低 | Pawn 后处理（`TrySetSkillLevel`/`TryWearApparel`/`ConfigureCorpsePawnForRole`） | `GenStep_FusionReactorArchaeologicalSite.cs:690-825` | 全是 private，**只能照抄不能直调** |
| 禁用 | `SiteMapData` 预制存档、`ShipMapData`、`PrefabMapLoader` | — | 预制序列化已停用（`ShipTransporter.cs:35`） |

> **硬约束**：Stellaris 是 1.6，且工程在 1.7 上编不过。所以 R1 **不依赖 Stellaris**，先把 AI 叙述者做成独立单世界 Mod；跨行星与遗址对接放 P3a/P5，届时顺手把需要的那几个文件移植到 net10.0。

### 2.3 RimMind —— 当前无法复用

| 项 | 结论 |
|---|---|
| 源码 | 本机无；GitHub 不通；公开搜索无镜像、无 release 包、无 Gitee |
| 《指导》点名的 4 个类 + `RimMindIncidentSelector` | 公开资料**查无实据**（多轮中英文精确检索 0 命中），不能凭名字反推 API |
| 许可 | 无法取得 `LICENSE` 原文，**MIT 待核实** |
| 可借鉴的设计（已证） | 异步请求队列（优先级/冷却/重试/取消）、OpenAI 兼容多供应商、上下文过滤器 28+ 项分三档、JSON 模式、跨模组 `SkipCheck` 互斥、AI 请求日志窗 |
| 已知设计坑（社区一手评价） | 「事件极端不均、只会重复刷野外营地」「缺少主循环与长期规划，几乎是 LLM 版随机发生器」「全局冷却导致商队/旅队两年不来」 |

**处理方式**：把所有模型访问收敛到 `ILlmGateway` 一个接口；P1 先落地自研 `OpenAiCompatibleGateway`（约 350 行），拿到源码后再补 `RimMindGatewayAdapter`（约 1 人日）。这样**源码缺失不阻塞开工**，只是少了「白捡 350 行」。

### 2.4 官方 1.7 API —— 不用自己造叙述者

`StorytellerDef` + 自定义 `StorytellerComp.MakeIntervalIncidents` 在 1.7 依然成立（§1 第 4 条）。这意味着「AI 决定何时发事件」可以走原生管线，不需要 Harmony 全局拦截，天然避开《指导》§3.1 警告的「无差别拦截其他模组事件」。

---

## 3. 对《指导》的 4 条修正建议

### 3-A（必改）跨行星记忆不能放 `GameComponent`

《指导》§4.3/§9.1 写「银河级状态放在 GameComponent 或项目现有独立全局管理器中」。但本机实测：Stellaris 的每颗行星是**独立存档文件**，切换行星 = `SaveGame(A)` + `LoadGame(B)`。`GameComponent` 属于当前存档，**换行星就换了一份**，银河记忆会丢。

**修正**：星系级叙事状态走外部清单，与 `PlanetWorldManifest` 同目录、同 campaign 分片：

```
Config/Stellaris/Stellaris_PlanetWorldManifest_<campaignId>.xml     ← 现有，只读
Config/Stellaris/Stellaris_AIStoryGalaxy_<campaignId>.json          ← 新增，我们写
```

行星级（单存档内）状态才用 `GameComponent`。两层职责分开：外部文件只存「跨行星事实摘要 + 线索 + 事件链游标」，体积可控（<256KB）。

### 3-B（建议）Site 从「自由几何」降级为「模板族 + 参数化」

《指导》§7 要求 AI 设计拓扑 → C# 几何求解 → 验证可玩性。这是全集里最贵、且**没有任何现成实现可抄**的一块（Stellaris 的布局器是三个硬编码主题，不是通用求解器）。而它的边际收益主要来自「同一主题拓扑不同」，玩家感知最强的其实是**叙事层**（谁死在这、发生了什么、我该选什么）。

**三级阶梯**（建议只走到 Tier 1）：

| Tier | 做法 | 新增工作量 | 效果 |
|---|---|---|---|
| **Tier 0** | 纯原生 GenStep（`AncientComplex`/`AncientReactor`），AI 只写叙事与角色 | 0 | 地图是原版脸，叙事全新 |
| **Tier 1（推荐）** | `Stellaris_GenStep_AISite` 读冻结蓝图；蓝图 = **布局档案（5–8 种）+ 参数（尺寸/损毁度/威胁/战利品/入口方向/房间数）**；从 Stellaris 的六段式生成器移植 | +6 人日 | 同一主题能有明显不同的遗址，"AI 设计过"可感知 |
| Tier 2 | 《指导》§7 完整版：区域拓扑图 + 几何求解 + 可解锁路径验证 | +12–16 人日 | 真·自由设计，但调试成本极高 |

**修正**：《指导》§12 的 M3 验收（「同一主题三种拓扑明显不同的遗址」）用 Tier 1 即可满足字面要求；只有「AI 输出房间坐标与轮廓」这类条款才需要 Tier 2。建议 Tier 2 单独立项。

### 3-C（不做）不 fork RimLua

RimLua 是 0.1.0-m0，改它会把「上游更新」变成负债。§4.4 的方案在零改动下就能拿到结构化返回与自定义预算。只有在 P2 压力测试发现「墙钟预算不够用」时，才考虑给它加一个约 200 行的 `CallSource(source, ctx, state, budget) → ReturnJson`。**默认不做**。

### 3-D（简化）双层规划压成两段式调用

《指导》§4.1 的「战略规划 / 战术响应」双循环，在 MVP 会变成两次模型调用、两份 prompt、两套失效处理。建议：

- **一次 Director**：每次调度只调一次，输出「休整 / 发原版事件 / 写新剧情」三选一（含 `no_event`）。
- **一次 EventWriter**：仅当选了「写新剧情」时再调，输出 Lua 脚本 + 元数据；校验失败只允许一轮修复。
- 战术响应降级为「信号驱动的重规划」：`pawn_died`、`site_entered`、`choice_selected` 等信号只修改**已有事件实例的状态机**，不再额外调模型（Lua 脚本自己决定下一步）。

这样每次叙事只需 1–2 次 API 调用，成本和延迟都可控。

---

## 4. 目标架构

### 4.1 目录与程序集

```
Mods\AIIncidents\
  About\About.xml                       # packageId: Feng.AIStoryteller（待定，见 D3）
  LoadFolders.xml
  Defs\
    StorytellerDefs\Stellaris_AIStoryteller.xml
    StorytellerDefs\Stellaris_AIStoryteller_Fallback.xml   # 带原版 comps 的兜底版
    GenStepDefs\Stellaris_AISite.xml                       # P3a
    SitePartDefs\Stellaris_AIResearchSitePart.xml          # P3a
    MapGeneratorDefs\Stellaris_AISiteMap.xml               # P3a
  Languages\ChineseSimplified\...
  1.7\Assemblies\StellarisAI.dll
  Source\StellarisAI\
    StellarisAI.csproj                  # net10.0, 引 Assembly-CSharp/Waylith/UnityStubs/RimLua
    Core\           # Ids, Result, Json, Log, Diag
    Providers\      # ILlmGateway, OpenAiCompatibleGateway, (RimMindGatewayAdapter)
    Director\       # DirectorLoop, BudgetPolicy, TensionMath, ProposalParser
    Context\        # SnapshotBuilder, SnapshotDto, NarrativeMemory
    Events\         # EventDefinition, EventInstance, SignalBus, StateMachine, IdempotencyLog
    Lua\            # LuaStageRunner, LuaScriptCache, LuaJsonBridge
    Actions\        # IActionHandler, ActionRegistry, Handlers\*
    Scenes\         # SceneBlueprint, ArchetypeLibrary, SiteCompiler, BlueprintValidator   (P3a)
    Pawns\          # PawnBlueprint, PawnPostProcessor                                     (P4)
    Persistence\    # AiStorytellerGameComponent, GalaxyArchive                            (P5)
    UI\             # SettingsUI, Dialog_StoryChoice, Window_Diagnostics
    Patches\        # Harmony: 站点进入/Pawn 死亡/选择回调
  Tests\StellarisAI.Harness\            # net10.0 离线验证器（不引用 Verse）
  Docs\                                 # ARCHITECTURE / ACTION_REGISTRY / SCHEMA / SAVE_FORMAT
  Licenses\                             # THIRD_PARTY_NOTICES.md + 各许可原文
  tools\build.ps1                       # 照抄 AIStory\tools\build.ps1
```

程序集名 `StellarisAI.dll`，根命名空间 `Stellaris.AI`（满足《指导》§1.3 的 `Stellaris.*`）。与 `Stellaris.dll` 同根命名空间但类型名不重叠，可共存。

### 4.2 线程模型（不变量 A 的落地）

```
[主线程] GameComponentTick (每 60 tick)
    ├─ 构建快照快照（纯 DTO，<1ms，不序列化 Pawn/Map 对象）
    ├─ 投递到 LlmScheduler（单槽，concurrency=1）
    └─ DrainResults(): 取回结果 → 核对 epoch/worldId/snapshotVersion → 过期即弃

[后台线程] Task.Run + CancellationToken
    ├─ HTTP POST（超时 45s，429/5xx 退避，最多 2 次重试）
    └─ 结果入 ConcurrentQueue（只放字符串与 DTO）

[主线程] DrainResults 的后续
    ├─ 解析/校验（Schema、权限、预算、引用存在性）
    ├─ Lua 阶段执行（LuaRuntime.CallFunction，主线程，带预算）
    ├─ 动作执行（IActionHandler，主线程）
    └─ 更新 EventInstance / 记忆 / 幂等日志
```

三条铁律：
1. 后台线程**只碰** `string` 与 DTO，绝不碰 `Find.*`。
2. 每次请求带 `epoch`（每次 `ProgramState.LoadedGame` 自增），回来时 epoch 不符直接丢弃。
3. 所有副作用都有 `eventInstanceId + opId`，重复执行被幂等日志拒绝。

### 4.3 叙述者接入

```xml
<StorytellerDef ParentName="BaseStoryteller">
  <defName>Stellaris_AIStoryteller</defName>
  <label>AI 叙事者</label>
  <comps>
    <li Class="Stellaris.AI.StorytellerComp_AIDirector">
      <props><tickInterval>60</tickInterval></props>
    </li>
  </comps>
</StorytellerDef>
```

`StorytellerComp_AIDirector.MakeIntervalIncidents(target)` **只做一件事**：把「已批准、已到点」的事件从队列里取出并向引擎返回 `FiringIncident`。它**不**调用模型、**不**决定时机。

时机的来源是另一个入口：`GameComponentTick` 里的 `DirectorLoop`（先问模型「要不要、要什么」，批准后写入队列并设 `fireAtTick`，到点后由叙述者取出）。这样：
- 与原版叙述者管线同构，不 patch `Storyteller`，不干扰其他模组；
- 每颗行星新建 `Storyteller` 实例时，comp 是无状态壳，状态全在 `GameComponent`，天然支持重建（§1 第 14 条）；
- 另出一个 `Stellaris_AIStoryteller_Fallback`（`_RandomMain` + 我们的 comp），给「API 挂了但玩家不想空窗」的玩家用；由设置项在开局选择。

### 4.4 Lua 通道（零 RimLua 改动）

RimLua 的限制是「`CallScript` 只回 `ReturnText`」。绕过方案：

```lua
-- AI 产出的脚本：一个纯函数，输入 ctx/state 表，输出 JSON 字符串
function run(ctx, state)
  local actions = {}
  local s = state
  if ctx.trigger == "start" then
    actions[#actions+1] = { opId = "a1", type = "story.send_letter",
                            args = { title = "异常信号", text = "轨道扫描发现地下信号。" } }
    s.stage = "await_entry"
  elseif ctx.trigger == "site_entered" and s.stage == "await_entry" then
    actions[#actions+1] = { opId = "a2", type = "story.show_choices",
                            args = { prompt = "反应堆仍在低鸣。", options = {
                              { id = "cool",   label = "尝试冷却" },
                              { id = "leave",  label = "撤离" } } } }
    s.stage = "await_choice"
  end
  s.fired = (s.fired or 0) + 1
  return rim.aist.json_encode({ actions = actions, next_state = s })
end
```

C# 侧：

```csharp
Dictionary<string, object> ctx   = SnapshotToPlain(ctxJson);   // 树
Dictionary<string, object> state = StateToPlain(stateJson);   // 树
LuaCallResult r = LuaRuntime.CallFunction(script, "run", new object[]{ctx, state},
                                          wallClockBudgetMs: 25, sliceInstructionBudget: 2000);
if (!r.Ok) -> 阶段失败（不落任何副作用）
StagePlan plan = JsonSerializer.Deserialize<StagePlan>(r.ReturnText);   // 结构化
```

关键点（都已核对源码）：
- `LuaValueConverter.FromClr` 会**递归**把 `IDictionary`/`IEnumerable`/数值/字符串/布尔 转成 Lua 表 —— 所以 `Dictionary<string,object>` 树可以安全传入（`LuaValueConverter.cs:160-178`）。
- `rim.aist.json_encode` 是我们用 `[LuaFunction]` 注册的，参数类型 `Table`，直接把 Lua 表转 JSON 字符串（`IsSupportedParameter(Table)=true`，`ToClr` 返回 `value.Table`）。**不需要跨 Script 建表**。
- 预算用 `LuaRuntime.CallFunction(..., wall, slice)` 自己传，不受 RimLua 全局设置影响；`Slices` 可反推指令数（每片 2000）。
- 脚本源码**只在内存**编译（`CreateScript` + `InstallGlobals` + `RunChunk`），不落盘、不走文件监视器，无热重载竞态。

### 4.5 存档

单一 JSON 包，用 `Scribe_Values.Look(ref json, "stellarisAI")` 整包存取（与 RimLua 同款做法），内部再带 `schemaVersion` 做迁移：

```json
{ "schemaVersion": 1, "epoch": 42, "director": {"tension": 0.42, "nextEligibleTick": 380000},
  "instances": [ {"id":"evt_0007","defId":"Stellaris_AIReactorRuin","scriptHash":"sha1:...",
                  "stage":"await_choice","state":{...},"executed":["a1","a2"],
                  "timers":[{"at":385000,"signal":"game_timer_due"}]} ],
  "log": [ {"t":380000,"kind":"outcome","text":"..."} ] }
```

约束：单包 ≤512KB；`log` 只保留最近 200 条 + 滚动摘要；**不存 API Key**（进 Mod 设置）；**不存 Lua VM**（读档后按 `source` 重编译）。

---

## 5. 契约

### 5.1 C# 关键接口

```csharp
public interface ILlmGateway {
    Task<LlmResult> CompleteAsync(LlmRequest req, CancellationToken ct);   // 后台线程
}

public interface IActionHandler {
    string OpType { get; }            // "story.send_letter"
    PermissionLevel Permission { get; }   // P0..P3
    ValidationResult Validate(PlannedAction a, ActionContext ctx);  // 纯函数、可离线测
    ExecutionResult Execute(PlannedAction a, ActionContext ctx);    // 仅主线程
}

public interface ISnapshotSource {      // 主线程
    string BuildSnapshotJson(AiTarget target, BudgetPolicy budget);
}
```

### 5.2 Action 注册表 v1

| opType | 权限 | 参数 | 预算/上限 | 失败处理 |
|---|---|---|---|---|
| `story.send_letter` | P0 | title≤80, text≤1200, letterDef | 1/阶段 | 跳过并记日志 |
| `story.show_choices` | P0 | prompt≤600, options[2..4]{id,label≤40} | 1/阶段 | 回退为普通信件 |
| `story.remember` | P0 | key, text≤400, tags | 8/阶段 | 忽略 |
| `story.set_tension` | P0 | delta∈[-0.5,0.5] | 1/阶段 | 忽略 |
| `incident.fire` | P1 | incidentDefName, points, targetKind | 验证 `CanFireNow`，1/阶段 | 拒绝，事件转 `Failed` |
| `pawn.spawn` | P1 | pawnKindDef, factionHandle, cell, count≤4 | 计入 PawnEditBudget | 部分失败即回滚已生成者 |
| `thing.spawn` | P1 | thingDef, count≤20, qualityRange | 计入 RewardBudget | 同上 |
| `site.create` | P1 | sitePartDef, tile/worldId, archetypeId, params | 1/阶段（P3a） | 拒绝 |
| `map.damage_area` | P2 | rect, damageDef, points | P4；DangerBudget | 拒绝 |

白名单外一律拒。权限与预算由 C# 注册表硬编码，**Lua 无法自称高权限**。

### 5.3 事件状态机

| 状态 | 进入条件 | 下一步 |
|---|---|---|
| `Scheduled` | 导演批准、写入 `fireAtTick` | 到点 → `Ready` |
| `Ready` | 到点且目标（map/site/pawn）仍合法 | 校验 → `Validating` |
| `Validating` | Schema / 权限 / 预算 / 引用全部通过 | 通过 → `Active`；任一失败 → `Rejected` |
| `Active` | 动作逐个执行（幂等） | 需等待 → `Waiting`；结束 → `Completed` / `Failed` |
| `Waiting` | 等 timer、玩家选择、Pawn 死亡等信号 | 信号到达 → `Active`；超时 → `Failed` |
| `Rejected` / `Completed` / `Failed` | 终态 | 写入记忆与审计，释放句柄 |

每阶段独立执行、独立保存；禁止把挂起的 Lua 协程当状态（RimLua 本身也禁 `coroutine`，天然契合）。

---

## 6. WBS 与验收

| 阶段 | 交付物 | 验收标准（可自动判定优先） | 人日 |
|---|---|---|---|
| **P0 骨架** | SDK-style 工程 + `build.ps1` + `StorytellerDef` + 空 `StorytellerComp` + 设置页 + 日志前缀 + 离线 harness 空壳 | 游戏能起、能选到叙述者、无红字；`Log` 前缀可 grep；`build.ps1` 一条命令出 dll | 2 |
| **P1 导演** | `ILlmGateway` + `OpenAiCompatibleGateway` + 快照构建 + `DirectorLoop` + `incident.fire` 走原版 `IncidentWorker` | ① AI 能选合法事件或明确 `no_event`；② 断网/401/429 不崩、不卡主线程（帧时间无尖峰）；③ 同一事件不重复触发；④ 快照 JSON 有 schema 校验 | 6 |
| **P2 Lua 事件运行时** | `LuaStageRunner` + `json_encode` 桥 + `ActionRegistry` + 校验器 + 幂等日志 + 状态机 + 存读档 + 信件/选项 | ① 不给任何专用 C# 事件类，AI 生成的三阶段事件能跑通；② 死循环/超预算/越权/坏 JSON 被拒且游戏继续；③ 每阶段存读档后能续；④ 重复 signal 只生效一次 | 10 |
| **P3a 遗址（Tier 1）** | `SceneBlueprint` + 档案库（5–8 种）+ `Stellaris_GenStep_AISite` + 校验 + 降级回退 | ① 同主题三种明显不同遗址；② 入口可达、门/屋顶/电可用；③ 生成失败降级为 Tier 0 且记审计 | 6 |
| **P4 Pawn + 现场编辑** | `PawnBlueprint` + 后处理器 + 关系 + 尸体 + `map.damage_area` | ① 属性与叙事一致；② 真尸体可存读档；③ 不误改玩家派系/意识形态/工作 | 7 |
| **P5 多行星** | 外部星系清单对接 + 世界切换时事件挂起/迁移 + 跨星球线索 | A 星发现线索 → 旅行 B 触发后续 → 回 A 状态正确；多次存读档无重复 | 6 |
| **P6 质量门禁** | 安全用例集、成本/频率配置、日志脱敏、许可清单、发布包 | 回归全绿；恶意脚本 20 例全部被拦；许可文件齐 | 5 |

**R1（建议第一版）= P0+P1+P2+P3a ≈ 24 人日。** 交付即「AI 叙述者能自主调度、能写多阶段 Lua 剧情、能生成参数化遗址」——《指导》§14 端到端场景的可玩版，只差 Tier 2 的自由几何。

> 人日口径：一名熟悉 RimWorld Mod + C# 的开发者的净工作日；以 AI 结对推进，我预计日历天数约 0.4–0.6×。

---

## 7. 里程碑与关键路径

```
P0 ──> P1 ──> P2 ──┬──> P3a ──> P4 ──> P5 ──> P6
                   └──(可并行: P5 的多行星清单对接在 P2 后即可开工)
```

- **M1 可演示点**（P0+P1 结束，约 8 人日）：选 AI 叙述者 → 它自己决定发一次原版袭击 → 日志里能看到完整 prompt 与决策。
- **M2 可演示点**（P2 结束，约 18 人日）：一次 AI 自写的三阶段剧情，含信件、选项、分支、跨存读档续接。
- **M3 可演示点**（P3a 结束，约 24 人日）：AI 生成遗址 + 剧情《反应堆残响》，端到端可玩。

---

## 8. 风险登记

| # | 风险 | 概率/影响 | 对策 |
|---|---|---|---|
| R1 | RimMind 源码拿不到，「复用」退化为「照设计重写」 | 高/中 | `ILlmGateway` 隔离，自研网关 350 行兜底；工期 +2–3 人日 |
| R2 | RimLua 是 0.1.0-m0，public 运行时可能变 | 中/中 | 全部调用收敛到 `Lua/` 一个目录（5 个调用点）；pin 版本；加冒烟测试 |
| R3 | RimLua 无总指令数/内存上限，恶意脚本可能吃满主线程 | 中/中 | 我们自己的四道闸：源码≤64KB、动作≤32、状态≤32KB、墙钟 25ms；后续可给 RimLua 加 200 行 |
| R4 | Stellaris 1.6 在 1.7 编不过 | 高/中 | R1 不依赖 Stellaris；P3a 只移植需要的那 4 个文件到 net10.0 |
| R5 | 自由几何求解器（Tier 2）调试黑洞 | 高/高 | 不进 R1；单独立项，先做离线验证器 |
| R6 | 每行星独立存档 → 银河记忆丢失 | 确定/高 | §3-A：外部清单，与 `PlanetWorldManifest` 同目录同 campaign 分片 |
| R7 | 模型成本/延迟不可控 | 中/中 | 每次叙事 1–2 次调用；全局最小间隔、每日上限、maxTokens 可配；离线时休整模式 |
| R8 | 生成内容违规/提示词注入 | 中/中 | 快照只出结构化事实；模型输出当**数据**，绝不拼进指令；动作白名单 + 数值范围 |
| R9 | 存档膨胀 | 低/中 | 单包 512KB 上限；日志滚动裁剪；脚本按 hash 去重 |
| R10 | 与 Stellaris 同根命名空间冲突 | 低/低 | 程序集名 `StellarisAI`，类型名全部带 `AI` 段 |

---

## 9. 许可与合规

| 组件 | 许可 | 我们的义务 |
|---|---|---|
| RimMind-Core / Storyteller | **MIT（待核实）** | 若复制代码：保留版权行 + `Licenses/RimMind-*.txt` + `THIRD_PARTY_NOTICES.md` 记录来源 commit 与改动范围 |
| MoonSharp | BSD-3-Clause | 由 RimLua 分发，我们不打包；发布说明里指向 RimLua |
| RimLua | 自有 | 只引用、不打包 |
| Stellaris | 自有 | 移植代码进 `Licenses/PROVENANCE.md` 记原文件路径 |

**注意**：《指导》§11.1 说 RimMind 是 MIT，但我拿不到 LICENSE 原文，无法证实。在你提供源码之前，任何「复用 RimMind」的表述都只能是**设计参考**，不是代码引用。

---

## 10. 需要你拍板

| # | 决策 | 我的建议 |
|---|---|---|
| **D1** | **能否把 RimMind-Storyteller / RimMind-Core 的源码放到本机？**（本机 GitHub 不通，无镜像） | 能拿到就放 `D:\RimWorldMods\RimMind\`，我按 §2.3 的清单逐项核对可复用类与许可；拿不到就走自研网关，工期 +2–3 人日 |
| **D2** | **首个里程碑范围**：R1（P0–P2 + P3a，24 人日）还是直接冲《指导》§7 自由几何？ | R1。先拿到「可玩 + 可验收」，再决定要不要 Tier 2 |
| **D3** | **Mod 身份**：目录沿用 `AIIncidents`？`packageId` 用 `Feng.AIStoryteller`？显示名「Stellaris AI 叙述者」？是否声明依赖 `Feng.RimLua` 与 `brrainz.harmony`？ | 沿用目录；`Feng.AIStoryteller`；依赖 `Feng.RimLua` + `brrainz.harmony`，**不**依赖 Stellaris |
| **D4** | 允许改 RimLua 吗？ | 默认不改（§3-C）；只在 P2 压测证明墙钟预算不够时再加 200 行 |
| **D5** | 默认供应商与预算：DeepSeek / OpenAI 兼容端点？每游戏日调用上限？ | 缺省 OpenAI 兼容（baseUrl 可配），默认每 30 分钟最多 1 次、每日 40 次，全部可配 |
| **D6** | 选项弹窗（`story.show_choices`）进不进 R1？ | 进。它是「叙事有后果」的最低成本体现，约 1.5 人日 |

---

## 附录：审核通过后的第一批动作（P0，约 2 人日）

1. 建 `StellarisAI.csproj`（net10.0，引 `Assembly-CSharp`/`Waylith`/`UnityStubs`/`RimLua`，`Private=false`）。
2. 照抄 `AIStory\tools\build.ps1`，SDK 指 `PioneerFrameworkLite\.zcode\dotnet10`（10.0.13）。
3. `About.xml`（依赖 RimLua + Harmony）+ `LoadFolders.xml`（v1.7）。
4. 两个 `StorytellerDef` + 空 `StorytellerComp_AIDirector`（返回空集合）。
5. `AiStorytellerGameComponent`（空 Tick + 日志）+ Mod 设置页（endpoint/model/key/开关）。
6. 游戏内验证：选叙述者、无红字、`AIStoryteller` 前缀日志可见。
7. 提交 P0 验收报告（含 `Player.log` 截图与 `build.ps1` 输出）。
