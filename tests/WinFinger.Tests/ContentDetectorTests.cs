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
}
