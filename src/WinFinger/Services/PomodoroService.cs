using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinFinger.Services;

public enum PomodoroPhase
{
    Idle,
    Focus,
    Break
}

/// <summary>Pomodoro state machine (mac PomodoroTimer): focus → rest cycles, 1s tick, clamped durations.</summary>
public sealed partial class PomodoroService : ObservableObject
{
    public const int FocusMin = 5, FocusMax = 90, FocusStep = 5;
    public const int BreakMin = 1, BreakMax = 30, BreakStep = 1;

    [ObservableProperty] private PomodoroPhase _phase = PomodoroPhase.Idle;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private TimeSpan _remaining;
    private int _focusMinutes = 25;
    private int _breakMinutes = 5;

    /// <summary>Clamped to 5…90 on every setter path (mac didSet).</summary>
    public int FocusMinutes
    {
        get => _focusMinutes;
        set
        {
            var clamped = Math.Clamp(value, FocusMin, FocusMax);
            if (SetProperty(ref _focusMinutes, clamped))
                OnFocusMinutesChanged(clamped);
        }
    }

    /// <summary>Clamped to 1…30 on every setter path.</summary>
    public int BreakMinutes
    {
        get => _breakMinutes;
        set => SetProperty(ref _breakMinutes, Math.Clamp(value, BreakMin, BreakMax));
    }
    [ObservableProperty] private int _completedFocusCount;

    /// <summary>Raised when a phase finishes; argument is the phase that just completed.</summary>
    public event Action<PomodoroPhase>? PhaseCompleted;

    private readonly DispatcherTimer _timer;

    public PomodoroService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        Remaining = TimeSpan.FromMinutes(FocusMinutes);
    }

    /// <summary>"%02d:%02d" — safe above 60 minutes.</summary>
    public string RemainingText => $"{(int)Remaining.TotalMinutes:00}:{Remaining.Seconds:00}";

    /// <summary>Seconds of the phase currently counting down (for the progress ring).</summary>
    public int TotalSeconds => (Phase == PomodoroPhase.Break ? BreakMinutes : FocusMinutes) * 60;

    public double Progress => TotalSeconds <= 0 ? 0 : Math.Clamp(Remaining.TotalSeconds / TotalSeconds, 0, 1);

    public void StartFocus()
    {
        Phase = PomodoroPhase.Focus;
        Remaining = TimeSpan.FromMinutes(FocusMinutes);
        Resume();
    }

    public void StartBreak()
    {
        Phase = PomodoroPhase.Break;
        Remaining = TimeSpan.FromMinutes(BreakMinutes);
        Resume();
    }

    /// <summary>mac toggle(): idle → start focus; running → pause; paused → resume.</summary>
    public void Toggle()
    {
        if (Phase == PomodoroPhase.Idle) StartFocus();
        else if (IsRunning) Pause();
        else Resume();
    }

    public void Pause()
    {
        _timer.Stop();
        IsRunning = false;
    }

    public void Resume()
    {
        if (Phase == PomodoroPhase.Idle) return;
        _timer.Start();
        IsRunning = true;
    }

    public void Reset()
    {
        _timer.Stop();
        IsRunning = false;
        Phase = PomodoroPhase.Idle;
        Remaining = TimeSpan.FromMinutes(FocusMinutes);
    }

    public void AdjustFocus(int delta) => FocusMinutes += delta;

    public void AdjustBreak(int delta) => BreakMinutes += delta;

    private void OnFocusMinutesChanged(int value)
    {
        if (Phase == PomodoroPhase.Idle)
            Remaining = TimeSpan.FromMinutes(value);
    }

    private void Tick()
    {
        if (Remaining > TimeSpan.FromSeconds(1))
        {
            Remaining -= TimeSpan.FromSeconds(1);
            return;
        }

        Remaining = TimeSpan.Zero;
        _timer.Stop();
        IsRunning = false;
        var finished = Phase;
        if (finished == PomodoroPhase.Focus)
            CompletedFocusCount++;
        PhaseCompleted?.Invoke(finished);

        // auto-advance to the opposite phase
        if (finished == PomodoroPhase.Focus) StartBreak();
        else StartFocus();
    }
}
