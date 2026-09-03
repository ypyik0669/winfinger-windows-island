using System.Diagnostics;
using WinFinger.Services;
using Xunit;

namespace WinFinger.Tests;

/// <summary>MarkdownStream（增量块解析 + 防闪烁尾行）与 MiniMarkdown.ParseInline 的用例。</summary>
public class MiniMarkdownTests
{
    // ── 代码围栏 ──

    [Fact]
    public void CodeFence_UnterminatedAtEndOfStream_StaysOneOpenCodeBlock()
    {
        var s = MarkdownStream.Parse("```\nprint(1)\nprint(2)\n");

        var block = Assert.Single(s.Blocks);
        Assert.Equal(MdKind.Code, block.Kind);
        Assert.False(block.Closed);
        Assert.Equal(new[] { "print(1)", "print(2)" }, block.Lines);
    }

    [Fact]
    public void CodeFence_WithLanguage_CapturesLangAndCloses()
    {
        var s = MarkdownStream.Parse("```csharp\nvar x = 1;\n```\n");

        var block = Assert.Single(s.Blocks);
        Assert.Equal(MdKind.Code, block.Kind);
        Assert.Equal('`', block.FenceChar);
        Assert.Equal(3, block.FenceLength);
        Assert.Equal("csharp", block.Lang);
        Assert.True(block.Closed);
        Assert.Equal(new[] { "var x = 1;" }, block.Lines);
    }

    [Fact]
    public void CodeFence_WithoutLanguage_LangIsEmptyString()
    {
        var s = MarkdownStream.Parse("```\nhi\n```\n");

        var block = Assert.Single(s.Blocks);
        Assert.Equal("", block.Lang);
        Assert.True(block.Closed);
    }

    [Fact]
    public void CodeFence_TildeFence_WorksLikeBacktick()
    {
        var s = MarkdownStream.Parse("~~~py\nprint(1)\n~~~\n");

        var block = Assert.Single(s.Blocks);
        Assert.Equal('~', block.FenceChar);
        Assert.Equal("py", block.Lang);
        Assert.True(block.Closed);
        Assert.Equal(new[] { "print(1)" }, block.Lines);
    }

    [Fact]
    public void CodeFence_ClosedByLongerFence_Closes()
    {
        // opened with 3 backticks, closed with 4 -> closing fence length must only be >= opening length
        var s = MarkdownStream.Parse("```\ncode\n````\n");

        var block = Assert.Single(s.Blocks);
        Assert.True(block.Closed);
        Assert.Equal(new[] { "code" }, block.Lines);
    }

    [Fact]
    public void CodeFence_ShorterClosingAttempt_DoesNotClose_StaysLiteralContent()
    {
        // opened with 4 backticks; a lone line of 3 backticks must not close it
        var s = MarkdownStream.Parse("````\ncode\n```\nmore\n");

        var block = Assert.Single(s.Blocks);
        Assert.False(block.Closed);
        Assert.Equal(new[] { "code", "```", "more" }, block.Lines);
    }

    [Fact]
    public void CodeFence_BlankLineInsideCodeBlock_IsLiteralNotASeparator()
    {
        var s = MarkdownStream.Parse("```\na\n\nb\n```\n");

        var block = Assert.Single(s.Blocks);
        Assert.Equal(new[] { "a", "", "b" }, block.Lines);
    }

    // ── 标题 ──

    [Theory]
    [InlineData("# H1", 1, "H1")]
    [InlineData("## H2", 2, "H2")]
    [InlineData("### H3", 3, "H3")]
    public void Heading_Levels1To3_CarryLevelAndStrippedText(string line, int level, string text)
    {
        var s = MarkdownStream.Parse(line + "\n");

        var block = Assert.Single(s.Blocks);
        Assert.Equal(MdKind.Heading, block.Kind);
        Assert.Equal(level, block.Level);
        Assert.Equal(text, block.Text);
        Assert.True(block.Closed);
    }

