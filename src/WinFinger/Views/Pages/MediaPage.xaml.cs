using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WinFinger.Services;
using WinFinger.ViewModels;

namespace WinFinger.Views.Pages;

public partial class MediaPage : UserControl, IIslandPage
{
    private AppViewModel? _model;
    private readonly DispatcherTimer _progressTimer;
    private bool _glowAnimating;
    private bool _lyricsLayout;

    public MediaPage()
    {
        InitializeComponent();
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _progressTimer.Tick += (_, _) => RefreshProgress();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) RefreshProgress();
            UpdateTimers();
        };
    }

    public void Initialize(AppViewModel model)
    {
        _model = model;
        PlayPauseButton.Click += (_, _) => model.Media.TogglePlayPause();
        NextButton.Click += (_, _) => model.Media.Next();
        PrevButton.Click += (_, _) => model.Media.Previous();
        model.Media.PropertyChanged += OnMediaChanged;
        model.Lyrics.PropertyChanged += OnLyricsChanged;
        Lyrics.Initialize(model.Media, model.Lyrics);
        Refresh();
    }

    public void OnShown() => Refresh();

    private void OnMediaChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MediaService.Position) or nameof(MediaService.Duration) or nameof(MediaService.PositionTimestamp))
            RefreshProgress();
        else
            Refresh();
    }

    private void OnLyricsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsService.Status)) Refresh();
    }

    private void Refresh()
    {
        if (_model is null) return;
        var media = _model.Media;

        bool showPlayer = media.HasSession && (media.Title.Length > 0 || media.Cover is not null);
        PlayerPane.Visibility = showPlayer ? Visibility.Visible : Visibility.Collapsed;
        EmptyPane.Visibility = showPlayer ? Visibility.Collapsed : Visibility.Visible;
        UpdateTimers();
        if (!showPlayer) return;

        // mac: lyricsLayout only when lyrics are ready, otherwise the big-cover player layout
        bool lyricsLayout = _model.Lyrics.Status == LyricsStatus.Ready;
        if (lyricsLayout != _lyricsLayout || !PlayerPane.IsLoaded)
        {
            _lyricsLayout = lyricsLayout;
            ApplyLayout(lyricsLayout);
        }

        TitleLabel.Text = media.Title.Length > 0 ? media.Title : "未知曲目";
        SubtitleLabel.Text = string.Join(" · ", new[] { media.Artist, media.Album, media.SourceName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        SubtitleLabel.Visibility = SubtitleLabel.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // artwork: never upscale tiny thumbnails (mac artworkDisplaySize)
        int side = Math.Min(media.CoverPixelWidth, media.CoverPixelHeight);
        double display = side <= 0 ? 204 : side < 160 ? 120 : side < 300 ? 164 : 204;
        ArtworkFrame.Width = ArtworkFrame.Height = display;
        CompactArtworkFrame.Width = CompactArtworkFrame.Height = side <= 0 ? 188 : side < 160 ? 120 : side < 300 ? 164 : 188;
        ArtworkImage.Source = media.Cover;
        CompactArtworkImage.Source = media.Cover;
        ArtworkPlaceholder.Visibility = media.Cover is null ? Visibility.Visible : Visibility.Collapsed;
        CompactPlaceholder.Visibility = media.Cover is null ? Visibility.Visible : Visibility.Collapsed;

        // accent colour on glow / progress / play button
        var accent = media.AccentColor;
        GlowStop0.Color = WithAlpha(accent, 0x38);
        GlowStop1.Color = accent;
        GlowStop2.Color = WithAlpha(accent, 0x7A);
        GlowStop3.Color = WithAlpha(accent, 0x38);
        ProgressBrush.Color = accent;
        PlayBrush.Color = accent;

        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, media.IsPlaying ? "Brush.Upload" : "Brush.TextSecondary");
        StatusLabel.Text = media.IsPlaying ? "正在播放" : "已暂停";
        PlayPauseGlyph.Text = media.IsPlaying ? "\uE769" : "\uE768";
        PlayPauseButton.ToolTip = media.IsPlaying ? "暂停" : "播放";

        AnimateGlow(media.IsPlaying);
        RefreshProgress();
    }

    private void ApplyLayout(bool lyrics)
    {
        if (lyrics)
        {
            ArtworkBox.Visibility = Visibility.Collapsed;
            LeftColumn.Width = new GridLength(280);
            GapColumn.Width = new GridLength(36);
            RightColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(InfoColumn, 0);
            InfoColumn.MaxWidth = 280;
            CompactArtworkBox.Visibility = Visibility.Visible;
            StatusRow.Visibility = Visibility.Collapsed;
            TitleLabel.FontSize = 22;
            TitleLabel.Margin = new Thickness(0, 16, 0, 0);
            ProgressPane.Margin = new Thickness(0, 22, 0, 0);
            Lyrics.Visibility = Visibility.Visible;
            Grid.SetColumn(Lyrics, 2);
        }
        else
        {
            ArtworkBox.Visibility = Visibility.Visible;
            LeftColumn.Width = GridLength.Auto;
            GapColumn.Width = new GridLength(42);
            RightColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(InfoColumn, 2);
            InfoColumn.MaxWidth = 430;
            CompactArtworkBox.Visibility = Visibility.Collapsed;
            StatusRow.Visibility = Visibility.Visible;
            TitleLabel.FontSize = 27;
            TitleLabel.Margin = new Thickness(0, 14, 0, 0);
            ProgressPane.Margin = new Thickness(0, 18, 0, 0);
            Lyrics.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshProgress()
    {
        if (_model is null) return;
        var media = _model.Media;
        bool hasDuration = media.Duration.TotalSeconds > 0;
        ProgressPane.Visibility = hasDuration ? Visibility.Visible : Visibility.Collapsed;
        if (!hasDuration) return;
        double elapsed = media.EffectivePosition.TotalSeconds;
        double ratio = Math.Clamp(elapsed / media.Duration.TotalSeconds, 0, 1);
        ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth * ratio);
        ElapsedLabel.Text = TimeText(elapsed);
        DurationLabel.Text = TimeText(media.Duration.TotalSeconds);
    }

    private void UpdateTimers()
    {
        bool run = IsVisible && _model?.Media.HasSession == true && _model.Media.Duration.TotalSeconds > 0;
        if (run && !_progressTimer.IsEnabled) _progressTimer.Start();
        else if (!run && _progressTimer.IsEnabled) _progressTimer.Stop();
    }

    private static string TimeText(double seconds)
    {
        int total = Math.Max((int)Math.Round(seconds), 0);
        return $"{total / 60}:{total % 60:00}";
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>mac: glow breathes (blur 11↔17, scale 0.98↔1.04, 1.55s) while playing; dims to 0.28 when paused.</summary>
    private void AnimateGlow(bool playing)
    {
        var opacity = new DoubleAnimation(playing ? 0.82 : 0.28, TimeSpan.FromMilliseconds(600));
        ArtworkGlow.BeginAnimation(OpacityProperty, opacity);
        CompactGlow.BeginAnimation(OpacityProperty, opacity);
        if (playing == _glowAnimating) return;
        _glowAnimating = playing;
        if (playing)
        {
            var blur = new DoubleAnimation(11, 17, TimeSpan.FromSeconds(1.55)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            var scale = new DoubleAnimation(0.98, 1.04, TimeSpan.FromSeconds(1.55)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(14)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(blur, 20);
            Timeline.SetDesiredFrameRate(scale, 20);
            Timeline.SetDesiredFrameRate(spin, 20);
            GlowBlur.BeginAnimation(BlurEffect.RadiusProperty, blur);
            CompactGlowBlur.BeginAnimation(BlurEffect.RadiusProperty, blur);
            GlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            GlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
            CompactGlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            CompactGlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
            GlowRotate.BeginAnimation(RotateTransform.AngleProperty, spin);
            CompactGlowRotate.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        else
        {
            GlowBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            CompactGlowBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            GlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            GlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CompactGlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CompactGlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            GlowRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            CompactGlowRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }
}
