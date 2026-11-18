using System;
using System.Collections.Generic;

namespace Stellaris.AI
{
    /// <summary>
    /// 提示词里的"动作可见性"裁剪。
    ///
    /// 关键词只影响提示词的展示。动作是否可用由玩家设置和运行时校验决定。
    ///
    /// 规则：常驻 = 叙事 + 生成 + 地图 + 角色（23 个）；
    /// "心理 / 场景 / 交互 / 世界"四组按剧情关键词出现，最多再加两组。
    /// </summary>
    public static class ActionSelector
    {
        private static readonly string[] SocialWords =
        {
            "心情", "情绪", "想法", "悲伤", "愤怒", "恐惧", "绝望", "希望", "愧疚", "孤独",
            "关系", "朋友", "亲人", "爱人", "兄弟", "姐妹", "父母", "孩子", "家", "背叛",
            "特质", "性格", "崩溃", "疯", "灵感", "梦", "记忆", "记得", "思念", "哀悼"
        };

        private static readonly string[] SceneWords =
        {
            "废墟", "遗迹", "建筑", "房屋", "墙", "地形", "泥", "焦", "灰", "血迹", "痕迹",
            "动物", "兽", "虫", "塌", "洞", "坑", "烧", "营地", "现场", "残骸", "雪", "荒"
        };

        private static readonly string[] InteractiveWords =
        {
            "目标", "期限", "倒计时", "守", "等", "抉择", "决定", "信件", "选择", "镜头", "演出"
        };

        private static readonly string[] WorldWords =
        {
            "派系", "外交", "研究", "任务", "好感", "联盟", "敌对", "声望", "科技", "条约", "商队"
        };

        public static ActionCategoryMask Choose(string title, string concept, AINarratorSettings settings)
        {
            // 常驻组：0.5.0 的老动作（23 个；世界动作改为关键词触发），不改老行为。
            ActionCategoryMask mask = ActionCategoryMask.Narrative
                                      | ActionCategoryMask.Spawn
                                      | ActionCategoryMask.Map
                                      | ActionCategoryMask.Pawn;

            string text = ((title ?? "") + " " + (concept ?? "")).ToLowerInvariant();

            var extras = new List<ActionCategory>(4);
            if (Matches(text, SocialWords)) extras.Add(ActionCategory.Social);
            if (Matches(text, SceneWords)) extras.Add(ActionCategory.Scene);
            if (Matches(text, InteractiveWords)) extras.Add(ActionCategory.Interactive);
            if (Matches(text, WorldWords)) extras.Add(ActionCategory.World);

            // 玩家在设置里关掉的组，一律不出现（比关键词优先级高）。
            extras.RemoveAll(c => !IsEnabled(c, settings));

            // 最多再加两组：再多就把提示词撑回"全量"了，等于白裁。
            int take = Math.Min(2, extras.Count);
            for (int i = 0; i < take; i++) mask |= ActionRegistry.MaskOf(extras[i]);

            return mask;
        }

        internal static bool IsEnabled(ActionCategory category, AINarratorSettings settings)
        {
            if (settings == null) return true;

            switch (category)
            {
                case ActionCategory.Social:
                    return settings.enableSocialActions;
                case ActionCategory.Scene:
                    return settings.enableSceneActions;
                case ActionCategory.Interactive:
                    return settings.enableInteractiveActions;
                case ActionCategory.World:
                    return true;
                default:
                    return true;
            }
        }

        private static bool Matches(string text, string[] words)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < words.Length; i++)
            {
                if (text.Contains(words[i])) return true;
            }

            return false;
        }

        /// <summary>日志/调试用：把 mask 翻译成人能读的组名。</summary>
        public static string Describe(ActionCategoryMask mask)
        {
            var parts = new List<string>();
            foreach (ActionCategory category in Enum.GetValues(typeof(ActionCategory)))
            {
                if ((mask & ActionRegistry.MaskOf(category)) == 0) continue;
                parts.Add(category.ToString());
            }

            return string.Join(",", parts.ToArray()) + " (" + ActionRegistry.CountIn(mask) + " 个)";
        }
    }
}
