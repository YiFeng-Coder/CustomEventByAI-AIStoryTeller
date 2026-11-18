# AI Narrator（AI 叙述者）

由外部大语言模型担任"叙事导演"的 RimWorld 模组：模型自己决定何时出事、出什么事，并可用 Lua 现场编写多阶段剧情（信件、选项、分支、定时器）。

- **PackageId**：`Feng.AINarrator`
- **版本**：0.6.0
- **支持游戏版本**：RimWorld 1.7
- **前置**：Harmony、RimLua（`Feng.RimLua`）

## 玩法概览

- 模型根据当前殖民地快照（人物、关系、地图、库存、近期事件）决定是否触发事件，而不是按固定节奏抽卡。
- 事件可用 Lua 现场编排：多阶段、带选项、带分支、带定时器。
- 0.6.0 起动作库扩展到 **42 个**：除叙事与地图外，还能改变角色想法与关系、留下可见现场（废墟 / 血迹 / 兽群），并给出叙事目标。
- 模型供应商（DeepSeek、OpenAI、Kimi、智谱 GLM、通义千问、SiliconFlow、OpenRouter、Ollama 等）、模型与调用预算均在游戏内设置页配置，API Key 保存在本地存档配置中，不随仓库分发。

## 本地化

- **英文是默认文本**：Def、界面、信件与提示词的英文直接写在源码/Def 里，英文玩家开箱即用；
- **简体中文**在 `Languages/ChineseSimplified/`，游戏语言为中文时自动覆盖；
- **叙述语言跟随游戏语言**：中文游戏里模型写中文剧情，其它语言写英文，不需要单独开关；
- 新增语言只要加一个 `Languages/<语言>/Keyed/` 目录，约定见 [本地化](Docs/本地化.md)。

## 安装

1. 将本目录整个放入 `RimWorld/Mods/AIIncidents`。
2. 确认已安装 Harmony 与 RimLua。
3. 启动游戏，在"选项 → 模组设置 → AI Narrator"里填入模型供应商与 API Key。

`LoadFolders.xml` 会把根目录与 `1.7/` 一起加载，预编译程序集位于 `1.7/Assemblies/AINarrator.dll`。

## 目录结构

| 路径 | 说明 |
| --- | --- |
| `About/` | 模组元数据（`About.xml`） |
| `Defs/` | 叙述者 Def（`Stellaris_AINarrator.xml`，默认英文） |
| `Languages/` | 语言文件（`English/`、`ChineseSimplified/`） |
| `tools/check_loc.py`、`tools/check_cjk.py` | 本地化自检脚本 |
| `1.7/Assemblies/` | 编译输出 `AINarrator.dll` |
| `Source/AINarrator/` | C# 源码（net10.0，命名空间 `Stellaris.AI`） |
| `tools/` | 构建与文档工具 |
| `Docs/` | 设计与交付文档（Markdown + Word） |

## 从源码构建

仓库不含游戏程序集，需要本机已安装 RimWorld。

```powershell
powershell -ExecutionPolicy Bypass -File tools/build.ps1
# 或指定游戏目录
powershell -ExecutionPolicy Bypass -File tools/build.ps1 -GameDir "D:\SteamA\steamapps\common\RimWorld"
# 仅做编译检查
powershell -ExecutionPolicy Bypass -File tools/build.ps1 -CheckOnly
```

脚本直接调用 Roslyn 的 `csc.dll`（不走 MSBuild），输出到 `1.7/Assemblies/AINarrator.dll`。也可以直接用 MSBuild / `dotnet build` 构建 `Source/AINarrator/AINarrator.csproj`，`GameDir` 属性指向游戏安装目录。

## 文档

- [P0-交付说明](Docs/P0-交付说明.md)
- [P1-交付说明](Docs/P1-交付说明.md)
- [P2-交付说明](Docs/P2-交付说明.md)
- [P3-动作扩展计划](Docs/P3-动作扩展计划.md)
- [P4-动作扩展计划_v2](Docs/P4-动作扩展计划_v2.md)
- [P5-Def检索交付说明](Docs/P5-Def检索交付说明.md)
- [编剧提示词](Docs/编剧提示词.md)
- [动作库参考](Docs/动作库参考.md)
- [本地化](Docs/本地化.md)

## 许可

[MIT](LICENSE)
