using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinFinger.Services;

namespace WinFinger.Controls;

/// <summary>
/// Scrolling synced lyrics (mac LyricsPane): current line 20pt bold primary, others 14pt medium secondary @0.72,
/// re-evaluated every 350 ms while playing, current line animated to the vertical centre.
/// </summary>
public sealed class LyricsPane : ContentControl
{
    private static readonly DependencyProperty ScrollOffsetProperty = DependencyProperty.Register(
        nameof(ScrollOffset), typeof(double), typeof(LyricsPane), new PropertyMetadata(0.0, OnScrollOffsetChanged));

    private readonly ScrollViewer _scroll;
    private readonly StackPanel _stack;
    private readonly StackPanel _placeholder;
    private readonly TextBlock _placeholderGlyph;
    private readonly TextBlock _placeholderTitle;
    private readonly TextBlock _placeholderSubtitle;
    private readonly DispatcherTimer _timer;
    private MediaService? _media;
    private LyricsService? _lyrics;
    private int _currentIndex = -1;
    private bool _firstLayout = true;

    private double ScrollOffset => (double)GetValue(ScrollOffsetProperty);

    public LyricsPane()
    {
        _stack = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        _scroll = new ScrollViewer
        {
            Content = _stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly
        };
        _placeholderGlyph = new TextBlock
        {
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 22,
            FontWeight = FontWeights.Light,
            Text = "\uE8A5",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _placeholderGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Teal");
        _placeholderTitle = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        _placeholderTitle.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
        _placeholderSubtitle = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        _placeholderSubtitle.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        _placeholder = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _placeholder.Children.Add(_placeholderGlyph);
        _placeholder.Children.Add(_placeholderTitle);
        _placeholder.Children.Add(_placeholderSubtitle);

        var root = new Grid();
        root.Children.Add(_scroll);
        root.Children.Add(_placeholder);
        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _timer.Tick += (_, _) => Sync(animated: true);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _firstLayout = true;
                Sync(animated: false);
            }
            UpdateTimer();
        };
        SizeChanged += (_, _) => Sync(animated: false);
    }

    public void Initialize(MediaService media, LyricsService lyrics)
    {
        _media = media;
        _lyrics = lyrics;
        lyrics.PropertyChanged += OnLyricsChanged;
        media.PropertyChanged += OnMediaChanged;
        Rebuild();
    }

    private void OnMediaChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MediaService.IsPlaying) or nameof(MediaService.Position))
        {
            UpdateTimer();
            Sync(animated: true);
        }
    }

    private void OnLyricsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LyricsService.Lines) or nameof(LyricsService.Status) or nameof(LyricsService.SourceTitle))
            Rebuild();
    }

    private void Rebuild()
    {
        if (_lyrics is null) return;
        _stack.Children.Clear();
        _currentIndex = -1;
        _firstLayout = true;

        if (_lyrics.Status != LyricsStatus.Ready)
        {
            _scroll.Visibility = Visibility.Collapsed;
            _placeholder.Visibility = Visibility.Visible;
            (_placeholderTitle.Text, _placeholderSubtitle.Text) = _lyrics.Status switch
            {
                LyricsStatus.Loading => ("正在匹配歌词", _lyrics.SourceTitle),
                LyricsStatus.Empty => ("暂时没有歌词", "仍可在左侧控制播放"),
                _ => ("还没有歌曲", "播放后会在这里显示歌词")
            };
            UpdateTimer();
            return;
        }

        _placeholder.Visibility = Visibility.Collapsed;
        _scroll.Visibility = Visibility.Visible;
        foreach (var line in _lyrics.Lines)
        {
            var block = new TextBlock
            {
                Text = line.Text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                Opacity = 0.72,
                Margin = new Thickness(0, 0, 0, 14),
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = new ScaleTransform(0.98, 0.98)
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            _stack.Children.Add(block);
        }
        UpdateTimer();
        Dispatcher.BeginInvoke(() => Sync(animated: false), DispatcherPriority.Loaded);
    }

    private void UpdateTimer()
    {
        bool run = IsVisible && _lyrics?.Status == LyricsStatus.Ready && _lyrics.HasTimedLines && _media?.IsPlaying == true;
        if (run && !_timer.IsEnabled) _timer.Start();
        else if (!run && _timer.IsEnabled) _timer.Stop();
    }

    private void Sync(bool animated)
    {
        if (_lyrics is null || _media is null || _lyrics.Status != LyricsStatus.Ready || _stack.Children.Count == 0) return;
        int index = _lyrics.CurrentIndex(_media.EffectivePosition.TotalSeconds);
        index = Math.Clamp(index, 0, _stack.Children.Count - 1);
        bool changed = index != _currentIndex;
        if (!changed && !_firstLayout) return;

        if (changed)
        {
            if (_currentIndex >= 0 && _currentIndex < _stack.Children.Count)
                StyleLine((TextBlock)_stack.Children[_currentIndex], current: false);
            StyleLine((TextBlock)_stack.Children[index], current: true);
            _currentIndex = index;
        }

        var target = (TextBlock)_stack.Children[index];
        if (!target.IsLoaded || _scroll.ViewportHeight <= 0) return;
        _stack.UpdateLayout();
        double lineTop = target.TranslatePoint(new Point(0, 0), _stack).Y;
        double offset = lineTop + target.ActualHeight / 2 - _scroll.ViewportHeight / 2;
        offset = Math.Clamp(offset, 0, Math.Max(0, _scroll.ExtentHeight - _scroll.ViewportHeight));
        bool jump = _firstLayout || !animated;
        _firstLayout = false;
        BeginAnimation(ScrollOffsetProperty, null);
        if (jump)
        {
            SetValue(ScrollOffsetProperty, offset);
            _scroll.ScrollToVerticalOffset(offset);
        }
        else
        {
            BeginAnimation(ScrollOffsetProperty, new DoubleAnimation(_scroll.VerticalOffset, offset, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            });
        }
    }

    private static void StyleLine(TextBlock block, bool current)
    {
        var duration = TimeSpan.FromMilliseconds(250);
        block.BeginAnimation(TextBlock.FontSizeProperty, new DoubleAnimation(current ? 20 : 14, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        block.BeginAnimation(OpacityProperty, new DoubleAnimation(current ? 1 : 0.72, duration));
        block.FontWeight = current ? FontWeights.Bold : FontWeights.Medium;
        block.SetResourceReference(TextBlock.ForegroundProperty, current ? "Brush.TextPrimary" : "Brush.TextSecondary");
        if (block.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(current ? 1 : 0.98, duration));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(current ? 1 : 0.98, duration));
        }
    }

    private static void OnScrollOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LyricsPane)d)._scroll.ScrollToVerticalOffset((double)e.NewValue);
}
