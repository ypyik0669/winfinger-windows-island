using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using WinFinger.Services;
using WinFinger.ViewModels;

namespace WinFinger.Views;

public partial class CompactIslandView : UserControl
{
    private AppViewModel? _model;
    private Rectangle[] _bars = Array.Empty<Rectangle>();
    private string _lastTitle = "";

    public CompactIslandView()
    {
        InitializeComponent();
    }

    public void Initialize(AppViewModel model)
    {
        _model = model;

        DownloadLabel.SetBinding(TextBlock.TextProperty,
            new Binding(nameof(MetricsService.DownloadText)) { Source = model.Metrics });
        UploadLabel.SetBinding(TextBlock.TextProperty,
            new Binding(nameof(MetricsService.UploadText)) { Source = model.Metrics });
        Ring.SetBinding(Controls.MemoryRing.ValueProperty,
            new Binding(nameof(MetricsService.MemoryUsedRatio)) { Source = model.Metrics });

        _bars = SpectrumPanel.Children.OfType<Rectangle>().ToArray();
        model.Visualizer.LevelsUpdated += OnLevelsUpdated;

        model.Media.PropertyChanged += OnMediaChanged;
        model.Pomodoro.PropertyChanged += OnPomodoroChanged;
        RefreshMedia();
        RefreshPomodoro();
    }

    /// <summary>Reveals/hides the now-playing title during hover pre-expand.</summary>
    public void SetHoverState(bool hovering)
    {
        if (_model is null) return;
        bool show = hovering && _model.Media.HasSession && _model.Media.Title.Length > 0
                    && PomodoroSlot.Visibility != Visibility.Visible;
        if (show)
        {
            HoverTitleLabel.Text = _model.Media.Title;
            HoverTitleLabel.Visibility = Visibility.Visible;
            HoverTitleLabel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)) { BeginTime = TimeSpan.FromMilliseconds(80) });
        }
        else
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(100));
            fade.Completed += (_, _) => HoverTitleLabel.Visibility = Visibility.Collapsed;
            HoverTitleLabel.BeginAnimation(OpacityProperty, fade);
        }
    }

    private void OnLevelsUpdated()
    {
        var visualizer = _model?.Visualizer;
        if (visualizer is null || _model is null) return;
        bool running = visualizer.IsRunning;
        if (!running)
        {
            // mac paused bars: static "low" heights
            double[] low = { 0.35, 0.72, 0.48, 0.82, 0.42 };
            for (int i = 0; i < _bars.Length; i++) _bars[i].Height = 14 * low[i];
            return;
        }
        var brush = SpectrumBrush();
        // 8 FFT bands → 5 bars
        int bands = AudioVisualizerService.BandCount;
        for (int i = 0; i < _bars.Length; i++)
        {
            int from = i * bands / _bars.Length;
            int to = Math.Max(from + 1, (i + 1) * bands / _bars.Length);
            double level = 0;
            for (int b = from; b < to && b < bands; b++) level = Math.Max(level, visualizer.Levels[b]);
            _bars[i].Height = 3 + level * 11;
            _bars[i].Fill = brush;
        }
    }

    private Brush SpectrumBrush()
    {
        var color = _model?.Media.AccentColor ?? Colors.White;
        var brush = new SolidColorBrush(Color.FromArgb(0xF2, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private void OnMediaChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MediaService.Cover) or nameof(MediaService.HasSession)
            or nameof(MediaService.IsPlaying) or nameof(MediaService.AccentColor) or nameof(MediaService.Title))
            RefreshMedia();
    }

    private void RefreshMedia()
    {
        if (_model is null) return;
        var media = _model.Media;
        bool show = media.HasSession && (media.Title.Length > 0 || media.Cover is not null)
                    && _model.Pomodoro.Phase == PomodoroPhase.Idle;
        MediaSlot.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        CoverImage.Source = media.Cover;
        CoverGlow.Color = media.AccentColor;
        CoverGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty,
            new DoubleAnimation(media.IsPlaying ? 5 : 1, TimeSpan.FromMilliseconds(300)));
        var brush = SpectrumBrush();
        foreach (var bar in _bars) bar.Fill = brush;
        if (!_model.Visualizer.IsRunning) OnLevelsUpdated();

        // mac: fades on title change
        if (media.Title != _lastTitle)
        {
            _lastTitle = media.Title;
            MediaSlot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(200)));
        }
    }

    private void OnPomodoroChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshPomodoro();
        if (e.PropertyName == nameof(PomodoroService.Phase)) RefreshMedia();
    }

    private void RefreshPomodoro()
    {
        if (_model is null) return;
        var pomodoro = _model.Pomodoro;
        if (pomodoro.Phase == PomodoroPhase.Idle)
        {
            PomodoroSlot.Visibility = Visibility.Collapsed;
            return;
        }
        PomodoroSlot.Visibility = Visibility.Visible;
        bool rest = pomodoro.Phase == PomodoroPhase.Break;
        PomodoroGlyph.Text = rest ? "\uE95A" : "\uE823";
        string brush = rest ? "Brush.Teal" : "Brush.Warning";
        PomodoroGlyph.SetResourceReference(TextBlock.ForegroundProperty, brush);
        PomodoroLabel.SetResourceReference(TextBlock.ForegroundProperty, brush);
        PomodoroLabel.Text = pomodoro.RemainingText;
    }
}
