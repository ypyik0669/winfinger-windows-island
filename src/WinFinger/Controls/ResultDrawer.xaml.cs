using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinFinger.Models;
using WinFinger.Services;
using WinFinger.ViewModels;

namespace WinFinger.Controls;

/// <summary>剪贴板页底部的结果抽屉：文本 / 图片 / 颜色 / 提示四种形态，底部按钮按 <see cref="ResultActions"/> 生成。</summary>
public partial class ResultDrawer : UserControl, IResultPresenter
{
    private const double MaxDrawerHeight = 260;

    private AppViewModel? _model;
    private readonly DispatcherTimer _flushTimer;
    private readonly StringBuilder _pending = new();
    private readonly StringBuilder _full = new();
    private CancellationTokenSource? _streamCts;
    private ClipboardEntry? _sourceEntry;
    private ResultActions _actions;
    private byte[]? _imageBytes;
    private BitmapSource? _image;
    private bool _streaming;

    public ResultDrawer()
    {
        InitializeComponent();
        _flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _flushTimer.Tick += (_, _) => Flush();
        CloseButton.Click += (_, _) => Close();
        StopButton.Click += (_, _) => StopStreaming();
    }

    public void Attach(AppViewModel model) => _model = model;

    public bool IsOpen { get; private set; }

    // ── IResultPresenter ──

    public void ShowText(string title, string text, ResultActions actions, ClipboardEntry? sourceEntry = null)
    {
        Dispatch(() =>
        {
            Reset(title, actions, sourceEntry);
            _full.Append(text);
            TextBody.Text = text;
            TextBody.Visibility = Visibility.Visible;
            HeaderStatus.Text = $"{text.Length} 字符";
            BuildFooter();
            Open();
        });
    }

    public void ShowStreaming(string title, ResultActions actions, CancellationTokenSource cts, ClipboardEntry? sourceEntry = null)
    {
        Dispatch(() =>
        {
            Reset(title, actions, sourceEntry);
            _streamCts = cts;
            _streaming = true;
            TextBody.Text = "";
            TextBody.Visibility = Visibility.Visible;
            StreamBar.Visibility = Visibility.Visible;
            StopButton.Visibility = Visibility.Visible;
            HeaderStatus.Text = "生成中…";
            BuildFooter();
            Open();
            _flushTimer.Start();
        });
    }

    public void AppendChunk(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        Dispatch(() =>
        {
            if (!_streaming) return;
            _pending.Append(chunk);
        });
    }

    public void Complete(string? error)
    {
        Dispatch(() =>
        {
            _flushTimer.Stop();
            Flush();
            _streaming = false;
            _streamCts = null;
            StreamBar.Visibility = Visibility.Collapsed;
            StopButton.Visibility = Visibility.Collapsed;
            if (!string.IsNullOrEmpty(error))
            {
                HeaderStatus.Text = error;
                if (_full.Length == 0)
                {
                    TextBody.Visibility = Visibility.Collapsed;
                    MessageText.Text = error;
                    MessageBody.Visibility = Visibility.Visible;
                    Footer.Children.Clear();
                }
            }
            else
            {
                HeaderStatus.Text = $"{_full.Length} 字符";
            }
            BuildFooter();
        });
    }

    public void ShowImage(string title, BitmapSource image, ResultActions actions, byte[]? pngBytes = null)
    {
        Dispatch(() =>
        {
            Reset(title, actions, null);
            _image = image;
            _imageBytes = pngBytes;
            ResultImage.Source = image;
            ImageSizeLabel.Text = $"{image.PixelWidth} × {image.PixelHeight}";
            ImageBody.Visibility = Visibility.Visible;
            HeaderStatus.Text = "";
            BuildFooter();
            Open();
        });
    }

    public void ShowColor(string title, Color color, string hex, string rgb, string hsl)
    {
        Dispatch(() =>
        {
            Reset(title, ResultActions.None, null);
            ColorSwatch.Background = new SolidColorBrush(color);
            ColorRows.Children.Clear();
            ColorRows.Children.Add(ColorRow("HEX", hex));
            ColorRows.Children.Add(ColorRow("RGB", rgb));
            ColorRows.Children.Add(ColorRow("HSL", hsl));
            ColorBody.Visibility = Visibility.Visible;
            HeaderStatus.Text = "";
            BuildFooter();
            Open();
        });
    }

