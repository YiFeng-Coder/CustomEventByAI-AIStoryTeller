using System;
using System.Threading;
using System.Threading.Tasks;

namespace CustomEventByAI.AIStoryTeller
{
    public enum LlmErrorKind
    {
        None = 0,
        NotConfigured,
        Network,
        Auth,
        RateLimited,
        ServerError,
        Timeout,
        BadResponse,
        Cancelled,
        Unknown,
        QuotaExceeded
    }

    /// <summary>一次模型调用的输入。纯数据，可在任意线程构造与读取。</summary>
    public class LlmRequest
    {
        public string SystemPrompt = "";
        public string UserPrompt = "";
        public string Model = "";
        public string Endpoint = "";
        public string ApiKey = "";
        public float Temperature = 0.7f;
        public int MaxTokens = 700;
        public int TimeoutSeconds = 45;

        // 默认不发 response_format：只有"输出契约本来就是 JSON"的调用（导演）才该打开。
        // 编剧要的是 TITLE + lua 代码块，开 JSON 模式会强制模型把脚本塞进字符串。
        public bool WantJsonObject = false;

        // 每次网关尝试都须重新占用额度，包括重试和 JSON 降级。
        internal Func<CancellationToken, Task<bool>> AuthorizeAttemptAsync;

        internal Task<bool> TryAuthorizeAttemptAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return AuthorizeAttemptAsync?.Invoke(ct) ?? Task.FromResult(true);
        }
    }

    /// <summary>一次模型调用的输出。失败时 Error 可读，Kind 用于决定是否重试。</summary>
    public class LlmResult
    {
        public bool Ok;
        public string Text = "";
        public string Error = "";
        public LlmErrorKind Kind = LlmErrorKind.None;

        /// <summary>
        /// 服务端给的结束原因：stop / length（被输出上限截断）/ content_filter …
        /// 截断时内容是"成功"返回的半截文本，解析器只会报一句含糊的错，
        /// 所以必须原样带回来，才知道该怪上限还是怪格式。
        /// </summary>
        public string FinishReason = "";

        public static LlmResult Success(string text, string finishReason = "") =>
            new LlmResult { Ok = true, Text = text ?? "", FinishReason = finishReason ?? "" };

        public static LlmResult Failure(LlmErrorKind kind, string error) =>
            new LlmResult { Ok = false, Kind = kind, Error = error ?? "" };

        public override string ToString() =>
            Ok ? "ok(" + Text.Length + " chars)" : Kind + ": " + Error;
    }

    /// <summary>
    /// 模型网关。实现必须满足：
    ///  * 只在后台线程执行，绝不访问 Verse / Find / 任何游戏对象；
    ///  * 不抛异常，所有失败都折成 LlmResult.Failure；
    ///  * 遵守传入的 CancellationToken。
    /// </summary>
    public interface ILlmGateway
    {
        string Name { get; }

        Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct);
    }
}
