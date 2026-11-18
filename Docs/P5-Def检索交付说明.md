# P5 · DefName 按需检索：交付说明

- 版本：0.6.0 之后的增量（未单独改 `About/About.xml` 的版本号）
- 对应计划：`Docs/计划案_Def检索工具_v0.1.md`
- 状态：**代码已落地，编译检查通过；游戏内验收请按 §5 跑一遍**
- 目标：不再把每类 Def 的静态/随机名单塞进提示词，改成"运行时索引 + 按需查询"

---

## 1. 交付了什么

### 1.1 新文件

| 文件 | 作用 |
|---|---|
| `Source/AINarrator/Defs/DefKind.cs` | `DefEntry` / `DefKind` / 固定类别表（item、weapon、incident…） |
| `Source/AINarrator/Defs/DefIndex.cs` | 按 kind 懒构建索引、确定性搜索、分页、渲染、目录、示例 |
| `Source/AINarrator/Defs/DefQueryService.cs` | `LOOKUP` 请求/结果、普通 Def 查询、incident 的"可触发 + 预算"查询、`@kind/query` 解析 |
| `Source/AINarrator/Events/DefArgResolver.cs` | 在校验前把 `@kind/关键词` 替换成真实 defName |
| `Source/AINarrator/Director/LookupSession.cs` | 一次导演/编剧请求内的查询轮状态 |
| `Source/AINarrator/Director/LookupProtocol.cs` | 编剧 `LOOKUP: {...}` 行解析、结果块格式 |

### 1.2 改动文件

| 文件 | 改动 |
|---|---|
| `Director/PromptBuilder.cs` | Def 目录替代全量名单；导演候选说明；`LOOKUP` 契约；`@` 说明 |
| `Director/DirectorLoop.cs` | 导演/编剧查询轮状态机；查询计入每日调用；保留修复重试上下文 |
| `Director/ProposalParser.cs` | 新增 `ProposalKind.Lookup` 与 `lookup` 字段 |
| `Context/CandidateTable.cs` | 去掉洗牌；新增 `SelectForPrompt`（预算允许 + 类别轮换 + 确定性） |
| `Events/EventRuntime.cs` | `RunStage` 在 `ValidateAll` 前调用 `DefArgResolver.Resolve` |
| `Settings/AINarratorSettings.cs` | 5 个新设置字段（含严格占位符调试开关） |
| `Core/AINarratorMod.cs` | 设置页新增"DefName 查询"区域 |
| `UI/Window_NarratorDebug.cs` | 显示查询轮状态；新增"测试查询 / 重建索引"（不消耗 API） |
| `Providers/MockGateway.cs` | 新增 `mock:lookup_*` 模式 |
| `Actions/BuiltinHandlers.cs` | `incident.fire` / `thing.spawn` / `pawn.spawn` 的 `Describe()` 不再说"必须出现在名单里" |
| `Docs/动作库参考.md` | 提示词机制说明（旧文档编码为 GBK，未整体重写） |

---

## 2. 现在模型怎么拿到 DefName

### 2.1 目录（不再枚举）

编剧 prompt 里是：

```text
【Def 目录】下面是每类的总数和几个示例（不是全部）。
需要具体名字时：单独输出一行
LOOKUP: {"kind":"item","q":"药","limit":8}
我会把匹配结果贴回来（最多 2 轮，剩余 2 轮）。
所有 Def 参数都可以写精确 defName，也可以写 @类别/关键词。

物品 item：共 1832 个；例：Steel(钢铁，Core)、MedicineIndustrial(工业药，Core)...
...
```

- **小类别（≤12 条）直接列全名**：灵感只有 8 种，只给 3 个示例会让模型看不到"手术灵感 / 创作灵感"，无法指定；屋顶、天气、技能等同样受益。
- 大类别仍给"总数 + 3 个跨模组示例"；示例按来源模组分组轮流取，不会全是 Core。
- 目录**不是白名单**；最终仍由 `ActionRegistry.ValidateAll` 与各 handler 的 `DefDatabase` 查询把关。

### 2.2 两条取用路径

