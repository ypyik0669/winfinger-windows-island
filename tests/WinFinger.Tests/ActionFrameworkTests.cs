using System.Text.RegularExpressions;
using WinFinger.Models;
using WinFinger.Services;
using Xunit;

namespace WinFinger.Tests;

public class ActionFrameworkTests
{
    private static Regex? Rx(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static bool Matches(ActionDefinition def, ClipboardEntry entry) =>
        ActionCatalogService.Matches(def, entry, Rx!);

    private static ActionDefinition Def(string id, ActionMatch? match = null, string run = "builtin:noop",
        bool inline = false, int order = 100, bool hidden = false) =>
        new() { Id = id, Title = id, Match = match, Run = run, Inline = inline, Order = order, Hidden = hidden };

    private static ClipboardEntry Text(string text, string? type = null, string? app = null) =>
        new() { Kind = ClipboardEntryKind.Text, Text = text, ContentType = type, SourceAppBundleId = app };

    // ── matching ──

    [Fact]
    public void Matches_NoMatchBlock_MatchesEverything() =>
        Assert.True(Matches(Def("any"), Text("hello")));

    [Theory]
    [InlineData("url", true)]
    [InlineData("json", false)]
    public void Matches_ByContentType(string entryType, bool expected)
    {
        var def = Def("open-url", new ActionMatch { Types = new[] { "url" } });
        Assert.Equal(expected, Matches(def, Text("https://x.dev", entryType)));
    }

    [Fact]
    public void Matches_ByKind()
    {
        var def = Def("pin", new ActionMatch { Kinds = new[] { "image" } });
        Assert.True(Matches(def, new ClipboardEntry { Kind = ClipboardEntryKind.Image }));
        Assert.False(Matches(def, Text("hello")));
    }

    [Fact]
    public void Matches_OcrKind_NeedsRecognisedText()
    {
        var def = Def("ai", new ActionMatch { Kinds = new[] { "ocr" } });
        var blank = new ClipboardEntry { Kind = ClipboardEntryKind.Image };
        var read = new ClipboardEntry { Kind = ClipboardEntryKind.Image, OcrText = "识别到的文字" };
        Assert.False(Matches(def, blank));
        Assert.True(Matches(def, read));
    }

    [Fact]
    public void Matches_TypesAndKinds_AreOred()
    {
        var def = Def("open-path", new ActionMatch { Types = new[] { "path" }, Kinds = new[] { "file" } });
        Assert.True(Matches(def, Text(@"C:\tmp\a.txt", "path")));
        Assert.True(Matches(def, new ClipboardEntry { Kind = ClipboardEntryKind.File }));
        Assert.False(Matches(def, Text("hello", "plain")));
    }

    [Fact]
    public void Matches_Regex_IsCaseInsensitiveAndConstrains()
    {
        var def = Def("jira", new ActionMatch { Regex = @"^[a-z]+-\d+$" });
        Assert.True(Matches(def, Text("ABC-123")));
        Assert.False(Matches(def, Text("not a ticket")));
    }

    [Fact]
    public void Matches_Regex_FallsBackToOcrText()
    {
        var def = Def("ocr-regex", new ActionMatch { Kinds = new[] { "ocr" }, Regex = "发票" });
        var entry = new ClipboardEntry { Kind = ClipboardEntryKind.Image, OcrText = "增值税发票" };
        Assert.True(Matches(def, entry));
    }

    [Fact]
    public void Matches_InvalidRegex_NeverMatches()
    {
        var def = Def("bad", new ActionMatch { Regex = "([" });
        Assert.False(ActionCatalogService.Matches(def, Text("anything"), _ => null));
    }

    [Fact]
    public void Matches_ByApp()
    {
        var def = Def("code", new ActionMatch { Apps = new[] { "code" } });
        Assert.True(Matches(def, Text("x", app: "code")));
        Assert.True(Matches(def, Text("x", app: "code.exe")));
        Assert.False(Matches(def, Text("x", app: "notepad")));
    }

    // ── merge ──

    [Fact]
    public void Merge_UserOverridesById()
    {
        var merged = ActionCatalogService.Merge(
            new[] { Def("open-url", run: "open:{text}", order: 10) },
            new[] { Def("open-url", run: "open:https://custom/{text}", order: 5) });
        var def = Assert.Single(merged);
        Assert.Equal("open:https://custom/{text}", def.Run);
        Assert.Equal(5, def.Order);
    }

    [Fact]
    public void Merge_HiddenRemovesBuiltIn()
    {
        var merged = ActionCatalogService.Merge(
            new[] { Def("open-url"), Def("qr-encode") },
            new[] { Def("open-url", hidden: true) });
        Assert.Equal(new[] { "qr-encode" }, merged.Select(d => d.Id));
    }

    [Fact]
    public void Merge_AppendsNewIdsAndSortsInlineFirst()
    {
        var merged = ActionCatalogService.Merge(
            new[] { Def("b", order: 20), Def("a", order: 10, inline: true) },
            new[] { Def("c", order: 5) });
        Assert.Equal(new[] { "a", "c", "b" }, merged.Select(d => d.Id));
    }

    [Fact]
    public void Merge_SkipsEntriesWithoutIdOrRun()
    {
        var merged = ActionCatalogService.Merge(
            new[] { Def("ok"), new ActionDefinition { Id = "", Run = "open:x" }, new ActionDefinition { Id = "no-run" } },
            Array.Empty<ActionDefinition>());
        Assert.Equal(new[] { "ok" }, merged.Select(d => d.Id));
    }

    // ── ParseRun ──

    [Theory]
    [InlineData("open:{text}", ActionRunKind.Open, "{text}")]
    [InlineData("shell:explorer.exe /select,\"{path}\"", ActionRunKind.Shell, "explorer.exe /select,\"{path}\"")]
    [InlineData("builtin: ocr ", ActionRunKind.Builtin, "ocr")]
    [InlineData("prompt:总结：{text}", ActionRunKind.Prompt, "总结：{text}")]
    public void ParseRun_SplitsPrefixAndPayload(string run, ActionRunKind expectedKind, string expectedPayload)
    {
        Assert.True(ActionCatalogService.ParseRun(run, out var kind, out string payload));
        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedPayload, payload);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nocolon")]
    [InlineData("weird:thing")]
    [InlineData("open:")]
    public void ParseRun_RejectsBadRuns(string run) =>
        Assert.False(ActionCatalogService.ParseRun(run, out _, out _));

    // ── placeholders ──

    [Fact]
    public void Expand_ReplacesTextPathAndApp()
    {
        var entry = new ClipboardEntry
        {
            Kind = ClipboardEntryKind.Image,
            OcrText = "识别文本",
            ImagePath = @"C:\tmp\a.png",
            SourceAppName = "Chrome"
        };
        Assert.Equal(@"识别文本|C:\tmp\a.png|C:\tmp\a.png|Chrome",
            ActionExecutor.Expand("{text}|{path}|{png}|{app}", entry));
    }

    [Fact]
    public void Expand_QuotesMultipleFilePaths()
    {
        var entry = new ClipboardEntry { Kind = ClipboardEntryKind.File };
        entry.FilePaths.AddRange(new[] { @"C:\a b.txt", @"C:\c.txt" });
        Assert.Equal("\"C:\\a b.txt\" \"C:\\c.txt\"", ActionExecutor.Expand("{paths}", entry));
    }

    [Fact]
    public void Expand_ForShell_EscapesQuotesAndFlattensLines()
    {
        var entry = new ClipboardEntry { Kind = ClipboardEntryKind.Text, Text = "say \"hi\"\nagain" };
        Assert.Equal("echo say \\\"hi\\\" again", ActionExecutor.Expand("echo {text}", entry, forShell: true));
    }

    [Fact]
    public void Expand_ForShell_TruncatesLongText()
    {
        var entry = new ClipboardEntry { Kind = ClipboardEntryKind.Text, Text = new string('x', 9000) };
        string expanded = ActionExecutor.Expand("{text}", entry, forShell: true);
        Assert.Equal(ActionExecutor.ShellTextLimit, expanded.Length);
    }

    [Fact]
    public void Expand_MissingValuesBecomeEmpty() =>
        Assert.Equal("||", ActionExecutor.Expand("{text}|{path}|{app}", new ClipboardEntry()));

    // ── builtin helpers ──

    [Fact]
    public void FormatJson_Indents()
    {
        string formatted = BuiltinTools.FormatJson("{\"a\":1}");
        Assert.Contains("\n", formatted);
        Assert.Contains("\"a\": 1", formatted);
    }

    [Fact]
    public void MinifyJson_StripsWhitespace() =>
        Assert.Equal("{\"a\":1,\"b\":[2,3]}", BuiltinTools.MinifyJson("{\n  \"a\": 1,\n  \"b\": [2, 3]\n}"));

    [Fact]
    public void FormatJson_ThrowsOnGarbage() =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => BuiltinTools.FormatJson("not json"));

    [Fact]
    public void DescribeTimestamp_ReportsBothUnits()
    {
        string? described = BuiltinTools.DescribeTimestamp("1700000000");
        Assert.NotNull(described);
        Assert.Contains("UTC：2023-11-14", described);
        Assert.Contains("秒：1700000000", described);
        Assert.Contains("来源单位：秒", described);
    }

    [Fact]
    public void DescribeTimestamp_ReturnsNullForNonTimestamps() =>
        Assert.Null(BuiltinTools.DescribeTimestamp("hello"));

    [Fact]
    public void WordCount_CountsCharsWordsLinesAndCjk()
    {
        string report = BuiltinTools.WordCount("hello world\n你好");
        Assert.Contains("字符：14", report);
        Assert.Contains("词：3", report);
        Assert.Contains("行：2", report);
        Assert.Contains("中日韩字符：2", report);
    }

    [Theory]
    [InlineData("+86 138-0013-8000", "+8613800138000")]
    [InlineData("(010) 1234 5678", "01012345678")]
    [InlineData("no digits", "")]
    public void DigitsOnly_KeepsDigitsAndLeadingPlus(string input, string expected) =>
        Assert.Equal(expected, BuiltinTools.DigitsOnly(input));
}
