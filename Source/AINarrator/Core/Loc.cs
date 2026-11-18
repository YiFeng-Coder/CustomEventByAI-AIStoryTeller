using Verse;

namespace Stellaris.AI
{
    /// <summary>
    /// 本地化入口。
    ///
    /// 两层文本，刻意分开：
    ///  * 玩家可见文本（设置页、调试窗、信件、提示消息、Def 名称与简介）
    ///    → <c>Languages/&lt;语言&gt;/Keyed/*.xml</c>，用 <see cref="T(string)"/> 取。
    ///    放在 XML 里，翻译者不用改代码、不用重新编译。
    ///  * 给模型看的提示词与校验报错 → 代码里的双语常量，用 <see cref="Pick"/> 选。
    ///    它们动辄上万字、带缩进与代码块，塞进 XML 既难维护也难校对，
    ///    而且校验报错要和它的校验逻辑写在一起，才不会漂移。
    ///
    /// 叙事语言跟随游戏语言：中文游戏 → 中文提示词、中文剧情；其它语言 → 英文。
    /// </summary>
    public static class Loc
    {
        private const string ChineseFolderPrefix = "Chinese";

        /// <summary>当前游戏语言是否为中文（简体、繁体都算中文）。</summary>
        public static bool IsChinese
        {
            get
            {
                var language = LanguageDatabase.activeLanguage;
                if (language == null) return false;
                string folder = language.folderName;
                return folder != null &&
                       folder.StartsWith(ChineseFolderPrefix, System.StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>按当前游戏语言二选一。zh 与 en 必须语义等价。</summary>
        public static string Pick(string zh, string en) => IsChinese ? zh : en;

        /// <summary>取 Keyed 文本。key 必须同时存在于 English 与 ChineseSimplified 的 Keyed 里。</summary>
        public static string T(string key) => key.Translate();

        /// <summary>取带位置参数的 Keyed 文本（{0}、{1}……）。</summary>
        public static string T(string key, params object[] args) => string.Format(key.Translate(), args);
    }
}
