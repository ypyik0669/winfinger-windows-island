using System.Text;

namespace WinFinger.Services;

/// <summary>块类型：段落 / 标题 / 代码 / 无序列表 / 有序列表 / 引用 / 分割线。</summary>
public enum MdKind { Paragraph, Heading, Code, Bullet, Number, Quote, Divider }

/// <summary>
/// 一个已提交的 Markdown 块。字段是"能表达所有块类型"的最小公约集：
/// 大多数字段只对特定 Kind 有意义（如 Lang/FenceLength/FenceChar 只用于 Code，Level 只用于 Heading），
/// 其余情况保持类型默认值即可，不需要为每种 Kind 单独建子类——渲染层按 Kind switch 即可。
/// </summary>
public sealed class MdBlock
{
    public MdKind Kind { get; init; }

    /// <summary>围栏代码块的信息字符串（```lang 中的 lang），未指定语言时为空串。</summary>
    public string Lang { get; init; } = "";

    /// <summary>开启围栏的反引号/波浪线数量；闭合围栏的字符数必须 ≥ 此值才算闭合。</summary>
    public int FenceLength { get; init; }

    /// <summary>围栏字符，'`' 或 '~'。</summary>
    public char FenceChar { get; init; }

    /// <summary>标题级别 1..3；非标题块为 0。</summary>
    public int Level { get; init; }

    /// <summary>
    /// 是否已闭合：代码块指"遇到了满足长度的闭合围栏"；标题/分割线创建时即闭合（单行、不可续写）；
    /// 段落/列表/引用在被空行或不同类型的行打断时闭合。闭合后的块不再接受新行。
    /// </summary>
    public bool Closed { get; set; }

    /// <summary>
    /// 原始行内容，保留缩进，不含结尾的 '\n'。列表/引用行已去掉各自的标记前缀（"- "/"1. "/"&gt; " 等），
    /// 代码块的开闭围栏行本身不计入此列表。
    /// </summary>
    public List<string> Lines { get; } = new();

    public string Text => string.Join("\n", Lines);
}

/// <summary>流式渲染时，尾部未提交行应该挂在哪里显示。</summary>
public enum MdTailTarget { None, LastBlock, NewParagraph }

/// <summary>自上一次 Append 以来发生变化的块范围：从 FirstChangedBlock 起到末尾都需要重新渲染。</summary>
public readonly record struct MdDelta(int FirstChangedBlock, MdTailTarget Tail);

/// <summary>行内片段类型：普通文本 / 代码 / 加粗 / 斜体。</summary>
public enum MdSpanKind { Text, Code, Bold, Italic }

/// <summary>一段行内文本及其样式，标记已被解析并剥离，Text 是要显示的内容。</summary>
public readonly record struct MdSpan(MdSpanKind Kind, string Text);

/// <summary>
/// 增量、按行解析的流式 Markdown 解析器：AI 回复是一个字符一个字符（或一小段一小段）到达的，
/// 每次 Append 只应处理"新增的这一点内容 + 当前尚未提交的那一行"，绝不能为了保证结果正确而
/// 回头重扫已经提交的历史块——否则每 ~50ms 触发一次的渲染循环会随着对话变长而越来越卡。
/// 因此块一旦提交（遇到 '\n'）就不再被本类回头修改，只有"当前打开的块"（_current）会被继续追加。
/// </summary>
public sealed class MarkdownStream
{
    private readonly List<MdBlock> _blocks = new();

    /// <summary>尚未遇到 '\n' 的当前行缓冲区；跨多次 Append 调用累积。</summary>
    private readonly StringBuilder _partial = new();

    /// <summary>当前仍在接收新行的块；为 null 表示上一行是空行/自闭合块，还没有块在"打开"状态。</summary>
    private MdBlock? _current;

    public IReadOnlyList<MdBlock> Blocks => _blocks;

    /// <summary>尚未被换行符终止的末尾行内容；仅在"可以安全显示而不会闪烁"时才非空，见 <see cref="TailTarget"/>。</summary>
    public string Tail { get; private set; } = "";

    public MdTailTarget TailTarget { get; private set; } = MdTailTarget.None;

