using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace WinFinger.Services;

/// <summary>AI 调用失败；<see cref="StatusCode"/> 为 HTTP 状态码（网络/超时类错误为 null）。</summary>
public sealed class AiException : Exception
{
    public AiException(string message, int? statusCode = null) : base(message) => StatusCode = statusCode;

    public int? StatusCode { get; }
}

/// <summary>SSE 行的解析结果类型。</summary>
public enum SseKind
{
    /// <summary>空行、注释行或没有增量内容，直接跳过。</summary>
    Ignore,
    /// <summary>一段增量文本。</summary>
    Content,
    /// <summary>流正常结束（[DONE]）。</summary>
    Done,
    /// <summary>流中返回了 error 对象。</summary>
    Error
}

/// <summary>一行 SSE 的解析结果。</summary>
public readonly record struct SseEvent(SseKind Kind, string Text)
{
    public static readonly SseEvent Ignore = new(SseKind.Ignore, "");
    public static readonly SseEvent Done = new(SseKind.Done, "");
}

/// <summary>
/// OpenAI 兼容的 Chat Completions 客户端（流式）。BaseUrl / Model / Key / 超时都来自设置。
/// </summary>
public sealed class AiService
{
    /// <summary>翻译动作使用的系统提示词。</summary>
    public const string TranslateSystemPrompt = "你是专业翻译，只输出译文，不解释。";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>设置为空白时的兜底值来源，避免在这里重复写一遍默认 BaseUrl/Model/超时。</summary>
    private static readonly AppSettings Defaults = new();

    private readonly SettingsService _settings;

    public AiService(SettingsService settings) => _settings = settings;

