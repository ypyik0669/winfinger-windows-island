using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using WinFinger.Services;
using WinFinger.ViewModels;

namespace WinFinger.Views.Pages;

public partial class PomodoroPage : UserControl, IIslandPage
{
    private AppViewModel? _model;

    public PomodoroPage()
    {
        InitializeComponent();
    }

    public void Initialize(AppViewModel model)
    {
        _model = model;
        var pomodoro = model.Pomodoro;

        StartButton.Click += (_, _) => pomodoro.Toggle();
        ResetButton.Click += (_, _) => pomodoro.Reset();

        FocusMinus.Click += (_, _) => pomodoro.AdjustFocus(-PomodoroService.FocusStep);
        FocusPlus.Click += (_, _) => pomodoro.AdjustFocus(PomodoroService.FocusStep);
        BreakMinus.Click += (_, _) => pomodoro.AdjustBreak(-PomodoroService.BreakStep);
        BreakPlus.Click += (_, _) => pomodoro.AdjustBreak(PomodoroService.BreakStep);

        pomodoro.PropertyChanged += OnPomodoroChanged;
        Refresh();
    }

    public void OnShown() => Refresh();

    private void OnPomodoroChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (_model is null) return;
        var pomodoro = _model.Pomodoro;
        bool rest = pomodoro.Phase == PomodoroPhase.Break;
        string phaseBrushKey = rest ? "Brush.Teal" : "Brush.Warning";

        TimeLabel.Text = pomodoro.RemainingText;
        PhaseLabel.Text = pomodoro.Phase switch
        {
            PomodoroPhase.Focus => pomodoro.IsRunning ? "专注中" : "专注已暂停",
            PomodoroPhase.Break => pomodoro.IsRunning ? "休息中" : "休息已暂停",
            _ => "准备专注"
        };
        PhaseLabel.SetResourceReference(ForegroundProperty, pomodoro.Phase == PomodoroPhase.Idle ? "Brush.TextSecondary" : phaseBrushKey);
        SubtitleLabel.Text = pomodoro.IsRunning
            ? "保持专注，完成后自动进入下一阶段"
            : pomodoro.Phase == PomodoroPhase.Idle ? "一次只做好一件事" : "随时可以继续";

        Ring.SetResourceReference(Controls.ProgressRing.ProgressBrushProperty, phaseBrushKey);
        Ring.Value = pomodoro.Phase == PomodoroPhase.Idle ? 1 : pomodoro.Progress;
        if (TryFindResource(phaseBrushKey) is SolidColorBrush glow)
            RingGlow.Color = glow.Color;
        RingGlow.BeginAnimation(DropShadowEffect.OpacityProperty,
            new DoubleAnimation(pomodoro.IsRunning ? 0.25 : 0, TimeSpan.FromMilliseconds(300)));

        StartButton.SetResourceReference(BackgroundProperty, phaseBrushKey);
        StartGlyph.Text = pomodoro.IsRunning ? "\uE769" : "\uE768";
        StartLabel.Text = pomodoro.IsRunning ? "暂停" : pomodoro.Phase == PomodoroPhase.Idle ? "开始专注" : "继续";

        FocusLabel.Text = $"{pomodoro.FocusMinutes} 分钟";
        BreakLabel.Text = $"{pomodoro.BreakMinutes} 分钟";
        FocusMinus.IsEnabled = pomodoro.FocusMinutes > PomodoroService.FocusMin;
        FocusPlus.IsEnabled = pomodoro.FocusMinutes < PomodoroService.FocusMax;
        BreakMinus.IsEnabled = pomodoro.BreakMinutes > PomodoroService.BreakMin;
        BreakPlus.IsEnabled = pomodoro.BreakMinutes < PomodoroService.BreakMax;

        StatsLabel.Text = pomodoro.CompletedFocusCount > 0
            ? $"已完成 {pomodoro.CompletedFocusCount} 个番茄"
            : "完成第一个番茄后会在这里累计";
    }
}
