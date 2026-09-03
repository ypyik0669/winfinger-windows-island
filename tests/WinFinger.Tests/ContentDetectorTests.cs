using System.Text.Json;
using System.Windows.Media;
using WinFinger.Models;
using WinFinger.Services;
using Xunit;

namespace WinFinger.Tests;

/// <summary>ContentDetector 与 ClipboardStore.Matches 的类型识别用例。</summary>
public class ContentDetectorTests
{
    [Theory]
    [InlineData("https://a.b/c", ContentDetector.Url)]
    [InlineData("www.x.com", ContentDetector.Url)]
    [InlineData("a@b.co", ContentDetector.Email)]
    [InlineData("+86 138 0000 0000", ContentDetector.Phone)]
    [InlineData("#1A2B3C", ContentDetector.Color)]
    [InlineData("rgb(1,2,3)", ContentDetector.Color)]
    [InlineData("1717000000", ContentDetector.Timestamp)]
    [InlineData("1717000000000", ContentDetector.Timestamp)]
    [InlineData("2026-09-03 12:00", ContentDetector.DateText)]
    [InlineData(@"C:\Windows", ContentDetector.Path)]
    [InlineData("{\"a\":1}", ContentDetector.Json)]
    [InlineData("# T\n- a", ContentDetector.Markdown)]
    [InlineData("function f(){return 1;}", ContentDetector.Code)]
    [InlineData("你好", ContentDetector.Plain)]
    [InlineData("", ContentDetector.Plain)]
    public void Detect_ReturnsExpectedType(string input, string expected)
        => Assert.Equal(expected, ContentDetector.Detect(input));

    // ── 误报回归：普通剪贴板文本不得被判成 code/date/phone ──

    [Theory]
    [InlineData("Please return the item; thanks.")]           // 散文里的分号 + return
    [InlineData("Call me back; I will explain later.")]
    [InlineData("公开的返回政策：请在 7 天内 return。")]
    [InlineData("1-2")]                                       // 不是日期
    [InlineData("3-4")]
    [InlineData("10-20")]
    [InlineData("1234567")]                                   // 裸数字串，不是电话
    [InlineData("9876543210")]
    public void Detect_OrdinaryText_IsPlain(string input)
        => Assert.Equal(ContentDetector.Plain, ContentDetector.Detect(input));

    [Fact]
    public void Detect_CompactDate_IsNeitherPhoneNorDate()
    {
        var type = ContentDetector.Detect("20260903");
        Assert.NotEqual(ContentDetector.Phone, type);
        Assert.NotEqual(ContentDetector.DateText, type);
        Assert.Equal(ContentDetector.Plain, type);
    }

    [Theory]
    [InlineData("+86 138 0000 0000", ContentDetector.Phone)]
    [InlineData("13800000000", ContentDetector.Phone)]
    [InlineData("010-12345678", ContentDetector.Phone)]
    // 400/800 服务号通常写成带分隔符的形式；紧凑的 "4001234567" 与 10 位 Unix 时间戳无法区分，按时间戳处理
    [InlineData("400-123-4567", ContentDetector.Phone)]
    [InlineData("800-810-1234", ContentDetector.Phone)]
    [InlineData("2026-09-03", ContentDetector.DateText)]
    [InlineData("2026/09/03 12:00", ContentDetector.DateText)]
    [InlineData("2026年9月3日", ContentDetector.DateText)]
    [InlineData("function f(){return 1;}", ContentDetector.Code)]
    public void Detect_StillRecognisesRealSamples(string input, string expected)
        => Assert.Equal(expected, ContentDetector.Detect(input));

    [Fact]
    public void Detect_MultiLineCSharpSnippet_IsCode()
    {
        const string snippet = "public void Foo()\n{\n    return;\n}";
        Assert.Equal(ContentDetector.Code, ContentDetector.Detect(snippet));
    }

    [Fact]
    public void Detect_PythonSnippet_IsCode()
    {
        const string snippet = "import os\n\ndef main(path):\n    return os.path.exists(path)";
        Assert.Equal(ContentDetector.Code, ContentDetector.Detect(snippet));
    }

    [Fact]
    public void Detect_VeryLongText_IsPlain()
        => Assert.Equal(ContentDetector.Plain, ContentDetector.Detect(new string('a', 70 * 1024)));

    [Fact]
    public void Detect_UncPath_SkipsExistenceCheck()
        => Assert.Equal(ContentDetector.Path, ContentDetector.Detect(@"\\server\share\file.txt"));

    [Fact]
    public void Detect_NonExistentDrivePath_IsNotPath()
        => Assert.NotEqual(ContentDetector.Path, ContentDetector.Detect(@"Z:\definitely\missing\a.txt"));

