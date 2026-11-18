using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace Stellaris.AI
{
    /// <summary>
    /// 调试窗：看最近一次请求的 prompt、回复、判定结果，以及配额与状态。
    /// 只在开发与验收时用，不影响运行时逻辑。
    /// </summary>
    public class Window_NarratorDebug : Window
    {
        private Vector2 _scroll;

        public Window_NarratorDebug()
        {
            doCloseX = true;
            doCloseButton = false;
            resizeable = true;
            draggable = true;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = false;
            optionalTitle = "AI 叙述者 · 调试";
        }

        public override Vector2 InitialSize => new Vector2(920f, 700f);

        private string _concept = "";
        private string _defQuery = "item/药";
        private string _defQueryResult = "";

        private void RunDefQuery()
        {
            try
            {
                string raw = (_defQuery ?? "").Trim();
                int slash = raw.IndexOf('/');
                if (slash <= 0 || slash >= raw.Length - 1)
                {
                    _defQueryResult = "格式：kind/关键词，例如 item/药";
                    return;
                }

                var request = new DefLookupRequest
                {
                    Kind = raw.Substring(0, slash).Trim(),
                    Query = raw.Substring(slash + 1).Trim(),
                    Limit = AINarratorMod.Settings != null ? AINarratorMod.Settings.lookupMaxResults : 8
                };

                NarratorWorldComponent comp = NarratorWorldComponent.Instance;
                DefLookupResult result = DefQueryService.Search(request, Find.AnyPlayerHomeMap,
                    comp != null ? comp.State : null, AINarratorMod.Settings,
                    Find.TickManager.TicksGame);
                _defQueryResult = result.Ok ? result.Text : "查询失败：" + result.Error;
            }
            catch (System.Exception e)
            {
                _defQueryResult = "查询异常：" + e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>
        /// 完全绕过模型：直接注册并启动内置示例脚本。
        /// 用来单独验证 Lua 运行时、动作层与定时器是否正常——如果这条链能跑通，
        /// 出问题就一定在"模型写的脚本"或"请求链路"上。
        /// </summary>
        private void InjectSampleEvent()
        {
            string threat = SampleEventScript.PickThreatDefName();
            bool allowLong = AINarratorMod.Settings != null && AINarratorMod.Settings.enableLongEvents;
            string script = SampleEventScript.Build(threat, allowLong);

            if (!EventRuntime.RegisterDefinition(SampleEventScript.Title, script,
                    out EventDefinitionRecord definition, out string error))
            {
                Messages.Message("示例事件注册失败：" + error, MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (!EventRuntime.Start(definition, "start", null, out string startError))
            {
                Messages.Message("示例事件启动失败：" + startError, MessageTypeDefOf.RejectInput, false);
                return;
            }

            Messages.Message("示例事件已启动（不消耗任何 API 调用）", MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>离线演示新增动作：不调模型，直接注册并启动一份覆盖 P4 动作的脚本。</summary>
        private void InjectP4Demo()
        {
            string script = SampleEventScript.BuildP4Demo();

            if (!EventRuntime.RegisterDefinition(SampleEventScript.DemoTitle, script,
                    out EventDefinitionRecord definition, out string error))
            {
                Messages.Message("P4 演示注册失败：" + error, MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (!EventRuntime.Start(definition, "start", null, out string startError))
            {
                Messages.Message("P4 演示启动失败：" + startError, MessageTypeDefOf.RejectInput, false);
                return;
            }

            Messages.Message("P4 演示已启动（不消耗任何 API 调用）", MessageTypeDefOf.NeutralEvent, false);
        }

        public override void DoWindowContents(Rect inRect)
        {
            float y = inRect.y;

            // 顶部按钮条
            Rect buttons = new Rect(inRect.x, y, inRect.width, 32f);
            float bx = buttons.x;

            if (Widgets.ButtonText(new Rect(bx, buttons.y, 140f, 30f), "立即请求一次"))
            {
                if (!DirectorLoop.ForceRequest())
                {
                    Messages.Message(DirectorLoop.LastStatus, MessageTypeDefOf.RejectInput, false);
                }
            }

            bx += 148f;
            bool luaEnabled = AINarratorMod.Settings == null || AINarratorMod.Settings.enableLuaEvents;
            Rect writerButton = new Rect(bx, buttons.y, 140f, 30f);

            // 设置里关掉 Lua 剧情后，这个按钮置灰——ForceWriter 自己也会拒绝，这里只是别让玩家点了才看到报错。
            bool prevEnabled = GUI.enabled;
            GUI.enabled = prevEnabled && luaEnabled;
            if (Widgets.ButtonText(writerButton, "强制写 Lua 剧情"))
            {
                if (!DirectorLoop.ForceWriter(_concept))
                {
                    Messages.Message(DirectorLoop.LastStatus, MessageTypeDefOf.RejectInput, false);
                }
            }
            GUI.enabled = prevEnabled;

            if (!luaEnabled)
            {
                TooltipHandler.TipRegion(writerButton, "已在 Mod 设置里关闭 Lua 剧情事件");
            }

            bx += 148f;
            if (Widgets.ButtonText(new Rect(bx, buttons.y, 140f, 30f), "注入示例事件"))
            {
                InjectSampleEvent();
            }

            bx += 148f;
            if (Widgets.ButtonText(new Rect(bx, buttons.y, 110f, 30f), "重扫候选表"))
            {
                CandidateTable.Rebuild();
            }

            bx += 118f;
            if (Widgets.ButtonText(new Rect(bx, buttons.y, 100f, 30f), "清空历史"))
            {
                NarratorWorldComponent comp = NarratorWorldComponent.Instance;
                if (comp != null)
                {
                    comp.State.history.Clear();
                    comp.State.pending.Clear();
                }
            }

            bx += 108f;
            if (Widgets.ButtonText(new Rect(bx, buttons.y, 140f, 30f), "注入 P4 演示"))
            {
                InjectP4Demo();
            }

            bx += 148f;
            if (Widgets.ButtonText(new Rect(bx, buttons.y, 80f, 30f), "关闭"))
            {
                Close();
            }

            // 第二行：强制编剧用的概念输入
            y += 36f;
            Widgets.Label(new Rect(inRect.x, y + 6f, 150f, 24f), "剧情概念（可留空）");
            _concept = Widgets.TextField(
                new Rect(inRect.x + 156f, y, inRect.width - 156f, 28f), _concept ?? "");

            // 第三行：Def 查询测试（不消耗 API 调用）
            y += 34f;
            Widgets.Label(new Rect(inRect.x, y + 6f, 150f, 24f), "Def 查询（kind/关键词）");
            _defQuery = Widgets.TextField(
                new Rect(inRect.x + 156f, y, inRect.width - 156f - 176f, 28f), _defQuery ?? "");

            if (Widgets.ButtonText(new Rect(inRect.xMax - 172f, y, 80f, 28f), "测试查询"))
            {
                RunDefQuery();
            }

            if (Widgets.ButtonText(new Rect(inRect.xMax - 88f, y, 84f, 28f), "重建索引"))
            {
                DefIndex.Rebuild();
                _defQueryResult = "DefIndex 已重建";
            }

            y += 34f;

            var sb = new StringBuilder();
            sb.Append("状态：").Append(DirectorLoop.LastStatus).Append('\n');
            sb.Append("阶段：").Append(DirectorLoop.PhaseName)
              .Append("　导演在途：").Append(DirectorLoop.DirectorBusy ? "是" : "否")
              .Append("　编剧在途：").Append(DirectorLoop.WriterBusy ? "是" : "否")
              .Append("　排队概念：").Append(DirectorLoop.HasQueuedConcept ? "1" : "0").Append('\n');
            sb.Append("殖民者保护：")
              .Append(AINarratorSettings.InfluenceLabel(AINarratorMod.Settings != null
                  ? AINarratorMod.Settings.colonistInfluence
                  : AINarratorSettings.InfluenceEverything))
              .Append('\n');
            sb.Append("候选总数：").Append(CandidateTable.Count)
              .Append("　动作总数：").Append(ActionRegistry.All.Count)
              .Append("　本次可见：").Append(PromptBuilder.LastMask).Append('\n');
            sb.Append("Def 查询：导演 ").Append(DirectorLoop.DirectorLookupStatus)
              .Append("　编剧 ").Append(DirectorLoop.WriterLookupStatus).Append('\n');
            if (!string.IsNullOrEmpty(_defQueryResult))
            {
                sb.Append("—— 测试查询 ——\n").Append(_defQueryResult).Append('\n');
            }

            NarratorWorldComponent component = NarratorWorldComponent.Instance;
            if (component != null)
            {
                NarratorState st = component.State;
                sb.Append("连续失败：").Append(st.failStreak)
                  .Append("　今日调用：").Append(st.callsToday).Append('/')
                  .Append(AINarratorMod.Settings != null ? AINarratorMod.Settings.maxApiCallsPerDay : 0)
                  .Append('\n');
                sb.Append("待发事件：").Append(st.pending.Count).Append('\n');
                sb.Append("配额：").Append(BudgetPolicy.Describe(st, AINarratorMod.Settings)).Append('\n');
                string objective = ObjectiveTracker.ActiveSummary(st);
                if (!string.IsNullOrEmpty(objective))
                {
                    sb.Append("当前叙事目标：").Append(objective).Append('\n');
                }
                sb.Append("最近事件史：\n").Append(st.RecentSummary(8)).Append('\n');
            }

            sb.Append("—— 最近一次 prompt ——\n");
            sb.Append(DirectorLoop.LastPrompt).Append('\n');
            if (!string.IsNullOrEmpty(DirectorLoop.LastWriterPrompt))
            {
                sb.Append("—— 最近一次编剧 prompt ——\n");
                sb.Append(DirectorLoop.LastWriterPrompt).Append('\n');
            }

            sb.Append("—— 最近一次回复 ——\n");
            sb.Append(DirectorLoop.LastResponse).Append('\n');
            sb.Append("—— 剧情运行时 ——\n");
            sb.Append(EventRuntime.LastStatus).Append('\n');
            sb.Append("最近阶段：").Append(EventRuntime.LastPlanSummary).Append('\n');

            if (component != null && component.State.instances.Count > 0)
            {
                sb.Append("活跃事件：\n");
                for (int i = 0; i < component.State.instances.Count; i++)
                {
                    EventInstanceRecord inst = component.State.instances[i];
                    sb.Append("- ").Append(inst.id)
                      .Append("　定义 ").Append(inst.defId)
                      .Append("　阶段 ").Append(string.IsNullOrEmpty(inst.stageName) ? "?" : inst.stageName)
                      .Append(inst.done ? "　[已结束]" : "")
                      .Append("　定时器 ").Append(inst.timers.Count)
                      .Append(string.IsNullOrEmpty(inst.lastError) ? "" : "　错误：" + inst.lastError)
                      .Append('\n');
                }
            }

            if (!string.IsNullOrEmpty(EventRuntime.LastScriptPreview))
            {
                sb.Append("—— 最近脚本（截断）——\n");
                sb.Append(EventRuntime.LastScriptPreview).Append('\n');
            }

            string text = sb.ToString();
            float viewWidth = inRect.width - 24f;
            float viewHeight = Mathf.Max(200f, Text.CalcHeight(text, viewWidth) + 20f);
            Rect viewRect = new Rect(0f, 0f, viewWidth, viewHeight);
            Rect scrollRect = new Rect(inRect.x, y, inRect.width, inRect.height - (y - inRect.y) - 4f);

            Widgets.BeginScrollView(scrollRect, ref _scroll, viewRect);
            Widgets.Label(viewRect, text);
            Widgets.EndScrollView();
        }
    }
}