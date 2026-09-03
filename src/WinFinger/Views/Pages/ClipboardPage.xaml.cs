using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinFinger.Models;
using WinFinger.ViewModels;

namespace WinFinger.Views.Pages;

public partial class ClipboardPage : UserControl, IIslandPage
{
    private AppViewModel? _model;
    private ClipboardFilter _filter = ClipboardFilter.All;
    private ICollectionView? _view;
    private readonly DispatcherTimer _hoverTimer;
    private FrameworkElement? _hoverTarget;

    public ClipboardPage()
    {
        InitializeComponent();
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            ShowPreview();
        };
    }

    public void Initialize(AppViewModel model)
    {
        _model = model;
        _view = CollectionViewSource.GetDefaultView(model.ClipboardStore.Entries);
        _view.Filter = o => o is ClipboardEntry entry && Services.ClipboardStore.Matches(entry, _filter, SearchBox.Text);
        EntryList.ItemsSource = _view;

        PauseButton.Click += (_, _) =>
        {
            model.ClipboardMonitor.IsPaused = !model.ClipboardMonitor.IsPaused;
            RefreshPauseButton();
            RefreshEmptyState();
        };
        model.ClipboardMonitor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Services.ClipboardMonitorService.IsPaused))
            {
                RefreshPauseButton();
                RefreshEmptyState();
            }
        };
        ClearButton.Click += (_, _) => model.ClipboardStore.Clear();
        ClearSearchButton.Click += (_, _) => SearchBox.Text = "";
        SearchBox.TextChanged += (_, _) =>
        {
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearSearchButton.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            RefreshFilter();
        };

        WireFilter(FilterAll, ClipboardFilter.All);
        WireFilter(FilterText, ClipboardFilter.Text);
        WireFilter(FilterImage, ClipboardFilter.Image);
        WireFilter(FilterFile, ClipboardFilter.File);
        WireFilter(FilterFavorite, ClipboardFilter.Favorite);

        model.ClipboardStore.Entries.CollectionChanged += OnEntriesChanged;
        model.ClipboardStore.FavoriteChanged += _ => RefreshFilter();
        RefreshPauseButton();
        RefreshFilter();
    }

    public void OnShown() => RefreshFilter();

    private void WireFilter(RadioButton button, ClipboardFilter filter)
    {
        button.Checked += (_, _) =>
        {
            _filter = filter;
            RefreshFilter();
        };
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshEmptyState();

    private void RefreshFilter()
    {
        _view?.Refresh();
        RefreshEmptyState();
    }

    private void RefreshPauseButton()
    {
        if (_model is null) return;
        bool paused = _model.ClipboardMonitor.IsPaused;
        PauseGlyph.Text = paused ? "\uE768" : "\uE769";
        PauseLabel.Text = paused ? "继续记录" : "暂停记录";
    }

    private void RefreshEmptyState()
    {
        if (_model is null || _view is null) return;
        int visible = _view.Cast<object>().Count();
        CountLabel.Text = $"{visible} 条";
        bool empty = visible == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EntryList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!empty) return;

        // mac ClipboardEmptyState: paused > query > filter
        string glyph, title, subtitle;
        const string defaultSubtitle = "复制文本、图片或文件后，它们会出现在这里";
        if (_model.ClipboardMonitor.IsPaused)
            (glyph, title, subtitle) = ("\uE769", "剪贴板记录已暂停", "点击上方按钮继续记录");
        else if (SearchBox.Text.Trim().Length > 0)
            (glyph, title, subtitle) = ("\uE721", "没有匹配的记录", "换个关键词，或切换分类再试试");
        else
            (glyph, title, subtitle) = _filter switch
            {
                ClipboardFilter.Favorite => ("\uE734", "还没有收藏", "点星星就能把常用条目留在收藏里"),
                ClipboardFilter.File => ("\uE7C3", "还没有文件记录", "从资源管理器复制文件后，会出现在这里"),
                ClipboardFilter.Image => ("\uEB9F", "还没有图片记录", defaultSubtitle),
                ClipboardFilter.Text => ("\uE77F", "还没有文本记录", defaultSubtitle),
                _ => ("\uE77F", "还没有复制记录", defaultSubtitle)
            };
        EmptyGlyph.Text = glyph;
        EmptyTitle.Text = title;
        EmptySubtitle.Text = subtitle;
    }

    // ── row actions ──

    private void OnRowClicked(object sender, MouseButtonEventArgs e)
    {
        // whole card copies (mac: row is a Button)
        if (_model is not null && ((FrameworkElement)sender).Tag is ClipboardEntry entry)
            _model.ClipboardMonitor.CopyToClipboard(entry);
    }

    private void OnCopyEntry(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_model is not null && ((FrameworkElement)sender).Tag is ClipboardEntry entry)
            _model.ClipboardMonitor.CopyToClipboard(entry);
    }

    private void OnToggleFavorite(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_model is not null && ((FrameworkElement)sender).Tag is ClipboardEntry entry)
            _model.ClipboardStore.ToggleFavorite(entry);
    }

    private void OnDeleteEntry(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_model is not null && ((FrameworkElement)sender).Tag is ClipboardEntry entry)
            _model.ClipboardStore.Remove(entry);
    }

    // ── image thumbnail: 1s hover preview, click opens the lightbox (Windows extra) ──

    private void OnThumbEnter(object sender, MouseEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ClipboardEntry { Kind: ClipboardEntryKind.Image }) return;
        _hoverTarget = (FrameworkElement)sender;
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void OnThumbLeave(object sender, MouseEventArgs e)
    {
        _hoverTimer.Stop();
        _hoverTarget = null;
        PreviewPopup.IsOpen = false;
    }

    private void ShowPreview()
    {
        if (_hoverTarget?.Tag is not ClipboardEntry entry || string.IsNullOrEmpty(entry.ImagePath) ||
            !System.IO.File.Exists(entry.ImagePath)) return;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(entry.ImagePath);
            image.EndInit();
            image.Freeze();
            // mac: scale = min(360/w, 260/h, 1); size = (max(140, w*scale), max(100, h*scale))
            double scale = Math.Min(Math.Min(360.0 / image.PixelWidth, 260.0 / image.PixelHeight), 1);
            PreviewImage.Source = image;
            PreviewImage.Width = Math.Max(140, Math.Round(image.PixelWidth * scale));
            PreviewImage.Height = Math.Max(100, Math.Round(image.PixelHeight * scale));
            PreviewPopup.PlacementTarget = _hoverTarget;
            PreviewPopup.IsOpen = true;
        }
        catch
        {
            // unreadable image
        }
    }

    private void OnThumbClicked(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ClipboardEntry { Kind: ClipboardEntryKind.Image } entry ||
            string.IsNullOrEmpty(entry.ImagePath) || !System.IO.File.Exists(entry.ImagePath)) return;
        e.Handled = true;
        PreviewPopup.IsOpen = false;
        try
        {
            var win = new ImagePreviewWindow(entry.ImagePath);
            win.Show();
            win.Activate(); // island is NOACTIVATE; the lightbox needs focus so Esc closes it
        }
        catch
        {
            // image file unreadable
        }
    }
}