    /// <summary>累计喂入 Append 的字符总数（含所有历史 chunk），用于调用方做粗略的进度/去重判断。</summary>
    public int SourceLength { get; private set; }

    /// <summary>
    /// 追加一段新到达的文本。只扫描 chunk 本身的字符；每完整一行就提交一次（O(1) 摊销），
    /// 复杂度为 O(chunk 长度 + 当前打开块的那一行长度)，与已提交的历史内容长度无关。
    /// 任何输入都不会抛出——找不到匹配就原样当作普通文本处理。
    /// </summary>
    public MdDelta Append(string chunk)
    {
        _firstChanged = int.MaxValue;

        if (!string.IsNullOrEmpty(chunk))
        {
            SourceLength += chunk.Length;
            for (int i = 0; i < chunk.Length; i++)
            {
                char ch = chunk[i];
                if (ch == '\r') continue; // CRLF 与 LF 等价：'\r' 一律丢弃，不进入缓冲区也不单独触发提交
                if (ch == '\n')
                {
                    string line = _partial.ToString();
                    _partial.Clear();
                    CommitLine(line);
                }
                else
                {
                    _partial.Append(ch);
                }
            }
        }

        string tailText = _partial.ToString();
        (Tail, TailTarget) = ComputeTail(tailText);

        int first = _firstChanged == int.MaxValue ? Math.Max(0, _blocks.Count - 1) : _firstChanged;
        return new MdDelta(first, TailTarget);
    }

    /// <summary>一次性解析完整文本：等价于 new + Append(full)。</summary>
    public static MarkdownStream Parse(string full)
    {
        var stream = new MarkdownStream();
        stream.Append(full);
        return stream;
    }

    // ── 增量状态机 ──

    /// <summary>本次 Append 中最早被修改/新增的块下标；int.MaxValue 表示"本次没有任何块变化"，对外从不暴露该哨兵值。</summary>
    private int _firstChanged = int.MaxValue;

    private void NoteChanged(int index)
    {
        if (index < _firstChanged) _firstChanged = index;
    }

    /// <summary>
    /// 把当前打开的块标记为闭合并清空指针；非代码块的"闭合"只是"不再接受新行"，内容保持不变。
    /// Closed 由 false 变 true 本身也算一次变化（调用方可能据此摘掉"仍在输出中"的视觉状态），所以要 NoteChanged。
    /// </summary>
    private void CloseCurrent()
    {
        if (_current != null)
        {
            _current.Closed = true;
            NoteChanged(_blocks.Count - 1);
            _current = null;
        }
    }

    /// <summary>段落/列表/引用共用的"续写或新开一块"逻辑：类型相同就续写，否则先闭合旧块再开新块。</summary>
    private void OpenOrAppend(MdKind kind, string text)
    {
        if (_current is { Closed: false } cur && cur.Kind == kind)
        {
            cur.Lines.Add(text);
            NoteChanged(_blocks.Count - 1);
            return;
        }

        CloseCurrent();
        var block = new MdBlock { Kind = kind };
        block.Lines.Add(text);
        _blocks.Add(block);
        NoteChanged(_blocks.Count - 1);
        _current = block;
    }