**路径 A：`@kind/关键词`（零额外调用）**

```lua
args = { thingDef = "@item/药", count = 3 }
args = { defName = "@incident/虫", points_multiplier = 1.0 }
args = { pawnKind = "@animal/狼", count = 3 }
```

- 语法：`@类别/关键词`；类别见目录里的英文 id。
- 解析时机：每个阶段执行前、`ValidateAll` 之前。
- 默认取搜索结果第一名（确定性排序），并在调试日志记录"解析成了谁"。
- **`inspirationDef` 是例外：命中多种灵感时直接拒绝**并返回候选，要求写具体 DefName。灵感效果各不相同，"差不多"的自动选择会让玩家看到完全无关的灵感。
- 零匹配会让该阶段失败；`start` 阶段会走已有的"被拒重试一次"，后续阶段则和其他非法参数一样终止实例。
- 打开 `strictDefPlaceholders` 后，歧义（>1 个结果）不再自动选，而是把前 5 个候选写进错误。

**路径 B：`LOOKUP` 查询轮（最多 `maxLookupRounds` 轮）**

编剧单独输出一行：

```text
LOOKUP: {"kind":"item","q":"药","limit":8}
```

导演输出 JSON：

```json
{"decision":"lookup","lookup":{"kind":"incident","q":"虫","limit":8},"reason":"想看看有哪些虫灾"}
```

- 宿主在主线程执行查询，把结果块追加到 user prompt，再请求一次。
- 每次查询都是真实 API 调用，计入 `callsToday` 与 `maxApiCallsPerDay`。
- 查询轮用尽后，prompt 明确要求直接给最终结果；再输出 `LOOKUP` 会被拒。
- incident 查询只返回**此刻 `CanFireNow` 为真且预算允许**的事件。

### 2.3 导演候选

- 去掉 `CandidateTable.Firable()` 里的 `Rand` 洗牌。
- `SelectForPrompt`：先按点数/`CanFireNow`/预算过滤，再按 ThreatBig → ThreatSmall → Misc 轮换取前 16（每类最多约 5 个），类内按最低点数降序、defName 升序。
- 同一局势、同一预算得到同一份候选；模型想要更多类型时用 `LOOKUP`。

---

## 3. 设置

| 设置 | 默认 | 说明 |
|---|---|---|
| `enableDefLookup` | 开 | 允许 `LOOKUP` 查询轮 |
| `maxLookupRounds` | 2 | 每次导演/编剧请求最多几轮（≥0，不设上限） |
| `lookupMaxResults` | 8 | 单次返回条数（≥4，不设上限） |
| `legacyDefLists` | 关 | 回退到旧版全量名单，仅对比/应急 |
| `strictDefPlaceholders` | 关 | 调试：歧义时返回候选而不是取第一名 |

设置页在"叙事风格提示词"下面新增"DefName 查询（多模组友好）"一段。

> `maxLookupRounds` 与 `lookupMaxResults` 使用**数字输入框**，不是滑条：  
> 小数值（2 轮 / 8 条）用滑条很难精确点到，直接填数字更符合玩家习惯。  
> 只校验下限（轮数 ≥0，条数 ≥4），**不设上限**；清空输入框不会丢失原值。

---

## 4. 调试窗

新增：

- `Def 查询：导演 查询轮 1/2 · 最近 item/药 total=37　编剧 ...`
- 一行输入 `kind/关键词`（默认 `item/药`）+「测试查询」按钮：直接在主线程查，不消耗 API。
- 「重建索引」按钮：清空 `DefIndex` 缓存；开发者模式重载 Defs 后使用。

`Player.log` 里（`debugLogging` 开启时）会看到：

```text
[AI Narrator] DefIndex built item: 1832 entries in 12ms
[AI Narrator] resolve @item/药 -> MedicineIndustrial (37 matches)
[AI Narrator] writer lookup item/药 total=37
```

---

## 5. 游戏内验收清单

先备份存档，开调试日志。

