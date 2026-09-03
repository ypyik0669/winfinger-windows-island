using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WinFinger.Services;

namespace WinFinger.Controls;

/// <summary>
/// 把 AI 回复按轻量 markdown 渲染。为流式而生：文本只会在末尾增长，
/// 所以每次刷新只重画「上次之后变过的块」和那半行还没收完的尾巴，
/// 已经收口的段落 / 代码块原地不动（整棵树重建的话，50ms 一次会把界面拖死）。
/// </summary>
public sealed class MarkdownView : ContentControl
{
    /// <summary>单个未闭合段落超过这个长度就不再重排行内样式，直接追加纯文本，收口时再解析一次。</summary>
    private const int InlineParseLimit = 4000;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(MarkdownView),
        new PropertyMetadata("", (d, e) => ((MarkdownView)d).OnSourceChanged((string?)e.NewValue ?? "")));

    private readonly StackPanel _stack = new();
    private readonly List<MdKind> _kinds = new();
    private MarkdownStream _md = new();
    private FrameworkElement? _tailElement;
    private string _rendered = "";

    public MarkdownView()
    {
        Content = _stack;
        Focusable = false;
    }

    public string Source
    {
        get => (string)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private void OnSourceChanged(string text)
    {
        // 流式时文本只增不减：只把新增的一段喂给解析器；其它情况（切换消息、加载历史）整篇重来
        if (text.Length >= _rendered.Length && text.StartsWith(_rendered, StringComparison.Ordinal))
        {
            if (text.Length == _rendered.Length) return;
            var delta = _md.Append(text[_rendered.Length..]);
            _rendered = text;
            Update(delta.FirstChangedBlock);
            return;
        }

        _md = new MarkdownStream();
        var full = _md.Append(text);
        _rendered = text;
        _stack.Children.Clear();
        _kinds.Clear();
        _tailElement = null;
        Update(0);
        _ = full;
    }

    private void Update(int firstChanged)
    {
        if (_tailElement is not null)
        {
            _stack.Children.Remove(_tailElement);
            _tailElement = null;
        }

        var blocks = _md.Blocks;
        int start = Math.Max(0, Math.Min(firstChanged, blocks.Count));
        for (int i = start; i < blocks.Count; i++)
        {
            var block = blocks[i];
            string? tail = i == blocks.Count - 1 && _md.TailTarget == MdTailTarget.LastBlock ? _md.Tail : null;
            if (i < _stack.Children.Count && i < _kinds.Count && _kinds[i] == block.Kind &&
                _stack.Children[i] is FrameworkElement existing && TryUpdate(existing, block, tail))
                continue;

            TruncateFrom(i);
            var element = Build(block, tail);
            _stack.Children.Add(element);
            _kinds.Add(block.Kind);
        }
        TruncateFrom(blocks.Count);

        if (_md.TailTarget == MdTailTarget.NewParagraph && _md.Tail.Length > 0)
        {
            _tailElement = BuildParagraph(_md.Tail, MdKind.Paragraph, 0);
            _stack.Children.Add(_tailElement);
        }
    }

    private void TruncateFrom(int index)
    {
        while (_stack.Children.Count > index)
        {
            _stack.Children.RemoveAt(_stack.Children.Count - 1);
            if (_kinds.Count > index) _kinds.RemoveAt(_kinds.Count - 1);
        }
        while (_kinds.Count > index) _kinds.RemoveAt(_kinds.Count - 1);
    }

    /// <summary>同类型的块就地改内容，省掉一次元素重建（流式时每 50ms 都会走到这里）。</summary>
    private static bool TryUpdate(FrameworkElement element, MdBlock block, string? tail)
    {
        string text = Compose(block, tail);
        switch (element)
        {
            case TextBlock textBlock when block.Kind is MdKind.Paragraph or MdKind.Heading or MdKind.Quote:
                FillInlines(textBlock, text);
                return true;
            case StackPanel list when block.Kind is MdKind.Bullet or MdKind.Number:
                FillList(list, block, tail);
                return true;
            case Border { Tag: TextBlock code } when block.Kind == MdKind.Code:
                code.Text = text;
                return true;
            case Border when block.Kind == MdKind.Divider:
                return true;
            default:
                return false;
        }
    }

    private static string Compose(MdBlock block, string? tail)
    {
        string text = block.Text;
        if (tail is not { Length: > 0 }) return text;
        // 代码块里正在打的那行如果只是闭合围栏的前几个字符，先别显示，否则收尾时会闪一下围栏符号
        if (block.Kind == MdKind.Code && tail.Trim().All(c => c is '`' or '~')) return text;
        return text.Length == 0 ? tail : text + (block.Kind == MdKind.Code ? "\n" : "") + tail;
    }

    private FrameworkElement Build(MdBlock block, string? tail)
    {
        string text = Compose(block, tail);
        return block.Kind switch
        {
            MdKind.Code => BuildCode(text, block.Lang),
            MdKind.Divider => BuildDivider(),
            MdKind.Bullet or MdKind.Number => BuildList(block, tail),
            _ => BuildParagraph(text, block.Kind, block.Level)
        };
    }

    private static TextBlock BuildParagraph(string text, MdKind kind, int level)
    {
        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
            FontSize = kind == MdKind.Heading ? level switch { 1 => 17, 2 => 15, _ => 14 } : 13,
            FontWeight = kind == MdKind.Heading ? FontWeights.SemiBold : FontWeights.Normal
        };
        textBlock.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Text");
        textBlock.SetResourceReference(TextBlock.ForegroundProperty,
            kind == MdKind.Quote ? "Brush.TextSecondary" : "Brush.TextPrimary");
        FillInlines(textBlock, text);
        return textBlock;
    }

    /// <summary>一个列表块里每行是一个条目，各自一行（否则只有第一条前面有符号）。</summary>
    private static StackPanel BuildList(MdBlock block, string? tail)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        FillList(panel, block, tail);
        return panel;
    }

    private static void FillList(StackPanel panel, MdBlock block, string? tail)
    {
        var lines = new List<string>(block.Lines);
        if (tail is { Length: > 0 }) lines.Add(tail);

        // 行数没变就只改文字，避免流式时每帧重建整个列表
        if (panel.Children.Count == lines.Count)
        {
            for (int i = 0; i < lines.Count; i++)
                if (panel.Children[i] is Grid { Tag: TextBlock body }) FillInlines(body, lines[i]);
            return;
        }

        panel.Children.Clear();
        for (int i = 0; i < lines.Count; i++)
            panel.Children.Add(BuildListItem(lines[i], block, i + 1));
    }

    private static Grid BuildListItem(string text, MdBlock block, int index)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var marker = new TextBlock
        {
            Text = block.Kind == MdKind.Bullet ? "·" : index + ".",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Top
        };
        marker.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
        marker.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Text");
        Grid.SetColumn(marker, 0);

        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        body.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Text");
        body.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
        FillInlines(body, text);
        Grid.SetColumn(body, 1);

        grid.Children.Add(marker);
        grid.Children.Add(body);
        grid.Tag = body;
        return grid;
    }

    private Border BuildCode(string text, string lang)
    {
        var body = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.NoWrap,
            FontSize = 12
        };
        body.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
        body.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");

        var scroller = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, lang.Length > 0 ? 4 : 0, 0, 0)
        };
        // 代码块自己的横向滚动条会吞掉滚轮，鼠标停在代码上时整个对话就滚不动了，转发给外层
        scroller.PreviewMouseWheel += (s, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            if (VisualTreeHelper.GetParent(scroller) is UIElement parent)
                parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = MouseWheelEvent,
                    Source = scroller
                });
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        var label = new TextBlock { Text = lang, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
        label.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
        var copy = new Button
        {
            Content = "\uE8C8",
            Width = 22,
            Height = 22,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "复制代码"
        };
        copy.SetResourceReference(StyleProperty, "Button.Icon");
        copy.Click += (_, _) => CopyHandler?.Invoke(body.Text);
        header.Children.Add(label);
        header.Children.Add(copy);
        Grid.SetRow(header, 0);
        Grid.SetRow(scroller, 1);
        grid.Children.Add(header);
        grid.Children.Add(scroller);

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 2, 0, 8),
            BorderThickness = new Thickness(1),
            Tag = body
        };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Fill");
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Stroke");
        border.Child = grid;
        return border;
    }

    private static Border BuildDivider()
    {
        var border = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 8) };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Hairline");
        return border;
    }

    /// <summary>行内样式：超长的未闭合段落直接放纯文本，等收口再解析，避免每帧重排几千字。</summary>
    private static void FillInlines(TextBlock target, string text)
    {
        target.Inlines.Clear();
        if (text.Length > InlineParseLimit)
        {
            target.Inlines.Add(new Run(text));
            return;
        }

        foreach (var span in MiniMarkdown.ParseInline(text))
        {
            switch (span.Kind)
            {
                case MdSpanKind.Bold:
                    target.Inlines.Add(new Bold(new Run(span.Text)));
                    break;
                case MdSpanKind.Italic:
                    target.Inlines.Add(new Italic(new Run(span.Text)));
                    break;
                case MdSpanKind.Code:
                    var run = new Run(span.Text);
                    run.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
                    run.SetResourceReference(TextElement.BackgroundProperty, "Brush.Fill");
                    target.Inlines.Add(run);
                    break;
                default:
                    target.Inlines.Add(new Run(span.Text));
                    break;
            }
        }
    }

    /// <summary>
    /// 代码块复制按钮的落点。控件是在 DataTemplate 里成批创建的，没法逐个挂事件，
    /// 所以用一个全局钩子：对话页初始化时接上剪贴板服务。
    /// </summary>
    public static Action<string>? CopyHandler { get; set; }
}