    /// <summary>提交一整行（不含 '\n'）。这是唯一会修改 Blocks 的入口。</summary>
    private void CommitLine(string line)
    {
        // 未闭合的代码块优先级最高：里面的每一行都是字面内容，直到遇到满足长度的闭合围栏为止。
        if (_current is { Kind: MdKind.Code, Closed: false } codeBlock)
        {
            if (TryMatchClosingFence(line, codeBlock.FenceChar, codeBlock.FenceLength))
            {
                codeBlock.Closed = true;
                NoteChanged(_blocks.Count - 1);
                _current = null;
            }
            else
            {
                codeBlock.Lines.Add(line);
                NoteChanged(_blocks.Count - 1);
            }
            return;
        }

        if (IsBlankLine(line))
        {
            CloseCurrent();
            return;
        }

        if (TryMatchFenceOpen(line, out char fenceChar, out int fenceLen, out string lang))
        {
            CloseCurrent();
            var block = new MdBlock { Kind = MdKind.Code, FenceChar = fenceChar, FenceLength = fenceLen, Lang = lang };
            _blocks.Add(block);
            NoteChanged(_blocks.Count - 1);
            _current = block; // 保持打开，等待闭合围栏
            return;
        }

        if (IsDividerLine(line))
        {
            CloseCurrent();
            _blocks.Add(new MdBlock { Kind = MdKind.Divider, Closed = true });
            NoteChanged(_blocks.Count - 1);
            return;
        }

        if (TryMatchHeading(line, out int level, out string headingText))
        {
            CloseCurrent();
            var block = new MdBlock { Kind = MdKind.Heading, Level = level, Closed = true };
            block.Lines.Add(headingText);
            _blocks.Add(block);
            NoteChanged(_blocks.Count - 1);
            return;
        }

        if (TryMatchBulletOrNumber(line, out MdKind listKind, out string itemText))
        {
            OpenOrAppend(listKind, itemText);
            return;
        }

        if (TryMatchQuote(line, out string quoteText))
        {
            OpenOrAppend(MdKind.Quote, quoteText);
            return;
        }

        OpenOrAppend(MdKind.Paragraph, line);
    }

    // ── 尾行（未提交的半行）计算：防止流式渲染时闪烁 ──

    /// <summary>
    /// 根据当前状态和尚未提交的半行内容，决定这半行该怎么显示。
    /// 顺序即优先级：未闭合代码块（逐字符透传）→ 语义歧义（先隐藏）→ 续写现有块 → 独立新段落。
    /// </summary>
    private (string, MdTailTarget) ComputeTail(string partial)
    {
        if (_current is { Kind: MdKind.Code, Closed: false })
        {
            return (partial, MdTailTarget.LastBlock);
        }

        if (IsAmbiguousTail(partial))
        {
            return ("", MdTailTarget.None);
        }

        if (_current is { Closed: false } cur &&
            cur.Kind is MdKind.Paragraph or MdKind.Bullet or MdKind.Number or MdKind.Quote)
        {
            string shown = partial;
            if (cur.Kind is MdKind.Bullet or MdKind.Number &&
                TryMatchBulletOrNumber(partial, out MdKind k, out string stripped) && k == cur.Kind)
            {
                shown = stripped;
            }
            return (shown, MdTailTarget.LastBlock);
        }

        return (partial, MdTailTarget.NewParagraph);
    }

    /// <summary>
    /// 半行是否"语义歧义"：再多打几个字符就可能整体变成另一种块类型，此时先什么都不显示，
    /// 好过闪一下再变（典型例子：流式输出到 "```py" 时不能先当普通段落文字露出来，等换行后才知道是代码块）。
    /// </summary>
    private static bool IsAmbiguousTail(string s)
    {
        if (IsBlankLine(s)) return true;

        string t = s.Trim();
        if (t.Length == 0) return true;

        char c0 = t[0];
        if (c0 == '`' || c0 == '~')
        {
            int i = 0;
            while (i < t.Length && t[i] == c0) i++;
            if (i >= 3) return true;        // "```" / "~~~" 及更多：即将/已经构成围栏起始
            if (i == t.Length) return true; // 整行就是 1-2 个围栏字符，还没到能判断的地步
            return false;
        }

        if (t is "-" or "*" or "+" or ">" or "#" or "##" or "###") return true;

        return IsBareOrdinalPrefix(t);
    }

    /// <summary>整个 trim 后的字符串是否恰好是"1-3 位数字 + '.'/')'"，后面什么都没有（有序列表标记打了一半）。</summary>
    private static bool IsBareOrdinalPrefix(string t)
    {
        int i = 0, digits = 0;
        while (i < t.Length && digits < 3 && char.IsAsciiDigit(t[i]))
        {
            i++;
            digits++;
        }
        if (digits == 0 || i >= t.Length) return false;
        if (t[i] != '.' && t[i] != ')') return false;
        return i + 1 == t.Length;
    }

    // ── 单行匹配：都只看"这一行"，不看历史，天然满足增量复杂度要求 ──

