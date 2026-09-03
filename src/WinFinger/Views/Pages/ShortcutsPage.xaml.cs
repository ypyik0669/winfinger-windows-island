using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using WinFinger.Models;
using WinFinger.Services;
using WinFinger.ViewModels;

namespace WinFinger.Views.Pages;

public partial class ShortcutsPage : UserControl, IIslandPage
{
    private AppViewModel? _model;
    private ShortcutReadStatus _status = ShortcutReadStatus.Idle;
    private IReadOnlyList<ShortcutGroup> _liveGroups = Array.Empty<ShortcutGroup>();
    private int _liveCount;
    private int _loadedPid = -1;
    private string _lastName = "";

    public ShortcutsPage()
    {
        InitializeComponent();
    }

    public void Initialize(AppViewModel model)
    {
        _model = model;
        model.ForegroundApp.PropertyChanged += OnForegroundChanged;
        RefreshButton.Click += (_, _) => _ = LoadShortcutsAsync(force: true);
        Render();
        _ = LoadShortcutsAsync(force: false);
    }

    public void OnShown()
    {
        Render();
        _ = LoadShortcutsAsync(force: false);
    }

    private void OnForegroundChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ForegroundAppService.ProcessId))
        {
            Render();
            _ = LoadShortcutsAsync(force: false);
        }
    }

    /// <summary>mac loadShortcuts(): live menu read, falls back to the catalog.</summary>
    private async Task LoadShortcutsAsync(bool force)
    {
        if (_model is null) return;
        var app = _model.ForegroundApp;
        int pid = app.ProcessId;
        if (!force && pid == _loadedPid) return;
        _loadedPid = pid;

        if (pid <= 0)
        {
            _status = ShortcutReadStatus.Unavailable;
            _liveGroups = Array.Empty<ShortcutGroup>();
            Render();
            return;
        }

        _status = ShortcutReadStatus.Loading;
        Render();
        var result = await AppShortcutReader.ReadAsync(app.Hwnd, pid);
        if (_model.ForegroundApp.ProcessId != pid) return; // app switched meanwhile
        _liveGroups = result.Groups;
        _liveCount = result.Count;
        _status = result.Status;
        Render();
    }

    private void Render()
    {
        if (_model is null) return;
        var app = _model.ForegroundApp;
        var fallback = _model.ShortcutCatalog.SetFor(app.ProcessName);
        bool generic = fallback.Id == "generic";

        string name = string.IsNullOrEmpty(app.ProcessName) ? "当前应用" : app.DisplayName;
        if (name != _lastName)
        {
            _lastName = name;
            AppNameLabel.Text = name;
            AppNameLabel.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        if (app.Icon is { } icon)
        {
            AppIcon.Source = icon;
            AppIcon.Visibility = Visibility.Visible;
            AppGlyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            AppIcon.Visibility = Visibility.Collapsed;
            AppGlyph.Visibility = Visibility.Visible;
        }

        StatusLabel.Text = _status switch
        {
            ShortcutReadStatus.Idle => "准备读取当前应用快捷键",
            ShortcutReadStatus.Loading => "正在读取当前应用菜单…",
            ShortcutReadStatus.Live => $"已从当前应用菜单读取 {_liveCount} 项",
            ShortcutReadStatus.PermissionRequired => "当前显示内置快捷键 · 授权后可实时读取",
            _ => generic ? "当前应用未公开菜单 · 显示通用快捷键" : "当前应用未公开菜单 · 显示内置快捷键"
        };
        BadgeLabel.Text = _status switch
        {
            ShortcutReadStatus.Live => "实时",
            ShortcutReadStatus.Loading => "读取中",
            ShortcutReadStatus.PermissionRequired => "需授权",
            _ => "内置"
        };
        BadgeDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, _status switch
        {
            ShortcutReadStatus.Live => "Brush.Upload",
            ShortcutReadStatus.PermissionRequired => "Brush.Warning",
            _ => "Brush.TextSecondary"
        });

        // mac displayedGroups = liveGroups.isEmpty ? fallbackSet.groups : liveGroups
        var groups = _liveGroups.Count == 0 ? fallback.Groups : _liveGroups;
        if (!ReferenceEquals(GroupList.ItemsSource, groups))
            GroupList.ItemsSource = groups;
    }
}