    [Theory]
    [InlineData("#### H4")]
    [InlineData("#nospace")]
    public void Heading_TooManyHashesOrMissingSpace_IsParagraph(string line)
    {
        var s = MarkdownStream.Parse(line + "\n");

        var block = Assert.Single(s.Blocks);
        Assert.Equal(MdKind.Paragraph, block.Kind);
        Assert.Equal(0, block.Level);
        Assert.Equal(line, block.Text);
    }

    // ── 列表 ──

    [Theory]
    [InlineData("- a\n- b\n- c\n")]
    [InlineData("* a\n* b\n* c\n")]
    [InlineData("+ a\n+ b\n+ c\n")]
    [InlineData(" - a\n - b\n - c\n")]
    [InlineData("  - a\n  - b\n  - c\n")]
    [InlineData("   - a\n   - b\n   - c\n")]
    public void Bullets_MarkersAndUpTo3LeadingSpaces_GroupIntoOneBlockMarkerStripped(string doc)
    {
        var s = MarkdownStream.Parse(doc);

        var block = Assert.Single(s.Blocks);
        Assert.Equal(MdKind.Bullet, block.Kind);
        Assert.Equal(new[] { "a", "b", "c" }, block.Lines);
    }

    [Fact]
    public void Bullet_FourLeadingSpaces_IsNotRecognisedAsBullet()
    {
        var s = MarkdownStream.Parse("    - a\n");

        var block = Assert.Single(s.Blocks);
        Assert.Equal(MdKind.Paragraph, block.Kind);
        Assert.Equal("    - a", block.Text);
    }

    [Fact]
    public void NumberList_DotAndParenMarkers_GroupAndStripMarker()
    {
        var s = MarkdownStream.Parse("1. one\n2) two\n3. three\n");

        var block = Assert.Single(s.Blocks);
        Assert.Equal(MdKind.Number, block.Kind);
        Assert.Equal(new[] { "one", "two", "three" }, block.Lines);
    }

    [Fact]
    public void NumberList_FourDigitMarker_IsNotANumberItem()
    {
        var s = MarkdownStream.Parse("1234. text\n");

        var block = Assert.Single(s.Blocks);
        Assert.Equal(MdKind.Paragraph, block.Kind);
    }

    [Fact]
    public void List_DifferentKindImmediatelyAfter_StartsANewBlock()
    {
        var s = MarkdownStream.Parse("- a\n1. b\n");

        Assert.Equal(2, s.Blocks.Count);
        Assert.Equal(MdKind.Bullet, s.Blocks[0].Kind);
        Assert.True(s.Blocks[0].Closed);
        Assert.Equal(MdKind.Number, s.Blocks[1].Kind);
    }

    // ── 引用 ──

    [Fact]
    public void Quote_ConsecutiveLines_GroupIntoOneBlockMarkerStripped()
    {
        var s = MarkdownStream.Parse("> line one\n> line two\n");

        var block = Assert.Single(s.Blocks);
        Assert.Equal(MdKind.Quote, block.Kind);
        Assert.Equal(new[] { "line one", "line two" }, block.Lines);
    }

    // ── 空行 / CRLF / 分割线 ──

    [Fact]
    public void BlankLine_SplitsParagraphsIntoSeparateBlocks()
    {
        var s = MarkdownStream.Parse("first\n\nsecond\n");

        Assert.Equal(2, s.Blocks.Count);
        Assert.Equal("first", s.Blocks[0].Text);
        Assert.True(s.Blocks[0].Closed);
        Assert.Equal("second", s.Blocks[1].Text);
    }

    [Fact]
    public void BlankLine_TwoInARow_ProduceNoExtraBlock()
    {
        var s = MarkdownStream.Parse("first\n\n\nsecond\n");
        Assert.Equal(2, s.Blocks.Count);
    }

