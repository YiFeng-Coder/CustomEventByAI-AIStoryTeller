using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;
using RimLua;

namespace CustomEventByAI.AIStoryTeller
{
    public sealed class StageOutcome
    {
        public bool Ok;
        public StagePlan Plan;
        public string Error = "";
        public int ElapsedMs;
    }

    /// <summary>
    /// 在 RimLua 的硬沙箱里执行"事件阶段脚本"。
    ///
    /// 契约（AI 必须照这个写）：
    ///   function run(ctx, state) -> rim.aist.json_encode({ actions=..., next_state=... })
    ///
    /// 关键点：
    ///  * 脚本在内存里编译，不落盘、不走 RimLua 的文件监视器；
    ///  * 每次校验与执行都创建独立的 Script，只有 next_state 会作为下一阶段的 state；
    ///  * ctx / state 都是纯数据（Dictionary/List/string/number/bool），脚本拿不到任何游戏对象；
    ///  * 预算是硬上限：墙钟 40ms、每片 4000 指令，超了直接判失败，绝不半途生效。
    /// </summary>
    public static class LuaStageRunner
    {
        public const int WallClockBudgetMs = 40;
        public const int SliceInstructionBudget = 4000;
        public const int MaxSourceBytes = 64 * 1024;
        public static bool Ready => LuaRuntime.Initialized;

        /// <summary>执行一次阶段。ctx 与 state 都是纯数据。</summary>
        public static StageOutcome Run(string source, Dictionary<string, object> ctx, string stateJson)
        {
            var outcome = new StageOutcome();

            if (string.IsNullOrWhiteSpace(source))
            {
                outcome.Error = Loc.Pick("脚本为空", "script is empty");
                return outcome;
            }

            if (System.Text.Encoding.UTF8.GetByteCount(source) > MaxSourceBytes)
            {
                outcome.Error = Loc.Pick("脚本超过 " + (MaxSourceBytes / 1024) + "KB 上限",
                    "script exceeds the " + (MaxSourceBytes / 1024) + "KB limit");
                return outcome;
            }

            if (!LuaRuntime.Initialized)
            {
                outcome.Error = Loc.Pick("RimLua 运行时尚未初始化（是不是没装/没启用 RimLua？）",
                    "the RimLua runtime is not initialized (is RimLua missing or disabled?)");
                return outcome;
            }

            LlmRunGuard();
            long started = Environment.TickCount64;

            Script script;
            try
            {
                script = Compile(source, out string compileError);
                if (script == null)
                {
                    outcome.Error = compileError;
                    return outcome;
                }
            }
            catch (Exception e)
            {
                outcome.Error = Loc.Pick("编译异常：" + e.GetType().Name + ": " + e.Message,
                    "compile threw: " + e.GetType().Name + ": " + e.Message);
                return outcome;
            }

            object state = LuaJson.Decode(stateJson);
            object[] args = { ctx ?? new Dictionary<string, object>(), state };

            RimLua.Api.LuaCallResult result = LuaRuntime.CallFunction(
                script, "run", args, WallClockBudgetMs, SliceInstructionBudget);

            outcome.ElapsedMs = (int)(Environment.TickCount64 - started);

            if (result == null)
            {
                outcome.Error = Loc.Pick("RimLua 返回了 null", "RimLua returned null");
                return outcome;
            }

            if (!result.Ok)
            {
                outcome.Error = result.Error ?? Loc.Pick("脚本执行失败", "script execution failed");
                return outcome;
            }

            StagePlan plan = StagePlanParser.Parse(result.ReturnText);
            if (plan.ParseError != null)
            {
                outcome.Error = plan.ParseError;
                return outcome;
            }

            outcome.Ok = true;
            outcome.Plan = plan;
            return outcome;
        }

        /// <summary>只做编译与"能否定义 run"的检查，用于产出入库前的校验。</summary>
        public static bool Validate(string source, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(source))
            {
                error = Loc.Pick("脚本为空", "script is empty");
                return false;
            }

            if (System.Text.Encoding.UTF8.GetByteCount(source) > MaxSourceBytes)
            {
                error = Loc.Pick("脚本超过 " + (MaxSourceBytes / 1024) + "KB 上限",
                    "script exceeds the " + (MaxSourceBytes / 1024) + "KB limit");
                return false;
            }

            if (!LuaRuntime.Initialized)
            {
                error = Loc.Pick("RimLua 未初始化", "RimLua is not initialized");
                return false;
            }

            try
            {
                Script script = Compile(source, out string compileError);
                if (script == null)
                {
                    error = compileError;
                    return false;
                }

                DynValue fn = script.Globals.Get("run");
                if (fn == null || fn.Type != DataType.Function)
                {
                    error = Loc.Pick("脚本里没有定义全局函数 run(ctx, state)",
                        "the script does not define the global function run(ctx, state)");
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>源码哈希，用于去重与存档里的脚本版本标识。</summary>
        public static string HashOf(string source)
        {
            if (source == null) return "";
            using var sha = System.Security.Cryptography.SHA1.Create();
            byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(source));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static Script Compile(string source, out string error)
        {
            error = null;
            Script script = LuaRuntime.CreateScript();
            LuaApiBridge.InstallStageGlobals(script);

            LuaRunResult compile = LuaRuntime.RunChunk(
                script, source, "ainarrator:" + HashOf(source).Substring(0, 8),
                WallClockBudgetMs, SliceInstructionBudget);

            if (compile == null || !compile.IsSuccess)
            {
                error = Loc.Pick("编译失败：" + (compile == null ? "null" : compile.Error),
                    "compile failed: " + (compile == null ? "null" : compile.Error));
                return null;
            }

            return script;
        }

        /// <summary>RimLua 侧要求主线程；这里只是提前给出更清楚的错误。</summary>
        private static void LlmRunGuard()
        {
            if (!RimLuaThread.IsMainThread)
            {
                throw new InvalidOperationException(Loc.Pick("Lua 阶段脚本必须在主线程执行",
                    "Lua stage scripts must run on the main thread"));
            }
        }
    }
}