    [Theory]
    [InlineData("#1A2B3C", 255, 0x1A, 0x2B, 0x3C)]
    [InlineData("#abc", 255, 0xAA, 0xBB, 0xCC)]
    [InlineData("#1A2B3C80", 0x80, 0x1A, 0x2B, 0x3C)]
    [InlineData("rgb(1,2,3)", 255, 1, 2, 3)]
    [InlineData("rgba(1, 2, 3, 0)", 0, 1, 2, 3)]
    [InlineData("hsl(0,100%,50%)", 255, 255, 0, 0)]
    public void TryParseColor_ParsesKnownFormats(string input, int a, int r, int g, int b)
    {
        Assert.True(ContentDetector.TryParseColor(input, out Color color));
        Assert.Equal(Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b), color);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("rgb(300,2,3)")]
    [InlineData("")]
    public void TryParseColor_RejectsInvalid(string input)
        => Assert.False(ContentDetector.TryParseColor(input, out _));

    [Fact]
    public void TryParseTimestamp_Seconds()
    {
        Assert.True(ContentDetector.TryParseTimestamp("1717000000", out var dt, out bool wasMillis));
        Assert.False(wasMillis);
        Assert.Equal(1717000000L, dt.ToUnixTimeSeconds());
    }

    [Fact]
    public void TryParseTimestamp_Milliseconds()
    {
        Assert.True(ContentDetector.TryParseTimestamp("1717000000000", out var dt, out bool wasMillis));
        Assert.True(wasMillis);
        Assert.Equal(1717000000000L, dt.ToUnixTimeMilliseconds());
    }

    [Theory]
    [InlineData("123")]           // 位数不对
    [InlineData("0000000000")]    // 1970 年，超出 2001–2100
    [InlineData("abcdefghij")]
    public void TryParseTimestamp_RejectsInvalid(string input)
        => Assert.False(ContentDetector.TryParseTimestamp(input, out _, out _));

    [Theory]
    [InlineData(ContentDetector.Url, "链接")]
    [InlineData(ContentDetector.Email, "邮箱")]
    [InlineData(ContentDetector.Phone, "电话")]
    [InlineData(ContentDetector.Color, "颜色")]
    [InlineData(ContentDetector.Json, "JSON")]
    [InlineData(ContentDetector.Timestamp, "时间戳")]
    [InlineData(ContentDetector.DateText, "日期")]
    [InlineData(ContentDetector.Path, "路径")]
    [InlineData(ContentDetector.Markdown, "Markdown")]
    [InlineData(ContentDetector.Code, "代码")]
    public void Label_MapsKnownTypes(string type, string expected)
        => Assert.Equal(expected, ContentDetector.Label(type));

    [Theory]
    [InlineData(ContentDetector.Plain)]
    [InlineData(null)]
    [InlineData("unknown")]
    public void Label_ReturnsNullForPlainAndUnknown(string? type)
        => Assert.Null(ContentDetector.Label(type));

    [Theory]
    [InlineData("你好", true)]
    [InlineData("hello", false)]
    [InlineData("", false)]
    public void HasCjk_Works(string input, bool expected)
        => Assert.Equal(expected, ContentDetector.HasCjk(input));

    // ── ClipboardStore.Matches ──

    private static ClipboardEntry TextEntry(string text, string? ocr = null) => new()
    {
        Kind = ClipboardEntryKind.Text,
        Text = text,
        ContentType = ContentDetector.Detect(text),
        OcrText = ocr
    };

    [Fact]
    public void Matches_TypePrefix_FiltersByContentType()
    {
        var url = TextEntry("https://a.b/c");
        var plain = TextEntry("just some words");
        Assert.True(ClipboardStore.Matches(url, ClipboardFilter.All, "type:url"));
        Assert.False(ClipboardStore.Matches(plain, ClipboardFilter.All, "type:url"));
    }

    [Fact]
    public void Matches_TypePrefix_IsCaseInsensitiveAndSpaceTolerant()
    {
        var url = TextEntry("https://a.b/c");
        Assert.True(ClipboardStore.Matches(url, ClipboardFilter.All, "   TYPE:URL   "));
    }

    [Fact]
    public void Matches_TypePrefix_CombinesWithRemainingWords()
    {
        var url = TextEntry("https://a.b/c");
        Assert.True(ClipboardStore.Matches(url, ClipboardFilter.All, "type:url a.b"));
        Assert.False(ClipboardStore.Matches(url, ClipboardFilter.All, "type:url zzz"));
    }

    [Fact]
    public void Matches_TypeOcr_RequiresOcrText()
    {
        var withOcr = TextEntry("image note", ocr: "hello world");
        var without = TextEntry("image note");
        Assert.True(ClipboardStore.Matches(withOcr, ClipboardFilter.All, "type:ocr"));
        Assert.False(ClipboardStore.Matches(without, ClipboardFilter.All, "type:ocr"));
    }

    [Fact]
    public void Matches_SearchesOcrQrAndTypeLabel()
    {
        var entry = TextEntry("nothing here", ocr: "苹果派");
        entry.QrText = "zebra-code";
        Assert.True(ClipboardStore.Matches(entry, ClipboardFilter.All, "苹果派"));
        Assert.True(ClipboardStore.Matches(entry, ClipboardFilter.All, "zebra-code"));

        var url = TextEntry("https://a.b/c");
        Assert.True(ClipboardStore.Matches(url, ClipboardFilter.All, "链接"));
    }

    [Fact]
    public void Matches_EmptyQuery_MatchesEverything()
        => Assert.True(ClipboardStore.Matches(TextEntry("anything"), ClipboardFilter.All, "   "));

    // ── JSON 往返：WhenWritingNull 只省略 null，不影响 filePaths ──

    [Fact]
    public void Json_TextEntry_KeepsEmptyFilePathsAndOmitsNullKeys()
    {
        var entry = TextEntry("hello");
        var json = JsonSerializer.Serialize(entry, ClipboardStore.JsonOptions);

        Assert.Contains("\"filePaths\": []", json);
        Assert.DoesNotContain("\"imagePath\"", json);
        Assert.DoesNotContain("\"ocrText\"", json);
        Assert.DoesNotContain("\"qrText\"", json);
        Assert.Contains("\"contentType\"", json);

        var back = JsonSerializer.Deserialize<ClipboardEntry>(json, ClipboardStore.JsonOptions);
        Assert.NotNull(back);
        Assert.Equal("hello", back!.Text);
        Assert.Equal(entry.Id, back.Id);
        Assert.Equal(ClipboardEntryKind.Text, back.Kind);
        Assert.Empty(back.FilePaths);
        Assert.Null(back.ImagePath);
        Assert.Null(back.OcrText);
        Assert.Equal(entry.ContentType, back.ContentType);
    }
}