    public void ShowMessage(string title, string message, (string Label, Action Run)? cta = null)
    {
        Dispatch(() =>
        {
            Reset(title, ResultActions.None, null);
            MessageText.Text = message;
            MessageBody.Visibility = Visibility.Visible;
            if (cta is { } action)
            {
                MessageCta.Content = action.Label;
                MessageCta.Visibility = Visibility.Visible;
                MessageCta.Click -= OnCtaClick;
                _cta = action.Run;
                MessageCta.Click += OnCtaClick;
            }
            HeaderStatus.Text = "";
            BuildFooter();
            Open();
        });
    }

    private Action? _cta;

    private void OnCtaClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _cta?.Invoke();
        }
        catch
        {
            // CTA 不能拖垮抽屉
        }
    }

    public void Close()
    {
        Dispatch(() =>
        {
            StopStreaming();
            _flushTimer.Stop();
            if (!IsOpen)
            {
                Visibility = Visibility.Collapsed;
                return;
            }
            IsOpen = false;
            var anim = new DoubleAnimation(ActualHeight, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            anim.Completed += (_, _) =>
            {
                if (IsOpen) return;
                BeginAnimation(HeightProperty, null);
                Height = double.NaN;
                Visibility = Visibility.Collapsed;
            };
            BeginAnimation(HeightProperty, anim);
        });
    }

    // ── internals ──

    private void Dispatch(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private void Reset(string title, ResultActions actions, ClipboardEntry? sourceEntry)
    {
        StopStreaming();
        _flushTimer.Stop();
        _pending.Clear();
        _full.Clear();
        _streaming = false;
        _image = null;
        _imageBytes = null;
        _cta = null;
        _actions = actions;
        _sourceEntry = sourceEntry;
        HeaderTitle.Text = title;
        HeaderStatus.Text = "";
        StreamBar.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Collapsed;
        TextBody.Visibility = Visibility.Collapsed;
        ImageBody.Visibility = Visibility.Collapsed;
        ColorBody.Visibility = Visibility.Collapsed;
        MessageBody.Visibility = Visibility.Collapsed;
        MessageCta.Visibility = Visibility.Collapsed;
        Footer.Children.Clear();
    }

    private void StopStreaming()
    {
        var cts = _streamCts;
        _streamCts = null;
        if (cts is null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 请求已经结束
        }
        _flushTimer.Stop();
        Flush();
        _streaming = false;
        StreamBar.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Collapsed;
    }

    private void Flush()
    {
        if (_pending.Length == 0) return;
        string chunk = _pending.ToString();
        _pending.Clear();
        _full.Append(chunk);
        TextBody.AppendText(chunk);
        TextBody.ScrollToEnd();
        HeaderStatus.Text = _streaming ? $"生成中… {_full.Length} 字符" : $"{_full.Length} 字符";
    }

    private void Open()
    {
        Visibility = Visibility.Visible;
        if (IsOpen) return;
        IsOpen = true;
        double width = ActualWidth > 0 ? ActualWidth : 560;
        Measure(new Size(width, double.PositiveInfinity));
        double target = Math.Min(MaxDrawerHeight, Math.Max(90, Root.DesiredSize.Height));
        var anim = new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) =>
        {
            if (!IsOpen) return;
            BeginAnimation(HeightProperty, null);
            Height = double.NaN; // 之后随内容自适应，上限由 MaxHeight 控制
        };
        BeginAnimation(HeightProperty, anim);
    }

    private string ResultText => _full.ToString();

    private FrameworkElement ColorRow(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = label,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)FindResource("Font.Text")
        };
        name.SetResourceReference(ForegroundProperty, "Brush.TextTertiary");
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        var text = new TextBlock
        {
            Text = value,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)FindResource("Font.Text")
        };
        text.SetResourceReference(ForegroundProperty, "Brush.TextPrimary");
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var copy = new Button
        {
            Style = (Style)FindResource("Button.Icon"),
            Content = "",
            Width = 24,
            Height = 24,
            ToolTip = $"复制 {label}"
        };
        copy.Click += (_, _) => CopyToClipboard(value);
        Grid.SetColumn(copy, 2);
        grid.Children.Add(copy);
        return grid;
    }

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _model?.ClipboardMonitor.CopyText(text);
        _model?.Notifications.Post("📋", "已复制");
    }

    // ── footer ──

    private void BuildFooter()
    {
        Footer.Children.Clear();
        if (_actions == ResultActions.None) return;
        bool hasText = _full.Length > 0;

        if (_actions.HasFlag(ResultActions.Copy))
            Footer.Children.Add(FooterButton("复制", "Button.Primary", () =>
            {
                if (_imageBytes is { Length: > 0 }) { _model?.ClipboardMonitor.CopyPng(_imageBytes); _model?.Notifications.Post("📋", "已复制图片"); }
                else CopyToClipboard(ResultText);
            }, hasText || _imageBytes is { Length: > 0 }));

        if (_actions.HasFlag(ResultActions.Paste))
            Footer.Children.Add(FooterButton("粘贴", "Button.Secondary", () =>
            {
                string text = ResultText;
                if (_model is not null && text.Length > 0) _ = _model.Paste.PasteTextAsync(text);
            }, hasText));

        if (_actions.HasFlag(ResultActions.ReplaceEntry) && _sourceEntry is not null)
            Footer.Children.Add(FooterButton("替换为条目", "Button.Secondary", () =>
            {
                if (_model is null || _sourceEntry is null) return;
                _model.ClipboardStore.UpdateText(_sourceEntry, ResultText);
                _model.Notifications.Post("📋", "条目已更新");
            }, hasText));

        if (_actions.HasFlag(ResultActions.AppendEntry))
            Footer.Children.Add(FooterButton("追加为新条目", "Button.Secondary", () =>
            {
                if (_model is null) return;
                _model.ClipboardStore.AppendText(ResultText, "动作", "winfinger.action");
                _model.Notifications.Post("📋", "已添加到剪贴板历史");
            }, hasText));

        if (_actions.HasFlag(ResultActions.SaveFile))
            Footer.Children.Add(FooterButton("保存文件", "Button.Secondary", SaveResult,
                hasText || _imageBytes is { Length: > 0 } || _image is not null));

        if (_actions.HasFlag(ResultActions.OpenUrl))
            Footer.Children.Add(FooterButton("打开链接", "Button.Secondary", () =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(ResultText.Trim()) { UseShellExecute = true });
                }
                catch
                {
                    _model?.Notifications.Post("📋", "打开失败");
                }
            }, hasText));

        if (_actions.HasFlag(ResultActions.Translate))
            Footer.Children.Add(FooterButton("翻译", "Button.Secondary", () =>
            {
                string text = ResultText;
                if (_model?.Executor is null || text.Length == 0) return;
                string target = _model.SettingsStore.Settings.AiTargetLanguage;
                _ = _model.Executor.RunAiAsync("翻译", AiService.TranslateSystemPrompt,
                    AiService.BuildTranslatePrompt(text, target), null);
            }, hasText && !_streaming));

        if (_actions.HasFlag(ResultActions.Ai))
        {
            var button = FooterButton("AI ▾", "Button.Secondary", null, hasText && !_streaming);
            button.Click += (_, _) => ShowAiMenu(button);
            Footer.Children.Add(button);
        }
    }

    private Button FooterButton(string label, string styleKey, Action? run, bool enabled)
    {
        var button = new Button
        {
            Content = label,
            Style = (Style)FindResource(styleKey),
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = enabled
        };
        if (run is not null)
            button.Click += (_, _) =>
            {
                try
                {
                    run();
                }
                catch
                {
                    _model?.Notifications.Post("📋", "操作失败");
                }
            };
        return button;
    }

    private void ShowAiMenu(Button anchor)
    {
        if (_model is null) return;
        string text = ResultText;
        if (text.Length == 0) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var temp = new ClipboardEntry { Kind = ClipboardEntryKind.Text, Text = text };
        foreach (var def in _model.Actions.All.Where(d => d.Run.StartsWith("prompt:", StringComparison.OrdinalIgnoreCase)))
        {
            var captured = def;
            var item = new MenuItem { Header = captured.Title, Style = (Style)FindResource("MenuItem.Flat") };
            item.Click += (_, _) =>
            {
                if (_model?.Executor is not null) _ = _model.Executor.RunAsync(captured, temp);
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) return;
        menu.IsOpen = true;
    }

    private void SaveResult()
    {
        bool isImage = _imageBytes is { Length: > 0 } || _image is not null;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = isImage ? "PNG 图片|*.png" : "文本文件|*.txt",
            Title = "保存结果",
            FileName = isImage
                ? $"WinFinger-{DateTime.Now:yyyyMMdd-HHmmss}.png"
                : $"WinFinger-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };
        if (Views.DialogOwner.WithOwner(owner => dlg.ShowDialog(owner)) != true) return;
        try
        {
            if (isImage)
            {
                if (_imageBytes is { Length: > 0 }) File.WriteAllBytes(dlg.FileName, _imageBytes);
                else if (_image is not null)
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(_image));
                    using var stream = File.Create(dlg.FileName);
                    encoder.Save(stream);
                }
            }
            else
            {
                File.WriteAllText(dlg.FileName, ResultText, new UTF8Encoding(false));
            }
            _model?.Notifications.Post("📋", "已保存");
        }
        catch
        {
            _model?.Notifications.Post("📋", "保存失败");
        }
    }
}
