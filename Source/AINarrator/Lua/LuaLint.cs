using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Stellaris.AI
{
    /// <summary>
    /// 脚本静态体检。
    ///
    /// 原则：**只查确定性错误**，凡是可能误伤的启发式检查一律不做——
    /// 误报一次就要让模型重写一次，代价比漏报大得多。
    ///
    /// 保留的检查：
    ///  1. 有没有 run(ctx, state)；
    ///  2. 有没有 rim.aist.json_encode；
    ///  3. 沙箱禁用接口；
    ///  4. type 是否都在动作注册表里；
    ///  5. 有没有处理 "start"；
    ///  6. 用了 show_choices 却没有 choice_selected 分支；
    ///  7. 短线模式下是否出现了 timer。
    ///
    /// 刻意不做：opId 唯一性（改成运行时按"阶段+opId"去重，避免跨分支复用被误杀）、
    /// "有没有 done/timer"（运行时已有精确的悬空判定）。
    /// </summary>
    public static class LuaLint
    {
        private static readonly Regex TypeLiteral =
            new Regex("type\\s*=\\s*\"([A-Za-z0-9_.]+)\"", RegexOptions.Compiled);

        private static readonly Regex Forbidden =
            new Regex(@"\b(require|dofile|loadstring|loadfile|coroutine)\b|(^|[^\w])(os|io|debug)\.",
                RegexOptions.Compiled);

        private static readonly Regex TimerWord =
            new Regex(@"\btimer\s*=", RegexOptions.Compiled);

        public static List<string> Check(string source, IEnumerable<string> knownActionTypes, bool allowLong)
        {
            var issues = new List<string>();
            if (string.IsNullOrWhiteSpace(source))
            {
                issues.Add("脚本为空");
                return issues;
            }

            if (!Regex.IsMatch(source, @"function\s+run\s*\("))
            {
                issues.Add("必须定义全局函数 run(ctx, state)");
            }

            if (!source.Contains("rim.aist.json_encode"))
            {
                issues.Add("必须用 rim.aist.json_encode({...}) 返回结果，否则游戏读不到动作");
            }

            Match forbidden = Forbidden.Match(source);
            if (forbidden.Success)
            {
                issues.Add("使用了沙箱禁止的接口：" + forbidden.Value.Trim() + "（只有 string/table/math 可用）");
            }

            var known = new HashSet<string>(StringComparer.Ordinal);
            if (knownActionTypes != null)
            {
                foreach (string type in knownActionTypes)
                {
                    if (!string.IsNullOrEmpty(type)) known.Add(type);
                }
            }

            foreach (Match match in TypeLiteral.Matches(source))
            {
                string type = match.Groups[1].Value;
                if (known.Count > 0 && !known.Contains(type))
                {
                    issues.Add("未知的动作类型 \"" + type + "\"（只能用动作手册里列出的 type）");
                }
            }

            if (!source.Contains("ctx.trigger") && !source.Contains("trigger =="))
            {
                issues.Add("脚本没有检查 ctx.trigger，无法区分不同阶段");
            }
            else if (!source.Contains("\"start\"") && !source.Contains("'start'"))
            {
                issues.Add("脚本没有处理 trigger == \"start\"：事件启动时就是这个触发，不处理会导致开局空转");
            }

            bool usesChoices = source.Contains("story.show_choices") || source.Contains("story.choice_letter");
            if (usesChoices && !source.Contains("choice_selected"))
            {
                issues.Add("用了选项类动作（show_choices / choice_letter），但没有处理 trigger == \"choice_selected\"，"
                           + "玩家选完剧情会卡住");
            }

            if (!allowLong && TimerWord.IsMatch(source))
            {
                issues.Add("玩家关闭了长线事件，脚本里不能出现 timer");
            }

            return issues;
        }
    }
}