    private static bool IsBlankLine(string s)
    {
        for (int i = 0; i < s.Length; i++)
            if (!char.IsWhiteSpace(s[i])) return false;
        return true;
    }

    private static bool TryMatchFenceOpen(string s, out char fenceChar, out int fenceLen, out string lang)
    {
        fenceChar = '\0';
        fenceLen = 0;
        lang = "";
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        if (i >= s.Length) return false;

        char c = s[i];
        if (c != '`' && c != '~') return false;

        int start = i;
        while (i < s.Length && s[i] == c) i++;
        int count = i - start;
        if (count < 3) return false;

        fenceChar = c;
        fenceLen = count;
        lang = s[i..].Trim();
        return true;
    }

    /// <summary>闭合围栏：去掉首尾空白后必须全部是同一种围栏字符，且数量 ≥ 开启时的长度。</summary>
    private static bool TryMatchClosingFence(string s, char fenceChar, int fenceLen)
    {
        int i = 0, n = s.Length;
        while (i < n && (s[i] == ' ' || s[i] == '\t')) i++;
        int start = i;
        while (i < n && s[i] == fenceChar) i++;
        int count = i - start;
        if (count == 0 || count < fenceLen) return false;
        while (i < n)
        {
            if (s[i] != ' ' && s[i] != '\t') return false;
            i++;
        }
        return true;
    }

    private static bool IsDividerLine(string s)
    {
        int start = 0, end = s.Length - 1;
        while (start <= end && char.IsWhiteSpace(s[start])) start++;
        while (end >= start && char.IsWhiteSpace(s[end])) end--;
        int len = end - start + 1;
        if (len < 3) return false;

        char c = s[start];
        if (c != '-' && c != '*' && c != '_') return false;
        for (int i = start; i <= end; i++)
            if (s[i] != c) return false;
        return true;
    }

    private static bool TryMatchHeading(string s, out int level, out string text)
    {
        level = 0;
        text = "";
        int i = 0;
        while (i < s.Length && s[i] == '#') i++;
        if (i is 0 or > 3) return false;      // 0 个（不是标题）或 4+ 个（超过 h3）都退化为段落
        if (i >= s.Length || s[i] != ' ') return false;
        level = i;
        text = s[(i + 1)..];
        return true;
    }

    /// <summary>无序/有序列表项：允许 0-3 个前导空格，标记后必须紧跟一个空格；返回值已剥离标记。</summary>
    private static bool TryMatchBulletOrNumber(string s, out MdKind kind, out string text)
    {
        kind = default;
        text = "";
        int i = 0, spaces = 0;
        while (i < s.Length && s[i] == ' ' && spaces < 3)
        {
            i++;
            spaces++;
        }
        if (i >= s.Length) return false;

        char c = s[i];
        if (c == '-' || c == '*' || c == '+')
        {
            if (i + 1 < s.Length && s[i + 1] == ' ')
            {
                kind = MdKind.Bullet;
                text = s[(i + 2)..];
                return true;
            }
            return false;
        }

        if (char.IsAsciiDigit(c))
        {
            int digits = 0;
            while (i < s.Length && digits < 3 && char.IsAsciiDigit(s[i]))
            {
                i++;
                digits++;
            }
            if (i < s.Length && (s[i] == '.' || s[i] == ')') && i + 1 < s.Length && s[i + 1] == ' ')
            {
                kind = MdKind.Number;
                text = s[(i + 2)..];
                return true;
            }
            return false;
        }

        return false;
    }

    private static bool TryMatchQuote(string s, out string text)
    {
        text = "";
        if (s.Length >= 2 && s[0] == '>' && s[1] == ' ')
        {
            text = s[2..];
            return true;
        }
        return false;
    }
}

