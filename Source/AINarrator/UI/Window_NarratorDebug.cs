using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace CustomEventByAI.AIStoryTeller
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
            optionalTitle = Loc.T("AI_Debug_Title");
        }

        public override Vector2 InitialSize => new Vector2(920f, 700f);

        private string _concept = "";
        private string _defQuery = Loc.Pick("item/药", "item/medicine");
        private string _defQueryResult = "";

        private void RunDefQuery()
        {
            try
            {
                string raw = (_defQuery ?? "").Trim();
                int slash = raw.IndexOf('/');
                if (slash <= 0 || slash >= raw.Length - 1)
                {
                    _defQueryResult = Loc.T("AI_Debug_QueryFormat");
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
                _defQueryResult = result.Ok ? result.Text : Loc.T("AI_Debug_QueryFailed", result.Error);
            }
            catch (System.Exception e)
            {
                _defQueryResult = Loc.T("AI_Debug_QueryException", e.GetType().Name, e.Message);
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
                Messages.Message(Loc.T("AI_Debug_SampleRegisterFailed", error), MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (!EventRuntime.Start(definition, "start", null, out string startError))
            {
                Messages.Message(Loc.T("AI_Debug_SampleStartFailed", startError), MessageTypeDefOf.RejectInput, false);
                return;
            }

            Messages.Message(Loc.T("AI_Debug_SampleStarted"), MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>离线演示新增动作：不调模型，直接注册并启动一份覆盖 P4 动作的脚本。</summary>
        private void InjectP4Demo()
        {
            string script = SampleEventScript.BuildP4Demo();

            if (!EventRuntime.RegisterDefinition(SampleEventScript.DemoTitle, script,
                    out EventDefinitionRecord definition, out string error))
            {
                Messages.Message(Loc.T("AI_Debug_DemoRegisterFailed", error), MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (!EventRuntime.Start(definition, "start", null, out string startError))
            {
                Messages.Message(Loc.T("AI_Debug_DemoStartFailed", startError), MessageTypeDefOf.RejectInput, false);
                return;
            }

            Messages.Message(Loc.T("AI_Debug_DemoStarted"), MessageTypeDefOf.NeutralEvent, false);
        }

        public override void DoWindowContents(Rect inRect)
        {
            float y = inRect.y;

            // 顶部按钮条
            Rect buttons = new Rect(inRect.x, y, inRect.width, 32f);
            float bx = buttons.x;

            if (Widgets.ButtonText(new Rect(bx, buttons.y, 140f, 30f), Loc.T("AI_Debug_RequestOnce")))
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
            if (Widgets.ButtonText(writerButton, Loc.T("AI_Debug_ForceWriter")))
            {
                if (!DirectorLoop.ForceWriter(_concept))
                {
                    Messages.Message(DirectorLoop.LastStatus, MessageTypeDefOf.RejectInput, false);
                }
            }
            GUI.enabled = prevEnabled;

            if (!luaEnabled)
            {
                TooltipHandler.TipRegion(writerButton, Loc.T("AI_Debug_LuaDisabledTip"));
            }

            bx += 148f;
            if (Widgets.ButtonText(new Rect(bx, buttons.y, 140f, 30f), Loc.T("AI_Debug_InjectSample")))
            {
                InjectSampleEvent();
            }

            bx += 148f;
            if (Widgets.ButtonText(new Rect(bx, buttons.y, 110f, 30f), Loc.T("AI_Debug_RebuildCandidates")))
            {
                CandidateTable.Rebuild();
            }

            bx += 118f;
            if (Widgets.ButtonText(new Rect(bx, buttons.y, 100f, 30f), Loc.T("AI_Debug_ClearHistory")))
            {
                NarratorWorldComponent comp = NarratorWorldComponent.Instance;
                if (comp != null)
                {
                    comp.State.history.Clear();
                    comp.State.pending.Clear();
                }
            }

            bx += 108f;
            if (Widgets.ButtonText(new Rect(bx, buttons.y, 140f, 30f), Loc.T("AI_Debug_InjectDemo")))
            {
                InjectP4Demo();
            }

            bx += 148f;
            if (Widgets.ButtonText(new Rect(bx, buttons.y, 80f, 30f), Loc.T("AI_Common_Close")))
            {
                Close();
            }

            // 第二行：强制编剧用的概念输入
            y += 36f;
            Widgets.Label(new Rect(inRect.x, y + 6f, 150f, 24f), Loc.T("AI_Debug_ConceptLabel"));
            _concept = Widgets.TextField(
                new Rect(inRect.x + 156f, y, inRect.width - 156f, 28f), _concept ?? "");

            // 第三行：Def 查询测试（不消耗 API 调用）
            y += 34f;
            Widgets.Label(new Rect(inRect.x, y + 6f, 150f, 24f), Loc.T("AI_Debug_DefQueryLabel"));
            _defQuery = Widgets.TextField(
                new Rect(inRect.x + 156f, y, inRect.width - 156f - 176f, 28f), _defQuery ?? "");

            if (Widgets.ButtonText(new Rect(inRect.xMax - 172f, y, 80f, 28f), Loc.T("AI_Debug_TestQuery")))
            {
                RunDefQuery();
            }

            if (Widgets.ButtonText(new Rect(inRect.xMax - 88f, y, 84f, 28f), Loc.T("AI_Debug_RebuildIndex")))
            {
                DefIndex.Rebuild();
                _defQueryResult = Loc.T("AI_Debug_DefIndexRebuilt");
            }

            y += 34f;

            var sb = new StringBuilder();
            sb.Append(Loc.T("AI_Debug_StatusLine", DirectorLoop.LastStatus)).Append('\n');
            sb.Append(Loc.T("AI_Debug_PhaseLine", DirectorLoop.PhaseName,
                    DirectorLoop.DirectorBusy ? Loc.T("AI_Common_Yes") : Loc.T("AI_Common_No"),
                    DirectorLoop.WriterBusy ? Loc.T("AI_Common_Yes") : Loc.T("AI_Common_No"),
                    DirectorLoop.HasQueuedConcept ? "1" : "0"))
              .Append('\n');
            sb.Append(Loc.T("AI_Debug_InfluenceLine",
                    AINarratorSettings.InfluenceLabel(AINarratorMod.Settings != null
                        ? AINarratorMod.Settings.colonistInfluence
                        : AINarratorSettings.InfluenceEverything)))
              .Append('\n');
            sb.Append(Loc.T("AI_Debug_CountsLine", CandidateTable.Count, ActionRegistry.All.Count,
                    PromptBuilder.LastMask))
              .Append('\n');
            sb.Append(Loc.T("AI_Debug_LookupLine", DirectorLoop.DirectorLookupStatus,
                    DirectorLoop.WriterLookupStatus)).Append('\n');
            if (!string.IsNullOrEmpty(_defQueryResult))
            {
                sb.Append(Loc.T("AI_Debug_TestQueryHeader")).Append('\n')
                  .Append(_defQueryResult).Append('\n');
            }

            NarratorWorldComponent component = NarratorWorldComponent.Instance;
            if (component != null)
            {
                NarratorState st = component.State;
                sb.Append(Loc.T("AI_Debug_FailLine", st.failStreak, st.callsToday,
                        AINarratorMod.Settings != null ? AINarratorMod.Settings.maxApiCallsPerDay : 0))
                  .Append('\n');
                sb.Append(Loc.T("AI_Debug_PendingLine", st.pending.Count)).Append('\n');
                sb.Append(Loc.T("AI_Debug_QuotaLine", BudgetPolicy.Describe(st, AINarratorMod.Settings))).Append('\n');
                string objective = ObjectiveTracker.ActiveSummary(st);
                if (!string.IsNullOrEmpty(objective))
                {
                    sb.Append(Loc.T("AI_Debug_ObjectiveLine", objective)).Append('\n');
                }
                sb.Append(Loc.T("AI_Debug_HistoryHeader")).Append('\n')
                  .Append(st.RecentSummary(8)).Append('\n');
            }

            sb.Append(Loc.T("AI_Debug_PromptHeader")).Append('\n');
            sb.Append(DirectorLoop.LastPrompt).Append('\n');
            if (!string.IsNullOrEmpty(DirectorLoop.LastWriterPrompt))
            {
                sb.Append(Loc.T("AI_Debug_WriterPromptHeader")).Append('\n');
                sb.Append(DirectorLoop.LastWriterPrompt).Append('\n');
            }

            sb.Append(Loc.T("AI_Debug_ResponseHeader")).Append('\n');
            sb.Append(DirectorLoop.LastResponse).Append('\n');
            sb.Append(Loc.T("AI_Debug_RuntimeHeader")).Append('\n');
            sb.Append(EventRuntime.LastStatus).Append('\n');
            sb.Append(Loc.T("AI_Debug_LastStageLine", EventRuntime.LastPlanSummary)).Append('\n');

            if (component != null && component.State.instances.Count > 0)
            {
                sb.Append(Loc.T("AI_Debug_ActiveEventsHeader")).Append('\n');
                for (int i = 0; i < component.State.instances.Count; i++)
                {
                    EventInstanceRecord inst = component.State.instances[i];
                    sb.Append(Loc.T("AI_Debug_InstanceLine",
                        inst.id,
                        inst.defId,
                        string.IsNullOrEmpty(inst.stageName) ? "?" : inst.stageName,
                        inst.done ? Loc.T("AI_Debug_DoneSuffix") : "",
                        inst.timers.Count,
                        string.IsNullOrEmpty(inst.lastError) ? "" : Loc.T("AI_Debug_ErrorSuffix", inst.lastError)))
                      .Append('\n');
                }
            }

            if (!string.IsNullOrEmpty(EventRuntime.LastScriptPreview))
            {
                sb.Append(Loc.T("AI_Debug_ScriptPreviewHeader")).Append('\n');
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
