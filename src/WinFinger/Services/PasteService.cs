using System.IO;
using WinFinger.Interop;
using WinFinger.Models;
using WinFinger.ViewModels;

namespace WinFinger.Services;

/// <summary>粘贴选项：Plain = 只带纯文本，CollapseFirst = 先收起面板。</summary>
public sealed record PasteOptions(bool Plain = false, bool CollapseFirst = true);

/// <summary>
/// "选中即粘贴"：写回剪贴板 → 收起面板 → 还原前台窗口 → 注入 Ctrl+V。
/// 前台还原失败（UAC 窗口、远程桌面等）时降级为"已复制，请手动 Ctrl+V"。
/// </summary>
public sealed class PasteService
{
    private readonly ClipboardMonitorService _monitor;
    private readonly ClipboardStore _store;
    private readonly FocusRestoreService _focus;
    private readonly AppViewModel _model;
    private readonly NotificationService _notifications;

    /// <summary>粘贴流程进行中：防止连点重入。</summary>
    public bool IsBusy { get; private set; }

    public PasteService(ClipboardMonitorService monitor, ClipboardStore store, FocusRestoreService focus,
        AppViewModel model, NotificationService notifications)
    {
        _monitor = monitor;
        _store = store;
        _focus = focus;
        _model = model;
        _notifications = notifications;
    }

    /// <summary>把一条记录写回剪贴板并粘贴到前台窗口。</summary>
    public async Task<bool> PasteAsync(ClipboardEntry entry, PasteOptions? options = null)
    {
        if (IsBusy) return false;
        var o = options ?? new PasteOptions();
        IsBusy = true;
        try
        {
            _store.Touch(entry);
            if (entry.Kind == ClipboardEntryKind.File &&
                !entry.FilePaths.Any(p => File.Exists(p) || Directory.Exists(p)))
            {
                _notifications.Post("📋", "文件已不存在");
                return false;
            }

            if (o.Plain) _monitor.CopyPlainText(entry);
            else _monitor.CopyToClipboard(entry);

            return await PasteCurrentClipboardAsync(o.CollapseFirst).ConfigureAwait(true);
        }
        catch
        {
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>多选粘贴：文本条目用换行连接；没有文本条目且只选了一条时退化为单条粘贴。</summary>
    public async Task<bool> PasteManyAsync(IReadOnlyList<ClipboardEntry> entries, PasteOptions? options = null)
    {
        if (entries.Count == 0) return false;
        var texts = entries
            .Where(e => e.Kind == ClipboardEntryKind.Text && !string.IsNullOrEmpty(e.Text))
            .Select(e => e.Text!)
            .ToList();
        if (texts.Count == 0)
            return entries.Count == 1 ? await PasteAsync(entries[0], options).ConfigureAwait(true) : false;

        if (IsBusy) return false;
        var o = options ?? new PasteOptions();
        IsBusy = true;
        try
        {
            foreach (var entry in entries) _store.Touch(entry);
            _monitor.CopyText(string.Join("\n", texts));
            return await PasteCurrentClipboardAsync(o.CollapseFirst).ConfigureAwait(true);
        }
        catch
        {
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>粘贴任意一段文本（OCR / AI 结果等）。</summary>
    public async Task<bool> PasteTextAsync(string text, PasteOptions? options = null)
    {
        if (string.IsNullOrEmpty(text) || IsBusy) return false;
        var o = options ?? new PasteOptions();
        IsBusy = true;
        try
        {
            _monitor.CopyText(text);
            return await PasteCurrentClipboardAsync(o.CollapseFirst).ConfigureAwait(true);
        }
        catch
        {
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>只复制不粘贴（面板不收起）。</summary>
    public void CopyOnly(ClipboardEntry entry, bool plain = false)
    {
        _store.Touch(entry);
        if (plain) _monitor.CopyPlainText(entry);
        else _monitor.CopyToClipboard(entry);
    }

    /// <summary>只复制多条文本（换行连接），面板不收起。</summary>
    public void CopyMany(IReadOnlyList<ClipboardEntry> entries)
    {
        var texts = new List<string>();
        foreach (var entry in entries)
        {
            _store.Touch(entry);
            if (entry.Kind == ClipboardEntryKind.Text && !string.IsNullOrEmpty(entry.Text)) texts.Add(entry.Text!);
        }
        if (texts.Count == 1) _monitor.CopyText(texts[0]);
        else if (texts.Count > 1) _monitor.CopyText(string.Join("\n", texts));
    }

    /// <summary>剪贴板已就绪，剩下的收起 / 还原前台 / 注入按键流程。</summary>
    private async Task<bool> PasteCurrentClipboardAsync(bool collapseFirst)
    {
        if (collapseFirst) _model.Collapse();

        if (!await _focus.RestoreAndWaitAsync(400).ConfigureAwait(true))
        {
            _notifications.Post("📋", "已复制，请手动 Ctrl+V");
            return false;
        }

        await Task.Delay(60).ConfigureAwait(true);
        KeyboardInjector.ReleaseStuckModifiers();
        KeyboardInjector.SendCtrlV();
        return true;
    }
}
