using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Stellaris.AI
{
    /// <summary>
    /// OpenAI 兼容的 Chat Completions 网关。
    ///
    /// 设计约束：
    ///  * 不预设任何厂商，endpoint 由玩家填写（完整 URL 或 base 地址，两者都支持）；
    ///  * 逐次调用超时由 LlmRequest.TimeoutSeconds 控制；
    ///  * 只对"可能自愈"的错误重试（429 / 5xx / 网络 / 超时），401 与 400 直接失败；
    ///  * 若服务端不支持 response_format，会自动去掉该字段重试一次。
    /// </summary>
    public sealed class OpenAiCompatibleGateway : ILlmGateway
    {
        private const int MaxAttempts = 3;

        private static readonly HttpClient Http = CreateClient();

        public string Name => "openai-compatible";

        private static HttpClient CreateClient()
        {
            // 单例即可：不同请求的 model/endpoint/key 都放在请求体与 Header 里。
            // 自动重定向可能在额度检查之外再次发送 POST。
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "RimWorld-AINarrator/0.1");
            return client;
        }

        public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            if (request == null) return LlmResult.Failure(LlmErrorKind.Unknown, "null request");
            if (string.IsNullOrWhiteSpace(request.Endpoint))
                return LlmResult.Failure(LlmErrorKind.NotConfigured, "endpoint is empty");
            if (string.IsNullOrWhiteSpace(request.Model))
                return LlmResult.Failure(LlmErrorKind.NotConfigured, "model is empty");

            string url = NormalizeUrl(request.Endpoint);

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                bool useJsonMode = request.WantJsonObject && attempt == 1;
                LlmResult result = await TryOnceAsync(url, request, useJsonMode, ct).ConfigureAwait(false);

                if (result.Ok) return result;

                // 服务端不认 response_format 时（400/422）必须去掉它重试一次——这正是这个类
                // 注释里承诺的降级行为。BadResponse 不在 IsRetryable 里，所以单独放行。
                bool retryWithoutJsonMode = useJsonMode && result.Kind == LlmErrorKind.BadResponse;

                if (!retryWithoutJsonMode)
                {
                    if (!IsRetryable(result.Kind) || attempt == MaxAttempts) return result;

                    // 超时设得很长时不再重试超时：否则一次配置错误会让玩家等上半小时。
                    if (result.Kind == LlmErrorKind.Timeout && request.TimeoutSeconds >= 120) return result;
                }

                int delayMs = 700 * attempt;
                try
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return LlmResult.Failure(LlmErrorKind.Cancelled, "cancelled during backoff");
                }
            }

            return LlmResult.Failure(LlmErrorKind.Unknown, "unreachable");
        }

        private static async Task<LlmResult> TryOnceAsync(string url, LlmRequest request, bool jsonMode, CancellationToken ct)
        {
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
                httpRequest.Content = new StringContent(BuildBody(request, jsonMode), Encoding.UTF8, "application/json");
                if (!string.IsNullOrWhiteSpace(request.ApiKey))
                {
                    httpRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + request.ApiKey.Trim());
                }

                if (!await request.TryAuthorizeAttemptAsync(ct).ConfigureAwait(false))
                    return LlmResult.Failure(LlmErrorKind.QuotaExceeded, "今日 API 调用已达上限");

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, request.TimeoutSeconds)));
                using HttpResponseMessage response = await Http
                    .SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                    .ConfigureAwait(false);

                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return FailureForStatus((int)response.StatusCode, body);
                }

                return ParseCompletion(body);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return LlmResult.Failure(LlmErrorKind.Cancelled, "cancelled");
            }
            catch (OperationCanceledException)
            {
                return LlmResult.Failure(LlmErrorKind.Timeout, "timeout after " + request.TimeoutSeconds + "s");
            }
            catch (HttpRequestException e)
            {
                return LlmResult.Failure(LlmErrorKind.Network, e.Message);
            }
            catch (Exception e)
            {
                return LlmResult.Failure(LlmErrorKind.Unknown, e.GetType().Name + ": " + e.Message);
            }
        }

        private static string BuildBody(LlmRequest request, bool jsonMode)
        {
            using var stream = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("model", request.Model);
                writer.WriteNumber("temperature", request.Temperature);
                // MaxTokens <= 0 表示"不限制"：不下发该字段，交给服务端默认上限。
                if (request.MaxTokens > 0) writer.WriteNumber("max_tokens", request.MaxTokens);
                writer.WriteBoolean("stream", false);

                writer.WritePropertyName("messages");
                writer.WriteStartArray();
                WriteMessage(writer, "system", request.SystemPrompt);
                WriteMessage(writer, "user", request.UserPrompt);
                writer.WriteEndArray();

                if (jsonMode)
                {
                    writer.WritePropertyName("response_format");
                    writer.WriteStartObject();
                    writer.WriteString("type", "json_object");
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void WriteMessage(Utf8JsonWriter writer, string role, string content)
        {
            writer.WriteStartObject();
            writer.WriteString("role", role);
            writer.WriteString("content", content ?? "");
            writer.WriteEndObject();
        }

        private static LlmResult ParseCompletion(string body)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;

                if (!root.TryGetProperty("choices", out JsonElement choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0)
                {
                    return LlmResult.Failure(LlmErrorKind.BadResponse, "no choices in response");
                }

                JsonElement first = choices[0];
                if (!first.TryGetProperty("message", out JsonElement message) ||
                    !message.TryGetProperty("content", out JsonElement content))
                {
                    return LlmResult.Failure(LlmErrorKind.BadResponse, "no message.content in response");
                }

                string text = content.ValueKind == JsonValueKind.String
                    ? content.GetString()
                    : content.GetRawText();

                if (string.IsNullOrWhiteSpace(text))
                {
                    return LlmResult.Failure(LlmErrorKind.BadResponse, "empty content");
                }

                string finishReason = "";
                if (first.TryGetProperty("finish_reason", out JsonElement finish) &&
                    finish.ValueKind == JsonValueKind.String)
                {
                    finishReason = finish.GetString() ?? "";
                }

                return LlmResult.Success(text, finishReason);
            }
            catch (JsonException e)
            {
                return LlmResult.Failure(LlmErrorKind.BadResponse, "invalid json: " + e.Message);
            }
        }

        private static LlmResult FailureForStatus(int status, string body)
        {
            string snippet = Trim(body, 300);
            if (status >= 300 && status < 400)
                return LlmResult.Failure(LlmErrorKind.Unknown, "HTTP " + status + ": 请填写重定向后的接口地址");
            if (status == 401 || status == 403)
                return LlmResult.Failure(LlmErrorKind.Auth, "HTTP " + status + ": " + snippet);
            if (status == 429)
                return LlmResult.Failure(LlmErrorKind.RateLimited, "HTTP 429: " + snippet);
            if (status >= 500)
                return LlmResult.Failure(LlmErrorKind.ServerError, "HTTP " + status + ": " + snippet);
            if (status == 400 || status == 404 || status == 422)
                return LlmResult.Failure(LlmErrorKind.BadResponse, "HTTP " + status + ": " + snippet);
            return LlmResult.Failure(LlmErrorKind.Unknown, "HTTP " + status + ": " + snippet);
        }

        private static bool IsRetryable(LlmErrorKind kind) =>
            kind == LlmErrorKind.RateLimited ||
            kind == LlmErrorKind.ServerError ||
            kind == LlmErrorKind.Network ||
            kind == LlmErrorKind.Timeout;

        private static string NormalizeUrl(string endpoint)
        {
            string url = endpoint.Trim();
            if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return url;
            if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return url + "/chat/completions";
            if (url.EndsWith("/", StringComparison.Ordinal)) url = url.Substring(0, url.Length - 1);
            return url + "/chat/completions";
        }

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace('\n', ' ').Replace('\r', ' ');
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }
    }
}
