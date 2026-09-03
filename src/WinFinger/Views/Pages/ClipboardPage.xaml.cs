using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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
    private readonly DispatcherTimer _relativeTimer;
    /// <summary>条目内容变化（后台 OCR 等）后的去抖刷新，避免每条都重建列表。</summary>
    private readonly DispatcherTimer _entryChangedTimer;
    private bool _actionsWired;
    private FrameworkElement? _hoverTarget;
    private ClipboardEntry? _editingEntry;
    /// <summary>本次左键按下的条目：抬起时不是同一条（拖拽滑出）就不当作点击。</summary>
    private ClipboardEntry? _pressedEntry;

    public ClipboardPage()
    {
        InitializeComponent();
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            ShowPreview();
        };
        _entryChangedTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        _entryChangedTimer.Tick += (_, _) =>
        {
            _entryChangedTimer.Stop();
            RefreshKeepingSelection();
        };
        // 相对时间（"3分钟"）每分钟重算一次，仅在页面可见时跑
        _relativeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _relativeTimer.Tick += (_, _) => RefreshRelativeTimes();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                _relativeTimer.Start();
            }
            else
            {
                _relativeTimer.Stop();
                _entryChangedTimer.Stop();
                Drawer.Close(); // 页面藏起来时结束流式输出
            }
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

        ClearButton.Click += (_, _) =>
            ClearConfirm.Visibility = ClearConfirm.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ClearKeepFavoritesButton.Click += (_, _) =>
        {
            ClearConfirm.Visibility = Visibility.Collapsed;
            model.ClipboardStore.Clear(includeFavorites: false);
        };
        ClearAllButton.Click += (_, _) =>
        {
            ClearConfirm.Visibility = Visibility.Collapsed;
            model.ClipboardStore.Clear(includeFavorites: true);
        };
        ClearCancelButton.Click += (_, _) => ClearConfirm.Visibility = Visibility.Collapsed;

        ClearSearchButton.Click += (_, _) => SearchBox.Text = "";
        SearchBox.TextChanged += (_, _) =>
        {
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearSearchButton.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            RefreshFilter();
        };
        SearchBox.PreviewKeyDown += (_, e) => HandleListKey(e);
        EntryList.PreviewKeyDown += (_, e) => HandleListKey(e);
        // 鼠标滑出列表就作废这次按下，别在别处抬起时误粘贴
        EntryList.MouseLeave += (_, _) => _pressedEntry = null;

        EditSaveButton.Click += (_, _) => CommitEdit();
        EditCancelButton.Click += (_, _) => CloseEdit();
        EditBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                CommitEdit();
            }
        };

        WireFilter(FilterAll, ClipboardFilter.All);
        WireFilter(FilterText, ClipboardFilter.Text);
        WireFilter(FilterImage, ClipboardFilter.Image);
        WireFilter(FilterFile, ClipboardFilter.File);
        WireFilter(FilterFavorite, ClipboardFilter.Favorite);

        // 动作框架：抽屉 → 执行器 → 右键菜单扩展点（Initialize 可能被调用多次，事件只挂一遍）
        Services.ActionCatalogService.Current = model.Actions;
        Drawer.Attach(model);
        model.AttachPresenter(Drawer);
        if (!_actionsWired)
        {
            _actionsWired = true;
            if (!model.EntryActionProviders.OfType<Services.CatalogActionProvider>().Any())
                model.EntryActionProviders.Add(new Services.CatalogActionProvider(model.Actions, () => model.Executor));
            model.Actions.Changed += () => Dispatcher.BeginInvoke(new Action(RefreshFilter));
            // OCR / 内容类型变了，内联动作要跟着换：去抖后再刷，别打断后台识别时的滚动与选中
            model.ClipboardStore.EntryChanged += _ => _entryChangedTimer.Start();
        }

        model.ClipboardStore.Entries.CollectionChanged += OnEntriesChanged;
        model.ClipboardStore.FavoriteChanged += _ => RefreshFilter();
        RefreshPauseButton();
        RefreshFilter();
    }

    public void OnShown()
    {
        RefreshFilter();
        if (IsVisible) _relativeTimer.Start();
        ResetToSearch();
    }

    /// <summary>面板展开：焦点直接落在搜索框，输入即筛选。</summary>
    public void OnExpanded() => ResetToSearch();

    public bool HandleEscape()
    {
        if (EditPopup.IsOpen)
        {
            CloseEdit();
            return true;
        }
        if (Drawer.IsOpen)
        {
            Drawer.Close();
            FocusSearch();
            return true;
        }
        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Text = "";
            FocusSearch();
            return true;
        }
        if (ClearConfirm.Visibility == Visibility.Visible)
        {
            ClearConfirm.Visibility = Visibility.Collapsed;
            return true;
        }
        return false;
    }

    /// <summary>只把键盘焦点送回搜索框：不动选中项、不动已有查询词。</summary>
    private void FocusSearch() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => SearchBox.Focus());

    /// <summary>面板刚展开 / 切到本页：清选中、聚焦并全选搜索框（下一次输入直接覆盖旧查询）。</summary>
    private void ResetToSearch() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        EntryList.SelectedIndex = -1;
        SearchBox.Focus();
        SearchBox.SelectAll();
    });

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
        // 过滤后选中项可能已经不在视图里，清掉避免"看不见的选中"
        if (EntryList.SelectedItem is ClipboardEntry selected && !VisibleEntries().Contains(selected))
            EntryList.SelectedIndex = -1;
        RefreshEmptyState();
    }

    /// <summary>刷新视图但保住当前选中项（后台 OCR 改动条目时不该把用户的选中/滚动位置甩掉）。</summary>
    private void RefreshKeepingSelection()
    {
        if (_view is null) return;
        var selected = EntryList.SelectedItem as ClipboardEntry;
        _view.Refresh();
        if (selected is null) return;
        if (VisibleEntries().Contains(selected))
        {
            EntryList.SelectedItem = selected;
            EntryList.ScrollIntoView(selected);
        }
        RefreshEmptyState();
    }

    private void RefreshRelativeTimes()
    {
        if (_model is null) return;
        foreach (var entry in _model.ClipboardStore.Entries) entry.RaiseCreatedAtChanged();
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

    // ── selection helpers ──

    private List<ClipboardEntry> VisibleEntries() =>
        _view is null ? new List<ClipboardEntry>() : _view.Cast<object>().OfType<ClipboardEntry>().ToList();

    /// <summary>当前多选条目，按列表顺序排列。</summary>
    private List<ClipboardEntry> SelectedEntries()
    {
        var chosen = EntryList.SelectedItems.OfType<ClipboardEntry>().ToHashSet();
        return VisibleEntries().Where(chosen.Contains).ToList();
    }

    private void SelectOnly(ClipboardEntry entry)
    {
        EntryList.SelectedItems.Clear();
        EntryList.SelectedItem = entry;
    }

    private void Activate(ClipboardEntry entry)
    {
        if (_model is null) return;
        if (_model.SettingsStore.Settings.PasteAfterSelect) _ = _model.Paste.PasteAsync(entry);
        else _model.Paste.CopyOnly(entry);
    }

    private static bool IsFromButton(object? source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is ButtonBase) return true;
        return false;
    }

    // ── mouse ──

    private void OnItemPreviewLeftDown(object sender, MouseButtonEventArgs e)
    {
        _pressedEntry = null;
        if (sender is not ListBoxItem { DataContext: ClipboardEntry entry }) return;
        if (IsFromButton(e.OriginalSource)) return;

        // Shift+单击 = 仅复制：拦下来，别让 ListBox 做区间选择
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            e.Handled = true;
            SelectOnly(entry);
            _model?.Paste.CopyOnly(entry);
            return;
        }
        // Ctrl+单击交给 ListBox 自己切换选中；焦点随后送回搜索框，继续打字即筛选
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => SearchBox.Focus());
            return;
        }
        // 普通单击：记下按下的条目，抬起时必须还是同一条才算"点击"
        _pressedEntry = entry;
    }

    private void OnItemLeftUp(object sender, MouseButtonEventArgs e)
    {
        var pressed = _pressedEntry;
        _pressedEntry = null;
        if (sender is not ListBoxItem { DataContext: ClipboardEntry entry }) return;
        if (IsFromButton(e.OriginalSource)) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != ModifierKeys.None) return;
        // 按下与抬起不在同一条（拖拽滑出）时不粘贴
        if (!ReferenceEquals(pressed, entry)) return;
        e.Handled = true;
        Activate(entry);
    }

    private void OnItemPreviewRightDown(object sender, MouseButtonEventArgs e)
    {
        _pressedEntry = null;
        if (sender is not ListBoxItem { DataContext: ClipboardEntry entry } item) return;
        // 右键点在选区外时先切成单选，菜单才对得上
        if (!EntryList.SelectedItems.Contains(entry)) SelectOnly(entry);
        item.ContextMenu ??= new ContextMenu();
    }

    private void OnItemContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_model is null || sender is not ListBoxItem { DataContext: ClipboardEntry entry } item) return;
        var menu = item.ContextMenu ??= new ContextMenu();
        menu.Items.Clear();
        menu.Closed -= OnContextMenuClosed;
        menu.Closed += OnContextMenuClosed;
        BuildEntryMenu(menu, entry);
    }

    private void OnContextMenuClosed(object sender, RoutedEventArgs e) => FocusSearch();

    // ── context menu ──

    private void BuildEntryMenu(ContextMenu menu, ClipboardEntry entry)
    {
        if (_model is null) return;
        var model = _model;
        var selection = SelectedEntries();
        bool isText = entry.Kind == ClipboardEntryKind.Text;
        bool isFile = entry.Kind == ClipboardEntryKind.File;
        bool isImage = entry.Kind == ClipboardEntryKind.Image;

        Add(menu.Items, "粘贴", "\uE77F", () =>
        {
            if (selection.Count > 1) _ = model.Paste.PasteManyAsync(selection);
            else _ = model.Paste.PasteAsync(entry);
        });
        Add(menu.Items, "仅复制", "\uE8C8", () =>
        {
            if (selection.Count > 1) model.Paste.CopyMany(selection);
            else model.Paste.CopyOnly(entry);
        });
        if (isText) Add(menu.Items, "粘贴为纯文本", "\uE8D2", () => _ = model.Paste.PasteAsync(entry, new Services.PasteOptions(Plain: true)));
        Add(menu.Items, entry.IsFavorite ? "取消收藏" : "收藏", entry.IsFavorite ? "\uE735" : "\uE734",
            () => model.ClipboardStore.ToggleFavorite(entry));
        if (isText) Add(menu.Items, "编辑文本…", "\uE70F", () => OpenEdit(entry));
        if (isFile)
        {
            Add(menu.Items, "打开所在文件夹", "\uE838", () => RevealInExplorer(entry));
            Add(menu.Items, "复制路径", "\uE71B", () => model.ClipboardMonitor.CopyText(string.Join(Environment.NewLine, entry.FilePaths)));
        }
        if (isImage) Add(menu.Items, "图片另存为…", "\uE792", () => SaveImageAs(entry));

        // 扩展点：OCR / AI 等能力挂上来的动作
        var extras = model.EntryActionProviders
            .SelectMany(p =>
            {
                try { return p.ActionsFor(entry, selection); }
                catch { return Enumerable.Empty<EntryAction>(); }
            })
            .Where(a =>
            {
                try { return a.IsVisible(entry); }
                catch { return false; }
            })
            .OrderBy(a => a.Order)
            .ToList();
        if (extras.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var group in extras.GroupBy(a => a.Group))
            {
                ItemCollection target = menu.Items;
                if (group.Key is { Length: > 0 } groupName)
                {
                    var sub = new MenuItem { Header = groupName, Style = (Style)FindResource("MenuItem.Flat") };
                    menu.Items.Add(sub);
                    target = sub.Items;
                }
                foreach (var action in group)
                {
                    var captured = action;
                    Add(target, captured.Header, captured.Icon, () =>
                    {
                        try { captured.Execute(entry); }
                        catch { /* 扩展动作不能拖垮菜单 */ }
                    }, captured.IsDanger);
                }
            }
            Add(menu.Items, "自定义动作…（打开 actions.json）", "\uE713", RevealActionsFile);
        }

        menu.Items.Add(new Separator());
        Add(menu.Items, selection.Count > 1 ? $"删除 {selection.Count} 条" : "删除", "\uE711", () =>
        {
            foreach (var item in selection.Count > 1 ? selection : new List<ClipboardEntry> { entry })
                model.ClipboardStore.Remove(item);
        }, danger: true);
    }

    private void Add(ItemCollection items, string header, string? glyph, Action execute, bool danger = false)
    {
        var brush = danger ? "Brush.Danger" : "Brush.TextPrimary";
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (!string.IsNullOrEmpty(glyph))
        {
            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)FindResource("Font.Icon"),
                FontSize = 11,
                Width = 16,
                VerticalAlignment = VerticalAlignment.Center
            };
            icon.SetResourceReference(ForegroundProperty, brush);
            panel.Children.Add(icon);
        }
        var text = new TextBlock { Text = header, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(ForegroundProperty, brush);
        panel.Children.Add(text);

        var menuItem = new MenuItem { Header = panel, Style = (Style)FindResource("MenuItem.Flat") };
        menuItem.Click += (_, _) => execute();
        items.Add(menuItem);
    }

    /// <summary>卡片上的内联动作按钮：吃掉点击，别让整行去粘贴。</summary>
    private void OnInlineAction(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _pressedEntry = null;
        if (_model?.Executor is null) return;
        if (((FrameworkElement)sender).Tag is not Controls.InlineActionItem item) return;
        _ = _model.Executor.RunAsync(item.Definition, item.Entry);
    }

    /// <summary>在资源管理器里定位用户的 actions.json。</summary>
    private void RevealActionsFile()
    {
        if (_model is null) return;
        try
        {
            string path = _model.Actions.ActionsPath;
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Services.StoragePaths.Root}\"") { UseShellExecute = true });
        }
        catch
        {
            _model.Notifications.Post("⚙️", "打开 actions.json 失败");
        }
    }

    private void RevealInExplorer(ClipboardEntry entry)
    {
        string? path = entry.FirstFilePath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                _model?.Notifications.Post("📋", "文件已不存在");
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            _model?.Notifications.Post("📋", "打开所在文件夹失败");
        }
    }

    private void SaveImageAs(ClipboardEntry entry)
    {
        if (string.IsNullOrEmpty(entry.ImagePath) || !File.Exists(entry.ImagePath))
        {
            _model?.Notifications.Post("📋", "图片文件已不存在");
            return;
        }
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG 图片|*.png",
                Title = "图片另存为",
                FileName = $"WinFinger-{entry.CreatedAt:yyyyMMdd-HHmmss}.png"
            };
            // 岛是 NOACTIVATE，对话框需要一个可激活的临时 owner
            if (DialogOwner.WithOwner(owner => dlg.ShowDialog(owner)) != true) return;
            File.Copy(entry.ImagePath, dlg.FileName, overwrite: true);
            _model?.Notifications.Post("📋", "图片已保存");
        }
        catch
        {
            _model?.Notifications.Post("📋", "图片保存失败");
        }
    }

    // ── edit popup ──

    private void OpenEdit(ClipboardEntry entry)
    {
        _editingEntry = entry;
        EditBox.Text = entry.Text ?? "";
        EditPopup.PlacementTarget = EntryList;
        EditPopup.IsOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            EditBox.Focus();
            EditBox.CaretIndex = EditBox.Text.Length;
        });
    }

    private void CommitEdit()
    {
        if (_editingEntry is not null && _model is not null)
            _model.ClipboardStore.UpdateText(_editingEntry, EditBox.Text);
        CloseEdit();
    }

    private void CloseEdit()
    {
        EditPopup.IsOpen = false;
        _editingEntry = null;
        FocusSearch();
    }

    // ── keyboard (shared by the search box and the list) ──

    private void HandleListKey(KeyEventArgs e)
    {
        if (_model is null) return;
        if (e.Key == Key.ImeProcessed) return; // 输入法组词中，键全部放行
        var model = _model;
        var visible = VisibleEntries();
        bool inList = EntryList.IsKeyboardFocusWithin;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var current = EntryList.SelectedItem as ClipboardEntry;

        switch (e.Key)
        {
            case Key.Down or Key.Up when visible.Count > 0:
            {
                e.Handled = true;
                int index = current is null ? -1 : visible.IndexOf(current);
                int next = e.Key == Key.Down
                    ? (index < 0 ? 0 : Math.Min(index + 1, visible.Count - 1))
                    : (index <= 0 ? 0 : index - 1);
                SelectOnly(visible[next]);
                EntryList.ScrollIntoView(visible[next]);
                return;
            }
            case Key.Enter:
            {
                var selection = SelectedEntries();
                var target = current ?? visible.FirstOrDefault();
                if (ctrl)
                {
                    if (selection.Count > 1) model.Paste.CopyMany(selection);
                    else if (shift && target is not null) model.Paste.CopyOnly(target, plain: true);
                    else if (target is not null) model.Paste.CopyOnly(target);
                    else return;
                }
                else if (selection.Count > 1) _ = model.Paste.PasteManyAsync(selection);
                else if (target is not null) _ = model.Paste.PasteAsync(target);
                else return;
                e.Handled = true;
                return;
            }
            case Key.Delete when inList || SearchBox.Text.Length == 0:
            {
                var selection = SelectedEntries();
                if (selection.Count == 0) return;
                e.Handled = true;
                foreach (var entry in selection) model.ClipboardStore.Remove(entry);
                EntryList.SelectedIndex = -1;
                return;
            }
            case Key.F or Key.D when ctrl && !shift:
            {
                var selection = SelectedEntries();
                if (selection.Count == 0) return;
                e.Handled = true;
                foreach (var entry in selection) model.ClipboardStore.ToggleFavorite(entry);
                return;
            }
            case Key.C when ctrl && shift:
            {
                var target = current ?? visible.FirstOrDefault();
                if (target is null) return;
                e.Handled = true;
                model.Paste.CopyOnly(target, plain: true);
                return;
            }
            case Key.Space when inList:
            {
                if (current is null) return;
                e.Handled = true;
                if (EntryList.SelectedItems.Contains(current)) EntryList.SelectedItems.Remove(current);
                else EntryList.SelectedItems.Add(current);
                return;
            }
        }
        // 其余按键（字母、空格、退格、左右方向键、Esc、Ctrl+1..5）交给搜索框和窗口
    }

    // ── row actions ──

    private void OnCopyEntry(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_model is not null && ((FrameworkElement)sender).Tag is ClipboardEntry entry)
            _model.Paste.CopyOnly(entry);
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
            !File.Exists(entry.ImagePath)) return;
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
            string.IsNullOrEmpty(entry.ImagePath) || !File.Exists(entry.ImagePath)) return;
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
