using MoonSharp.Interpreter;
using RimLua;
using RimLua.Api;
using Verse;

namespace CustomEventByAI.AIStoryTeller
{
    /// <summary>
    /// 注册给 Lua 的宿主函数，位于 <c>rim.aist.*</c>。
    ///
    /// 只暴露两个"纯函数"级能力：
    ///   rim.aist.json_encode(table) -> string   把结果表编码成 JSON（唯一的结构化回传通道）
    ///   rim.aist.version()          -> string   便于脚本自检
    ///
    /// 刻意不暴露任何能读写游戏对象的方法——所有副作用都必须走"动作计划"。
    /// </summary>
    [LuaModule("aist")]
    public static class LuaApiBridge
    {
        [LuaFunction("json_encode")]
        public static string JsonEncode(Table table)
        {
            return LuaJson.Encode(table);
        }

        [LuaFunction("version")]
        public static string HostVersion()
        {
            return "ainarrator-1";
        }

        /// <summary>给事件沙箱挂载专用 API 表，独立于 RimLua 的全局注册表。</summary>
        internal static void InstallStageGlobals(Script script)
        {
            var aist = new Table(script);
            aist.Set("json_encode", DynValue.NewCallback((_, args) =>
            {
                if (args.Count != 1)
                    throw new ScriptRuntimeException("rim.aist.json_encode requires one table");
                Table table = args.AsType(0, "rim.aist.json_encode", DataType.Table).Table;
                return DynValue.NewString(JsonEncode(table));
            }, "rim.aist.json_encode"));

            aist.Set("version", DynValue.NewCallback((_, args) =>
            {
                if (args.Count != 0)
                    throw new ScriptRuntimeException("rim.aist.version takes no arguments");
                return DynValue.NewString(HostVersion());
            }, "rim.aist.version"));

            var rim = new Table(script);
            rim.Set("aist", DynValue.NewTable(aist));
            script.Globals.Set("rim", DynValue.NewTable(rim));
        }

        /// <summary>
        /// 注册到 RimLua。必须在 RimLua 初始化之前或之后都能生效（注册表是静态的），
        /// 由 Mod 构造函数调用一次即可。
        /// </summary>
        public static void Register()
        {
            int count = RimLuaApi.RegisterModule(typeof(LuaApiBridge));
            if (count <= 0)
            {
                Log.Warning(AINarratorMod.LogPrefix + "failed to register rim.aist.* (RimLua rejected the module)");
            }
        }
    }
}
