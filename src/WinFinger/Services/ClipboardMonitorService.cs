using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using WinFinger.Interop;
using WinFinger.Models;

namespace WinFinger.Services;

/// <summary>Listens for WM_CLIPBOARDUPDATE and records history into the store (files → image → text, like mac).</summary>
public sealed partial class ClipboardMonitorService : ObservableObject
{
    [ObservableProperty] private bool _isPaused;

    private readonly ClipboardStore _store;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private string? _ignoreHash;      // content we just wrote back ourselves
    private DateTime _suppressUntil;  // belt-and-braces time window

    /// <summary>Raised once per successful capture (new entry or a duplicate touch) so the UI can toast.</summary>
    public event Action<ClipboardEntry>? Captured;

    public ClipboardMonitorService(ClipboardStore store)
    {
        _store = store;
    }

    /// <summary>Attach the listener to an existing window's message loop.</summary>
    public void Attach(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        NativeMethods.AddClipboardFormatListener(_hwnd);
    }

    public void Detach()
    {
        if (_hwnd != IntPtr.Zero)
            NativeMethods.RemoveClipboardFormatListener(_hwnd);
        _source?.RemoveHook(WndProc);
        _source = null;
        _hwnd = IntPtr.Zero;
    }

    /// <summary>Writes an entry back to the system clipboard without re-recording it.</summary>
    public void CopyToClipboard(ClipboardEntry entry)
    {
        try
        {
            switch (entry.Kind)
            {
                case ClipboardEntryKind.Text when entry.Text is { } text:
                    _ignoreHash = ClipboardStore.Hash(Encoding.UTF8.GetBytes(text));
                    _suppressUntil = DateTime.UtcNow.AddMilliseconds(500);
                    WithRetry(() => Clipboard.SetText(text));
                    break;
                case ClipboardEntryKind.Image when _store.ImageData(entry) is { } png:
                {
                    _ignoreHash = ClipboardStore.Hash(png);
                    _suppressUntil = DateTime.UtcNow.AddMilliseconds(500);
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = new MemoryStream(png);
                    image.EndInit();
                    image.Freeze();
                    WithRetry(() => Clipboard.SetImage(image));
                    break;
                }
                case ClipboardEntryKind.File when entry.FilePaths.Count > 0:
                {
                    var existing = entry.FilePaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                    if (existing.Count == 0) return;
                    _ignoreHash = ClipboardStore.FileHash(existing);
                    _suppressUntil = DateTime.UtcNow.AddMilliseconds(500);
                    var list = new StringCollection();
                    list.AddRange(existing.ToArray());
                    WithRetry(() => Clipboard.SetFileDropList(list));
                    break;
                }
            }
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
            OnClipboardChanged();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnClipboardChanged()
    {
        if (IsPaused) return;

        var (sourceId, sourceName) = ForegroundApp();

        try
        {
            // 1. files
            StringCollection? files = null;
            WithRetry(() =>
            {
                if (Clipboard.ContainsFileDropList()) files = Clipboard.GetFileDropList();
            });
            if (files is { Count: > 0 })
            {
                var paths = files.Cast<string>()
                    .Where(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
                    .ToList();
                if (paths.Count > 0)
                {
                    var hash = ClipboardStore.FileHash(paths.Select(p => Path.GetFullPath(p)));
                    if (ShouldIgnore(hash)) return;
                    var captured = _store.AppendFiles(paths, sourceName, sourceId);
                    if (captured is not null) Captured?.Invoke(captured);
                    return;
                }
            }

            // 2. image
            BitmapSource? image = null;
            WithRetry(() =>
            {
                if (Clipboard.ContainsImage()) image = Clipboard.GetImage();
            });
            if (image is not null)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                var png = stream.ToArray();
                var hash = ClipboardStore.Hash(png);
                if (ShouldIgnore(hash)) return;
                var captured = _store.AppendImage(png, sourceName, sourceId);
                if (captured is not null) Captured?.Invoke(captured);
                return;
            }

            // 3. text
            string? text = null;
            WithRetry(() =>
            {
                if (Clipboard.ContainsText()) text = Clipboard.GetText();
            });
            if (!string.IsNullOrEmpty(text))
            {
                var hash = ClipboardStore.Hash(Encoding.UTF8.GetBytes(text));
                if (ShouldIgnore(hash)) return;
                var captured = _store.AppendText(text, sourceName, sourceId);
                if (captured is not null) Captured?.Invoke(captured);
            }
        }
        catch
        {
            // reading can race with the copying app; skip this update
        }
    }

    private bool ShouldIgnore(string hash)
    {
        if (_ignoreHash == hash && DateTime.UtcNow <= _suppressUntil)
        {
            _ignoreHash = null;
            return true;
        }
        _ignoreHash = null;
        return false;
    }

    /// <summary>(process id string, friendly name) of the foreground app; own app → nulls.</summary>
    private static (string? id, string? name) ForegroundApp()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return (null, null);
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return (null, null);
            using var process = Process.GetProcessById((int)pid);
            if (string.Equals(process.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase))
                return (null, null);
            string id = process.ProcessName.ToLowerInvariant();
            string name = process.ProcessName;
            try
            {
                var description = process.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) name = description;
                if (process.MainModule?.FileName is { } file) id = file;
            }
            catch
            {
                // elevated process: main module not accessible
            }
            return (id, name);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>Clipboard access races with other apps (CLIPBRD_E_CANT_OPEN); retry 3×50ms.</summary>
    private static void WithRetry(Action action)
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
