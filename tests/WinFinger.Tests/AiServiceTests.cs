using WinFinger.Services;
using Xunit;

namespace WinFinger.Tests;

public class AiServiceTests
{
    [Fact]
    public void BuildPayloadJson_KeepsMessageOrderAndRoles()
    {
        var cfg = new AppSettings { AiModel = "m1" };
        var turns = new[]
        {
            new ChatTurn("system", "s"),
            new ChatTurn("user", "u1"),
            new ChatTurn("assistant", "a1"),
            new ChatTurn("user", "u2")
        };

        string json = AiService.BuildPayloadJson(cfg, turns, stream: true, maxTokens: null, model: null,
            temperature: AiService.ChatTemperature);

        int s1 = json.IndexOf("\"u1\"", StringComparison.Ordinal);
        int a1 = json.IndexOf("\"a1\"", StringComparison.Ordinal);
        int u2 = json.IndexOf("\"u2\"", StringComparison.Ordinal);
        Assert.True(s1 > 0 && s1 < a1 && a1 < u2);
        Assert.Contains("\"role\":\"assistant\"", json);
        Assert.Contains("\"stream\":true", json);
        Assert.Contains("\"model\":\"m1\"", json);
    }

    [Fact]
    public void BuildPayloadJson_ModelOverrideBeatsSettings()
    {
        var cfg = new AppSettings { AiModel = "settings-model" };
        string json = AiService.BuildPayloadJson(cfg, new[] { new ChatTurn("user", "hi") },
            stream: false, maxTokens: 1, model: "override-model", temperature: 0.3);

        Assert.Contains("\"model\":\"override-model\"", json);
        Assert.Contains("\"max_tokens\":1", json);
        Assert.Contains("\"stream\":false", json);
    }

    [Fact]
    public void BuildPayloadJson_BlankModel_FallsBackToDefault()
    {
        string json = AiService.BuildPayloadJson(new AppSettings { AiModel = "  " },
            new[] { new ChatTurn("user", "hi") }, stream: true, maxTokens: null, model: null, temperature: 0.3);

        Assert.Contains("\"model\":\"gpt-4o-mini\"", json);
    }

    [Fact]
    public void ParseSseLine_DataDelta_ReturnsContent()
    {
        var evt = AiService.ParseSseLine("data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}");
        Assert.Equal(SseKind.Content, evt.Kind);
        Assert.Equal("x", evt.Text);
    }

    [Fact]
    public void ParseSseLine_BareNdjson_ReturnsContent()
    {
        var evt = AiService.ParseSseLine("{\"choices\":[{\"delta\":{\"content\":\"y\"}}]}");
        Assert.Equal(SseKind.Content, evt.Kind);
        Assert.Equal("y", evt.Text);
    }

    [Fact]
    public void ParseSseLine_Done_ReturnsDone()
    {
        Assert.Equal(SseKind.Done, AiService.ParseSseLine("data: [DONE]").Kind);
        Assert.Equal(SseKind.Done, AiService.ParseSseLine("data: [DONE]\r").Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(": keep-alive")]
    [InlineData("data:")]
    [InlineData("event: message")]
    [InlineData("data: not-json")]
    public void ParseSseLine_NoiseLines_AreIgnored(string line)
    {
        Assert.Equal(SseKind.Ignore, AiService.ParseSseLine(line).Kind);
    }

    [Fact]
    public void ParseSseLine_EmptyChoices_IsIgnored()
    {
        Assert.Equal(SseKind.Ignore, AiService.ParseSseLine("data: {\"choices\":[]}").Kind);
    }

    [Fact]
    public void ParseSseLine_DeltaWithoutContent_IsIgnored()
    {
        Assert.Equal(SseKind.Ignore,
            AiService.ParseSseLine("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}").Kind);
        Assert.Equal(SseKind.Ignore,
            AiService.ParseSseLine("data: {\"choices\":[{\"delta\":{\"content\":null}}]}").Kind);
    }

    [Fact]
    public void ParseSseLine_ErrorObject_ReturnsError()
    {
        var evt = AiService.ParseSseLine("data: {\"error\":{\"message\":\"boom\"}}");
        Assert.Equal(SseKind.Error, evt.Kind);
        Assert.Equal("boom", evt.Text);
    }

    [Fact]
    public void ParseSseLine_NonStreamMessageContent_ReturnsContent()
    {
        var evt = AiService.ParseSseLine("{\"choices\":[{\"message\":{\"content\":\"pong\"}}]}");
        Assert.Equal(SseKind.Content, evt.Kind);
        Assert.Equal("pong", evt.Text);
    }

    [Fact]
    public void BuildTranslatePrompt_Auto_CjkGoesToEnglish()
    {
        var prompt = AiService.BuildTranslatePrompt("你好世界", "auto");
        Assert.Contains("英文", prompt);
        Assert.Contains("你好世界", prompt);
    }

    [Fact]
    public void BuildTranslatePrompt_Auto_LatinGoesToChinese()
    {
        var prompt = AiService.BuildTranslatePrompt("hello world", "auto");
        Assert.Contains("中文", prompt);
    }

    [Fact]
    public void BuildTranslatePrompt_ExplicitTargetWins()
    {
        Assert.Contains("日文", AiService.BuildTranslatePrompt("hello", "ja"));
        Assert.Contains("英文", AiService.BuildTranslatePrompt("hello", "en"));
        Assert.Contains("中文", AiService.BuildTranslatePrompt("你好", "zh"));
    }

    [Fact]
    public void TranslateSystemPrompt_IsTerse()
    {
        Assert.Equal("你是专业翻译，只输出译文，不解释。", AiService.TranslateSystemPrompt);
    }

    [Fact]
    public void AiException_CarriesStatusCode()
    {
        var ex = new AiException("nope", 429);
        Assert.Equal(429, ex.StatusCode);
        Assert.Null(new AiException("nope").StatusCode);
    }
}