1. **目录替换**
   - 填 `mock:story`，调试窗「强制写 Lua 剧情」。
   - 看 `LastWriterPrompt`：应是 `【Def 目录】` + 每类总数/示例，而不是 13~25 行全量名单。
   - 输出里不应出现 `【可用的 DefName 名单】`（除非开了 `legacyDefLists`）。

2. **`@kind/关键词` 零调用**
   - 用 `mock:story` 或手写概念让模型写一段 `thing.spawn{thingDef="@item/药"}` 的脚本。
   - 调试日志应出现 `resolve @item/药 -> ...`；脚本应通过校验并生成物品。

3. **编剧查询轮**
   - 接口地址填 `mock:lookup_story`，强制写剧情。
   - 第一次编剧回复应是 `LOOKUP: {"kind":"item","q":"药","limit":5}`；
   - prompt 里出现 `【查询结果 #1】`；第二次回复应是用结果中某个 defName 的脚本。
   - 调试窗显示 `编剧 查询轮 1/2`。

4. **查询轮上限**
   - 填 `mock:lookup_loop`，强制写剧情。
   - 应看到 2 轮查询后提示"查询轮不可用或已用尽"，最终被拒，不会无限请求。

5. **空结果 / 坏格式**
   - `mock:lookup_empty`：第一次查询无结果，模型应能用不带 Def 的脚本收尾。
   - `mock:lookup_bad`：`LOOKUP` JSON 非法，应回喂格式错误，且最多修复一次。

6. **导演 incident 查询**
   - 填 `mock:lookup_incident`，点「立即请求一次」。
   - 导演第一次返回 `decision=lookup`；第二次从查询结果里选一个事件；`LastStatus` 显示批准/休整。

7. **多模组**
   - 在 50+ 模组存档里，测试查询一个晚加载模组的物品（例如自定义"万灵药"）。
   - 目录里应出现该模组的示例；`LOOKUP`/`@item/万灵药` 应能命中；旧版全量名单不会包含它。

8. **回退**
   - 打开 `legacyDefLists`，编剧 prompt 恢复旧版名单；
   - 关闭 `enableDefLookup`，`LOOKUP` 不再被广告/接受，`@` 仍可用。

9. **回归**
   - `mock:story`、`mock:raid`、`mock:hallucinate`、`mock:garbage`、示例事件、P4 演示事件都应正常。
   - 旧脚本（精确 defName）行为不变。

---

## 6. 已知限制 / 后续

- **未在真实游戏里跑过**：本环境只做了 `tools/build.ps1 -CheckOnly` 编译检查；上面的 §5 需要在游戏内跑一遍。
- **`DefIndex` 首次构建**会扫描对应 `DefDatabase`；在 100+ 模组下首次可能几十毫秒。日志会打印耗时；如果某类 >100ms，再把该 kind 移到加载阶段预构建。
- **`incident` 查询**会对匹配项逐个 `CanFireNow` 检查；返回条数由玩家输入，不设上限；`total` 需要遍历候选，如果事件表很大且卡顿，可改成"只对前 N 个匹配做精确检查"。
- **自定义 incident 类别**仍受 `CandidateTable.AllowedCategories` 限制；本增量没有放开，避免影响预算池语义。
- **`@kind/query` 默认取第一名**：可能不是模型心里那个；想要精确挑选时用 `LOOKUP` 或精确 defName；需要严格行为时开 `strictDefPlaceholders`。
- **旧文档编码**：`Docs/动作库参考.md`、`Docs/编剧提示词.md` 是 GBK，当前工具按 UTF-8 读取会显示乱码；本次只新增了本文件，没有整体重写旧文档。

---

## 7. 回退

- `legacyDefLists = true`：编剧恢复旧版全量名单。
- `enableDefLookup = false`：关闭查询轮；`@kind/query` 仍可用。
- `@kind/query` 出问题时的最小回退：把设置里的 `strictDefPlaceholders` 打开观察，或临时用精确 defName 写脚本。
- 导演候选确定化如果体感"太固定"：把 `SelectForPrompt` 的类别轮换/每类上限调大，或恢复旧版 `Firable` 的洗牌（不建议，会重新引入不可复现与 `Rand` 消耗）。
