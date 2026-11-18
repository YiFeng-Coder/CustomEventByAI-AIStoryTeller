using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace Stellaris.AI
{
    /// <summary>
    /// 叙事抉择弹窗。
    ///
    /// 为什么不用原版 ChoiceLetter：那需要额外定义一个 LetterDef + 匹配的 viewer，
    /// 在没有真机验证的条件下风险太高。自绘 Window 行为完全可控，且能显示长叙事文本。
    ///
    /// 选择结果通过 <c>EventRuntime.FireSignalTo(instanceId, "choice_selected", ...)</c>
    /// 回到事件状态机，驱动下一阶段。
    /// </summary>
    public class Dialog_NarrativeChoice : Window
    {
        private readonly string _title;
        private readonly string _text;
        private readonly List<KeyValuePair<string, string>> _options;
        private readonly Action<string> _onChoose;
        private Vector2 _scroll;

        public Dialog_NarrativeChoice(string title, string text,
            List<KeyValuePair<string, string>> options, Action<string> onChoose)
        {
            _title = string.IsNullOrEmpty(title) ? Loc.T("AI_Choice_Title") : title;
            _text = text ?? "";
            _options = options ?? new List<KeyValuePair<string, string>>();
            _onChoose = onChoose;

            doCloseX = true;
            doCloseButton = false;
            closeOnCancel = false;
            absorbInputAroundWindow = true;
            forcePause = true;
            draggable = true;
            optionalTitle = _title;
        }

        public override Vector2 InitialSize => new Vector2(620f, 460f);

        public override void DoWindowContents(Rect inRect)
        {
            const float buttonHeight = 46f;
            const float gap = 6f;

            float optionsHeight = _options.Count * (buttonHeight + gap);
            float textHeight = Mathf.Max(80f, inRect.height - optionsHeight - 16f);

            Rect textRect = new Rect(inRect.x, inRect.y, inRect.width, textHeight);
            DrawScrollableText(textRect, _text);

            float y = inRect.y + textHeight + 8f;

            for (int i = 0; i < _options.Count; i++)
            {
                KeyValuePair<string, string> option = _options[i];
                Rect buttonRect = new Rect(inRect.x, y, inRect.width, buttonHeight);

                if (Widgets.ButtonText(buttonRect, option.Value))
                {
                    Choose(option.Key);
                    return;
                }

                y += buttonHeight + gap;
            }
        }

        private void DrawScrollableText(Rect rect, string text)
        {
            float viewWidth = rect.width - 18f;
            float viewHeight = Mathf.Max(rect.height, Text.CalcHeight(text, viewWidth) + 8f);
            Rect viewRect = new Rect(0f, 0f, viewWidth, viewHeight);

            Widgets.BeginScrollView(rect, ref _scroll, viewRect);
            Widgets.Label(viewRect, text);
            Widgets.EndScrollView();
        }

        private void Choose(string id)
        {
            try
            {
                _onChoose?.Invoke(id);
            }
            catch (Exception e)
            {
                Verse.Log.Error(AINarratorMod.LogPrefix + "choice callback failed: " + e);
            }

            Close();
        }
    }
}