/// <summary>无状态的行内 Markdown 解析：加粗/斜体/行内代码，用于把一个已提交的块渲染成富文本片段。</summary>
public static class MiniMarkdown
{
    /// <summary>
    /// 单趟扫描，从左到右，绝不抛出。优先级：转义 &gt; 行内代码（内容不再二次解析，反引号里的 * 不会被当成斜体）
    /// &gt; 加粗 &gt; 斜体。找不到配对的标记就地降级为普通文本并继续扫描——因此永远不会因为一个孤立的 "*" 而丢内容。
    /// 相邻的普通文本天然合并（同一个缓冲区持续追加，只有遇到真正的标记span才 flush），不需要额外的合并步骤。
    /// </summary>
    public static List<MdSpan> ParseInline(string text)
    {
        var result = new List<MdSpan>();
        if (string.IsNullOrEmpty(text)) return result;

        var buf = new StringBuilder();
        int i = 0;
        int n = text.Length;

        while (i < n)
        {
            char c = text[i];

            if (c == '\\' && i + 1 < n && IsEscapable(text[i + 1]))
            {
                buf.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                int close = text.IndexOf('`', i + 1);
                if (close >= 0)
                {
                    FlushText(buf, result);
                    result.Add(new MdSpan(MdSpanKind.Code, text.Substring(i + 1, close - i - 1)));
                    i = close + 1;
                    continue;
                }
                buf.Append(c);
                i++;
                continue;
            }

            if ((c == '*' || c == '_') && i + 1 < n && text[i + 1] == c
                && i + 2 < n && !char.IsWhiteSpace(text[i + 2])
                && !(c == '_' && i > 0 && IsWordChar(text[i - 1])))
            {
                int close = IndexOfDouble(text, i + 2, c);
                if (close >= 0)
                {
                    FlushText(buf, result);
                    result.Add(new MdSpan(MdSpanKind.Bold, text.Substring(i + 2, close - i - 2)));
                    i = close + 2;
                    continue;
                }
                buf.Append(c);
                i++;
                continue;
            }

            if (c == '*' || c == '_')
            {
                int close = FindEmphasisClose(text, i, c);
                if (close > i + 1)
                {
                    FlushText(buf, result);
                    result.Add(new MdSpan(MdSpanKind.Italic, text.Substring(i + 1, close - i - 1)));
                    i = close + 1;
                    continue;
                }
                buf.Append(c);
                i++;
                continue;
            }

            buf.Append(c);
            i++;
        }

        FlushText(buf, result);
        return result;
    }

    /// <summary>
    /// 找配对的斜体标记，用的是 CommonMark 定界规则的简化版：
    /// `_` 只有在词的边界上才算标记——不然 MAX_BUFFER_SIZE、foo_bar_baz 这类标识符
    /// 会被吃掉下划线变成斜体（模型写代码相关内容时非常常见）；
    /// 另外开标记后面不能是空白、闭标记前面不能是空白，这样 "2 * 3 ... 4 * 5" 里的乘号也不会配上对。
    /// </summary>
    private static int FindEmphasisClose(string text, int open, char marker)
    {
        if (open + 1 >= text.Length) return -1;
        if (char.IsWhiteSpace(text[open + 1])) return -1;
        if (marker == '_' && open > 0 && IsWordChar(text[open - 1])) return -1;

        for (int i = open + 1; i < text.Length; i++)
        {
            if (text[i] != marker) continue;
            if (char.IsWhiteSpace(text[i - 1])) continue;
            if (marker == '_' && i + 1 < text.Length && IsWordChar(text[i + 1])) continue;
            return i;
        }
        return -1;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static void FlushText(StringBuilder buf, List<MdSpan> result)
    {
        if (buf.Length == 0) return;
        result.Add(new MdSpan(MdSpanKind.Text, buf.ToString()));
        buf.Clear();
    }

    private static bool IsEscapable(char c) => c is '*' or '`' or '_' or '\\';

    /// <summary>
    /// 找配对的双标记（** 或 __）：闭标记前不能是空白（"2 ** 3 ... 4 ** 5" 里的乘方不该配成粗体），
    /// __ 同样只认词边界，别把 foo__bar__baz 当成粗体。
    /// </summary>
    private static int IndexOfDouble(string s, int start, char marker)
    {
        for (int k = start; k < s.Length - 1; k++)
        {
            if (s[k] != marker || s[k + 1] != marker) continue;
            if (k > 0 && char.IsWhiteSpace(s[k - 1])) continue;
            if (marker == '_' && k + 2 < s.Length && IsWordChar(s[k + 2])) continue;
            return k;
        }
        return -1;
    }
}