    [Fact]
    public void Crlf_BehavesExactlyLikeLf()
    {
        var lf = MarkdownStream.Parse("# T\n\nfirst\nsecond\n\n- a\n- b\n");
        var crlf = MarkdownStream.Parse("# T\r\n\r\nfirst\r\nsecond\r\n\r\n- a\r\n- b\r\n");

        Assert.Equal(lf.Blocks.Count, crlf.Blocks.Count);
        for (int i = 0; i < lf.Blocks.Count; i++)
        {
            Assert.Equal(lf.Blocks[i].Kind, crlf.Blocks[i].Kind);
            Assert.Equal(lf.Blocks[i].Lines, crlf.Blocks[i].Lines);
        }
    }

    [Theory]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("___")]
    [InlineData("----------")]
    public void Divider_VariousMarkersAndLengths_AreSelfClosingDividerBlocks(string line)
    {
        var s = MarkdownStream.Parse(line + "\n");

        var block = Assert.Single(s.Blocks);
        Assert.Equal(MdKind.Divider, block.Kind);
        Assert.True(block.Closed);
    }

    // ── 尾行（未提交半行）── 这是防闪烁的关键部分

    [Theory]
    [InlineData("```py")]
    [InlineData("```")]
    [InlineData("``")]
    [InlineData("`")]
    [InlineData("~~~")]
    [InlineData("~~")]
    [InlineData("~")]
    [InlineData("-")]
    [InlineData("*")]
    [InlineData("+")]
    [InlineData(">")]
    [InlineData("#")]
    [InlineData("##")]
    [InlineData("###")]
    [InlineData("1.")]
    [InlineData("12)")]
    [InlineData("   ")]
    [InlineData("")]
    public void Tail_AmbiguousPrefixes_AreHiddenUntilNewlineResolvesThem(string partial)
    {
        var s = new MarkdownStream();
        var delta = s.Append(partial);

        Assert.Equal(MdTailTarget.None, s.TailTarget);
        Assert.Equal("", s.Tail);
        Assert.Equal(MdTailTarget.None, delta.Tail);
    }

    [Fact]
    public void Tail_PartialWordInsideOpenCodeBlock_IsLastBlockVerbatim()
    {
        var s = new MarkdownStream();
        s.Append("```\nprint(");

        Assert.Equal(MdTailTarget.LastBlock, s.TailTarget);
        Assert.Equal("print(", s.Tail);
    }

    [Fact]
    public void Tail_OrdinarySentence_IsLastBlock_WhenParagraphAlreadyOpen()
    {
        var s = new MarkdownStream();
        s.Append("hello world\nsecond partial");

        Assert.Equal(MdTailTarget.LastBlock, s.TailTarget);
        Assert.Equal("second partial", s.Tail);
    }

    [Fact]
    public void Tail_OrdinarySentence_IsNewParagraph_AfterBlankLine()
    {
        var s = new MarkdownStream();
        s.Append("first\n\nsecond partial");

        Assert.Equal(MdTailTarget.NewParagraph, s.TailTarget);
        Assert.Equal("second partial", s.Tail);
    }

    [Fact]
    public void Tail_NothingOpenYet_IsNewParagraph()
    {
        var s = new MarkdownStream();
        s.Append("hello wor");

        Assert.Equal(MdTailTarget.NewParagraph, s.TailTarget);
        Assert.Equal("hello wor", s.Tail);
    }

    [Fact]
    public void Tail_ClosedCodeFence_DoesNotStayLastBlock()
    {
        var s = new MarkdownStream();
        s.Append("```\ncode\n```\nnext");

        // the code block is closed; a following partial line starts a fresh paragraph, not LastBlock
        Assert.Equal(MdTailTarget.NewParagraph, s.TailTarget);
        Assert.Equal("next", s.Tail);
    }

    // ── MdDelta.FirstChangedBlock ──

    [Fact]
    public void Append_Delta_OnEmptyChunkWithNoBlocksYet_IsZero()
    {
        var s = new MarkdownStream();
        var delta = s.Append("");
        Assert.Equal(0, delta.FirstChangedBlock);
    }

    [Fact]
    public void Append_Delta_PointsAtTheBlockThatActuallyChanged()
    {
        var s = new MarkdownStream();

        var d1 = s.Append("first\n");
        Assert.Equal(0, d1.FirstChangedBlock);

        // closing block 0 (blank line) and opening block 1 both count as changes -> min is 0
        var d2 = s.Append("\nsecond\n");
        Assert.Equal(0, d2.FirstChangedBlock);

        // nothing changes now: fall back to the last existing block's index
        var d3 = s.Append("");
        Assert.Equal(Math.Max(0, s.Blocks.Count - 1), d3.FirstChangedBlock);
        Assert.Equal(1, d3.FirstChangedBlock);
    }

    // ── 增量 Append 与一次性 Parse 等价（属性测试）──

    private const string MultiBlockSample =
        "# Title\n" +
        "\n" +
        "Some paragraph text that keeps going.\n" +
        "Second line of the same paragraph.\n" +
        "\n" +
        "- item one\n" +
        "- item two\n" +
        "+ item three\n" +
        "\n" +
        "1. first\n" +
        "2) second\n" +
        "\n" +
        "> a quote\n" +
        "> continues\n" +
        "\n" +
        "```csharp\n" +
        "var x = 1;\n" +
        "// * not italic in here\n" +
        "```\n" +
        "\n" +
        "---\n" +
        "\n" +
        "trailing partial paragraph with no newline";

    [Fact]
    public void Append_CharByChar_ProducesSameBlocksAsParseOfWholeDocument()
    {
        var whole = MarkdownStream.Parse(MultiBlockSample);

        var incremental = new MarkdownStream();
        foreach (char c in MultiBlockSample)
        {
            incremental.Append(c.ToString());
        }

        AssertSameBlocks(whole.Blocks, incremental.Blocks);
        Assert.Equal(whole.Tail, incremental.Tail);
        Assert.Equal(whole.TailTarget, incremental.TailTarget);
    }

    [Fact]
    public void Append_ArbitraryChunkBoundaries_ProduceSameBlocksAsParseOfWholeDocument()
    {
        var whole = MarkdownStream.Parse(MultiBlockSample);

        // split into uneven chunks (7 chars at a time) instead of one-char-at-a-time,
        // to make sure line/fence detection doesn't depend on a particular chunk size
        var incremental = new MarkdownStream();
        for (int i = 0; i < MultiBlockSample.Length; i += 7)
        {
            int len = Math.Min(7, MultiBlockSample.Length - i);
            incremental.Append(MultiBlockSample.Substring(i, len));
        }

        AssertSameBlocks(whole.Blocks, incremental.Blocks);
    }

    private static void AssertSameBlocks(IReadOnlyList<MdBlock> expected, IReadOnlyList<MdBlock> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Kind, actual[i].Kind);
            Assert.Equal(expected[i].Level, actual[i].Level);
            Assert.Equal(expected[i].Lang, actual[i].Lang);
            Assert.Equal(expected[i].FenceChar, actual[i].FenceChar);
            Assert.Equal(expected[i].FenceLength, actual[i].FenceLength);
            Assert.Equal(expected[i].Closed, actual[i].Closed);
            Assert.Equal(expected[i].Lines, actual[i].Lines);
        }
    }

    // ── 大文档性能冒烟测试 ──

    [Fact]
    public void Parse_100kCharSingleParagraph_CompletesQuickly()
    {
        string big = new string('a', 100_000);
        var sw = Stopwatch.StartNew();

        var s = MarkdownStream.Parse(big + "\n");

        sw.Stop();
        var block = Assert.Single(s.Blocks);
        Assert.Equal(MdKind.Paragraph, block.Kind);
        Assert.Equal(100_000, block.Text.Length);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Parse took {sw.ElapsedMilliseconds}ms, expected roughly linear-time completion");
    }

    // ── 行内解析 ──

    [Fact]
    public void ParseInline_EmptyInput_ReturnsEmptyList()
        => Assert.Empty(MiniMarkdown.ParseInline(""));

    [Fact]
    public void ParseInline_Bold()
    {
        var span = Assert.Single(MiniMarkdown.ParseInline("**b**"));
        Assert.Equal(new MdSpan(MdSpanKind.Bold, "b"), span);
    }

    [Fact]
    public void ParseInline_ItalicStar()
    {
        var span = Assert.Single(MiniMarkdown.ParseInline("*i*"));
        Assert.Equal(new MdSpan(MdSpanKind.Italic, "i"), span);
    }

    [Fact]
    public void ParseInline_ItalicUnderscore()
    {
        var span = Assert.Single(MiniMarkdown.ParseInline("_i_"));
        Assert.Equal(new MdSpan(MdSpanKind.Italic, "i"), span);
    }

    [Fact]
    public void ParseInline_Code()
    {
        var span = Assert.Single(MiniMarkdown.ParseInline("`c`"));
        Assert.Equal(new MdSpan(MdSpanKind.Code, "c"), span);
    }

    [Fact]
    public void ParseInline_CodeContainingAsterisk_IsNotTreatedAsItalic()
    {
        var span = Assert.Single(MiniMarkdown.ParseInline("`a*b*c`"));
        Assert.Equal(new MdSpan(MdSpanKind.Code, "a*b*c"), span);
    }

    [Fact]
    public void ParseInline_UnmatchedDoubleStar_IsEmittedAsLiteralText()
    {
        var span = Assert.Single(MiniMarkdown.ParseInline("**oops"));
        Assert.Equal(new MdSpan(MdSpanKind.Text, "**oops"), span);
    }

    [Fact]
    public void ParseInline_UnmatchedSingleStar_IsEmittedAsLiteralText()
    {
        var span = Assert.Single(MiniMarkdown.ParseInline("*oops"));
        Assert.Equal(new MdSpan(MdSpanKind.Text, "*oops"), span);
    }

    [Theory]
    [InlineData(@"\*not italic\*", "*not italic*")]
    [InlineData(@"\`not code\`", "`not code`")]
    [InlineData(@"\_not italic\_", "_not italic_")]
    [InlineData(@"\\literal backslash", @"\literal backslash")]
    public void ParseInline_Escapes_ProduceLiteralCharactersNotMarkers(string input, string expectedText)
    {
        var span = Assert.Single(MiniMarkdown.ParseInline(input));
        Assert.Equal(new MdSpan(MdSpanKind.Text, expectedText), span);
    }

    [Fact]
    public void ParseInline_EscapeInTheMiddle_StaysMergedWithSurroundingText()
    {
        var span = Assert.Single(MiniMarkdown.ParseInline(@"a\*b"));
        Assert.Equal(new MdSpan(MdSpanKind.Text, "a*b"), span);
    }

    [Fact]
    public void ParseInline_MixedContent_ProducesExpectedSpanSequenceWithMergedText()
    {
        var spans = MiniMarkdown.ParseInline("hi **bold** and *italic* and `code` end");

        Assert.Equal(new[]
        {
            new MdSpan(MdSpanKind.Text, "hi "),
            new MdSpan(MdSpanKind.Bold, "bold"),
            new MdSpan(MdSpanKind.Text, " and "),
            new MdSpan(MdSpanKind.Italic, "italic"),
            new MdSpan(MdSpanKind.Text, " and "),
            new MdSpan(MdSpanKind.Code, "code"),
            new MdSpan(MdSpanKind.Text, " end"),
        }, spans);
    }

    [Fact]
    public void ParseInline_NeverThrows_ForPathologicalMarkerRuns()
    {
        var exception = Record.Exception(() => MiniMarkdown.ParseInline(new string('*', 500) + new string('`', 200)));
        Assert.Null(exception);
    }
}