    /// <summary>是否已配置 API Key。</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.GetAiApiKey());

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            // 超时由每次请求的 linked CTS 控制，流式响应不能用 HttpClient.Timeout
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinFinger/1.1.0");
        return client;
    }

    /// <summary>流式对话；逐段 yield 增量文本。失败抛 <see cref="AiException"/>（消息已本地化）。</summary>
    public async IAsyncEnumerable<string> StreamChatAsync(string systemPrompt, string userPrompt,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string? key = _settings.GetAiApiKey();
        if (string.IsNullOrWhiteSpace(key)) throw new AiException("未配置 API Key");

        var cfg = _settings.Settings;
        int timeout = cfg.AiTimeoutSeconds > 0 ? cfg.AiTimeoutSeconds : Defaults.AiTimeoutSeconds;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(timeout));
        var token = linked.Token;

        using var request = BuildRequest(cfg, key, systemPrompt, userPrompt, stream: true);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AiException($"网络错误：{ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiException($"请求超时（{timeout} s）");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await BuildErrorAsync(response, token).ConfigureAwait(false);
            }

            Stream stream;
            StreamReader reader;
            try
            {
                stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                reader = new StreamReader(stream, Encoding.UTF8);
            }
            catch (HttpRequestException ex)
            {
                throw new AiException($"网络错误：{ex.Message}");
            }

            using (reader)
            {
                while (true)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new AiException($"网络错误：{ex.Message}");
                    }
                    catch (IOException ex)
                    {
                        throw new AiException($"网络错误：{ex.Message}");
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new AiException($"请求超时（{timeout} s）");
                    }

                    if (line is null) yield break;

                    var evt = ParseSseLine(line);
                    switch (evt.Kind)
                    {
                        case SseKind.Content:
                            yield return evt.Text;
                            break;
                        case SseKind.Done:
                            yield break;
                        case SseKind.Error:
                            throw new AiException(evt.Text);
                        default:
                            break;
                    }
                }
            }
        }
    }

    /// <summary>连通性自检：发一次极小的请求，返回 (是否成功, 提示文案)。</summary>
    public async Task<(bool ok, string message)> TestAsync(CancellationToken ct)
    {
        string? key = _settings.GetAiApiKey();
        if (string.IsNullOrWhiteSpace(key)) return (false, "未配置 API Key");

        var cfg = _settings.Settings;
        int timeout = cfg.AiTimeoutSeconds > 0 ? cfg.AiTimeoutSeconds : Defaults.AiTimeoutSeconds;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(timeout));
        var token = linked.Token;

        try
        {
            using var request = BuildRequest(cfg, key, "You are a helpful assistant.", "ping",
                stream: false, maxTokens: 1);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = await BuildErrorAsync(response, token).ConfigureAwait(false);
                return (false, error.Message);
            }
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return (true, $"连接成功（{cfg.AiModel}）{ReplySnippet(body)}");
        }
        catch (AiException ex)
        {
            return (false, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return (false, $"网络错误：{ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, $"请求超时（{timeout} s）");
        }
    }

    /// <summary>构造翻译用户提示词；target 为 auto 时：含 CJK → 英文，否则 → 中文。</summary>
    public static string BuildTranslatePrompt(string text, string target)
    {
        string language = ResolveTargetLanguage(text, target);
        return $"把下面的内容翻译成{language}：\n\n{text}";
    }

    private static string ResolveTargetLanguage(string text, string target)
    {
        string t = (target ?? "").Trim();
        if (t.Length == 0 || t.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return ContentDetector.HasCjk(text) ? "英文" : "中文";

        return t.ToLowerInvariant() switch
        {
            "zh" or "zh-cn" or "zh-hans" or "chinese" => "中文",
            "en" or "en-us" or "english" => "英文",
            "ja" or "ja-jp" or "japanese" => "日文",
            "ko" or "ko-kr" or "korean" => "韩文",
            "fr" => "法文",
            "de" => "德文",
            "es" => "西班牙文",
            "ru" => "俄文",
            _ => t
        };
    }

    /// <summary>解析一行 SSE / NDJSON。容忍无 data: 前缀、注释行、空 delta、流中 error 对象。</summary>
    public static SseEvent ParseSseLine(string line)
    {
        if (line is null) return SseEvent.Ignore;
        string s = line.Trim('\r', '\n', ' ', '\t');
        if (s.Length == 0) return SseEvent.Ignore;
        if (s.StartsWith(':')) return SseEvent.Ignore; // SSE 心跳注释

        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            s = s[5..].Trim();

        if (s.Length == 0) return SseEvent.Ignore;
        if (s == "[DONE]") return SseEvent.Done;
        if (!s.StartsWith('{')) return SseEvent.Ignore;

        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return SseEvent.Ignore;

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                string message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? (m.GetString() ?? "未知错误")
                    : "未知错误";
                return new SseEvent(SseKind.Error, message);
            }

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
                return SseEvent.Ignore;

            var choice = choices[0];
            string? content = null;
            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                && delta.TryGetProperty("content", out var dc) && dc.ValueKind == JsonValueKind.String)
                content = dc.GetString();
            // 非流式响应或部分服务端会直接给 message.content
            else if (choice.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object
                     && msg.TryGetProperty("content", out var mc) && mc.ValueKind == JsonValueKind.String)
                content = mc.GetString();

            return string.IsNullOrEmpty(content) ? SseEvent.Ignore : new SseEvent(SseKind.Content, content);
        }
        catch (JsonException)
        {
            return SseEvent.Ignore;
        }
    }

    private static HttpRequestMessage BuildRequest(AppSettings cfg, string key, string systemPrompt,
        string userPrompt, bool stream, int? maxTokens = null)
    {
        string baseUrl = (cfg.AiBaseUrl ?? "").Trim().TrimEnd('/');
        if (baseUrl.Length == 0) baseUrl = Defaults.AiBaseUrl.TrimEnd('/');

        var payload = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrWhiteSpace(cfg.AiModel) ? Defaults.AiModel : cfg.AiModel,
            ["stream"] = stream,
            ["temperature"] = 0.3,
            ["messages"] = new object[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = systemPrompt },
                new Dictionary<string, string> { ["role"] = "user", ["content"] = userPrompt }
            }
        };
        if (maxTokens is { } max) payload["max_tokens"] = max;

        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    /// <summary>把非 2xx 响应映射成友好的中文错误（永不回显 API Key）。</summary>
    private static async Task<AiException> BuildErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        int code = (int)response.StatusCode;
        string detail = "";
        try
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            detail = ExtractErrorMessage(body);
        }
        catch
        {
            // 读不到响应体就只用状态码
        }

        string message = code switch
        {
            401 or 403 => "API Key 无效或无权限",
            429 => "请求过于频繁或额度用尽",
            404 => "模型或地址不存在（检查 BaseUrl/Model）",
            _ => detail.Length > 0 ? $"请求失败 (HTTP {code}): {detail}" : $"请求失败 (HTTP {code})"
        };
        return new AiException(message, code);
    }

    private static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? "";
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    return m.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应体，退回截断的原文
        }
        string trimmed = body.Trim().ReplaceLineEndings(" ");
        return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
    }

    private static string ReplySnippet(string body)
    {
        var evt = ParseSseLine(body);
        return evt.Kind == SseKind.Content ? $"：{evt.Text}" : "";
    }
}
