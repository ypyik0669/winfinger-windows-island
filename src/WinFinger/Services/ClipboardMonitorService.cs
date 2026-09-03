using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using WinFinger.Interop;
using WinFinger.Models;

namespace WinFinger.Services;

/// <summary>
/// 监听 WM_CLIPBOARDUPDATE，并把实际读取交给后台 STA worker（文件 → 文本 → 图片）。
/// UI 线程只负责投递请求与最终写入 store。
/// </summary>
public sealed partial class ClipboardMonitorService : ObservableObject
{
    [ObservableProperty] private bool _isPaused;

    private readonly ClipboardStore _store;
    private readonly ForegroundAppService _foreground;
    private readonly SettingsService _settings;
    private ClipboardCaptureWorker? _worker;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private string? _ignoreHash;              // 我们自己刚写回剪贴板的内容
    private ClipboardEntryKind? _ignoreKind;  // 图片写回后系统会重编码成不同字节，只能按类型抑制
    private DateTime _suppressUntil;          // 抑制窗口

    /// <summary>Raised once per successful capture (new entry or a duplicate touch) so the UI can toast.</summary>
    public event Action<ClipboardEntry>? Captured;

    public ClipboardMonitorService(ClipboardStore store, ForegroundAppService foreground, SettingsService settings)
    {
        _store = store;
        _foreground = foreground;
        _settings = settings;
    }

    /// <summary>Attach the listener to an existing window's message loop.</summary>
    public void Attach(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        _worker ??= new ClipboardCaptureWorker(OnWorkerCaptured, () => _settings.Settings.MaxTextLength);
        NativeMethods.AddClipboardFormatListener(_hwnd);
    }

    public void Detach()
    {
        if (_hwnd != IntPtr.Zero)
            NativeMethods.RemoveClipboardFormatListener(_hwnd);
        _source?.RemoveHook(WndProc);
        _source = null;
        _hwnd = IntPtr.Zero;
        _worker?.Dispose();
        _worker = null;
    }

    /// <summary>
    /// 抑制一次自写回：window 内命中同 hash 即跳过；
    /// 传入 kind 时，window 内同类型的采集也一并跳过（图片写回后系统重编码，字节必然不同）。
    /// </summary>
    public void Suppress(string hash, TimeSpan window, ClipboardEntryKind? kind = null)
    {
        _ignoreHash = hash;
        _ignoreKind = kind;
        _suppressUntil = DateTime.UtcNow + window;
    }

    /// <summary>Writes an entry back to the system clipboard without re-recording it.</summary>
    public void CopyToClipboard(ClipboardEntry entry)
    {
        try
        {
            switch (entry.Kind)
            {
                case ClipboardEntryKind.Text when entry.Text is { } text:
                    Suppress(TextHash(text), TimeSpan.FromMilliseconds(2000));
                    WithWriteRetry(() => Clipboard.SetText(text, TextDataFormat.UnicodeText));
                    break;
                case ClipboardEntryKind.Image when _store.ImageData(entry) is { } png:
                    CopyPng(png);
                    break;
                case ClipboardEntryKind.File when entry.FilePaths.Count > 0:
                {
                    var existing = entry.FilePaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                    if (existing.Count == 0) return;
                    Suppress(ClipboardStore.FileHash(existing), TimeSpan.FromMilliseconds(2000));
                    var list = new StringCollection();
                    list.AddRange(existing.ToArray());
                    WithWriteRetry(() => Clipboard.SetFileDropList(list));
                    break;
                }
            }
        }
        catch
        {
            // clipboard is contended; give up silently
        }
    }

    /// <summary>写入一段文本（UnicodeText）。</summary>
    public void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Suppress(TextHash(text), TimeSpan.FromMilliseconds(2000));
            WithWriteRetry(() => Clipboard.SetText(text, TextDataFormat.UnicodeText));
        }
        catch
        {
            // clipboard is contended; give up silently
        }
    }

    /// <summary>纯文本粘贴：只放 UnicodeText，丢掉富文本/HTML 等格式。</summary>
    public void CopyPlainText(ClipboardEntry entry)
    {
        if (entry.Kind != ClipboardEntryKind.Text || entry.Text is not { } text || text.Length == 0) return;
        try
        {
            Suppress(TextHash(text), TimeSpan.FromMilliseconds(2000));
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text);
            WithWriteRetry(() => Clipboard.SetDataObject(data, true));
        }
        catch
        {
            // clipboard is contended; give up silently
        }
    }

    /// <summary>写入一张 PNG 图片（截图路径复用）。</summary>
    public void CopyPng(byte[] png)
    {
        if (png.Length == 0) return;
        try
        {
            Suppress(ClipboardStore.Hash(png), TimeSpan.FromMilliseconds(2000), ClipboardEntryKind.Image);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();
            WithWriteRetry(() => Clipboard.SetImage(image));
        }
        catch
        {
            // clipboard is contended; give up silently
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            if (!IsPaused)
            {
                // 前台还没见过任何非本应用的窗口时保持旧语义：来源留空（UI 显示“未知应用”）
                var sourceId = _foreground.ProcessName;
                var sourceName = string.IsNullOrEmpty(sourceId) ? null : _foreground.DisplayName;
                _worker?.Enqueue(new CaptureRequest(
                    NativeMethods.GetClipboardSequenceNumber(),
                    sourceId,
                    sourceName,
                    DateTime.Now));
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>worker 线程回调：marshal 回 UI 线程再落库。</summary>
    private void OnWorkerCaptured(CaptureResult result)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(() => Commit(result));
    }

    /// <summary>UI 线程：去重抑制 → 落库 → 通知。</summary>
    private void Commit(CaptureResult result)
    {
        if (IsPaused) return;
        if (ShouldIgnore(result.Hash, result.Kind)) return;

        var id = result.Request.SourceId;
        var name = result.Request.SourceName;
        ClipboardEntry? captured = result.Kind switch
        {
            ClipboardEntryKind.Text when result.Text is { } text => _store.AppendText(text, name, id, result.TextTruncated),
            ClipboardEntryKind.Image when result.Png is { } png => _store.AppendImage(png, name, id),
            ClipboardEntryKind.File when result.Files is { Count: > 0 } files => _store.AppendFiles(files, name, id),
            _ => null
        };
        if (captured is not null) Captured?.Invoke(captured);
    }

    private bool ShouldIgnore(string hash, ClipboardEntryKind kind)
    {
        if (_ignoreHash is null && _ignoreKind is null) return false;
        if (DateTime.UtcNow > _suppressUntil)
        {
            _ignoreHash = null; // 只有过期才清空
            _ignoreKind = null;
            return false;
        }
        bool hashMatch = _ignoreHash == hash;
        bool kindMatch = _ignoreKind is not null && _ignoreKind == kind;
        // 命中后不清空：一次写回会触发两次 WM_CLIPBOARDUPDATE（SetDataObject 的延迟渲染 + flush），
        // 抑制窗口很短，让它自然过期即可。
        return hashMatch || kindMatch;
    }

    /// <summary>与采集端一致的文本哈希口径（同样按 MaxTextLength 截断）。</summary>
    private string TextHash(string text) => ClipboardStore.TextHash(text, _settings.Settings.MaxTextLength);

    /// <summary>写入路径仍可能撞上别的进程占用剪贴板；重试 3×50ms。</summary>
    private static void WithWriteRetry(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
        }
    }
}
