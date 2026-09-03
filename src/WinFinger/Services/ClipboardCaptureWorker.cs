using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using WinFinger.Interop;
using WinFinger.Models;

namespace WinFinger.Services;

/// <summary>一次剪贴板读取请求（由 WndProc 投递）。</summary>
internal sealed record CaptureRequest(uint Sequence, string? SourceId, string? SourceName, DateTime At);

/// <summary>读取结果，回调在 worker 线程上触发，由调用方 marshal 回 UI 线程。</summary>
internal sealed record CaptureResult(
    CaptureRequest Request,
    ClipboardEntryKind Kind,
    string? Text,
    bool TextTruncated,
    byte[]? Png,
    List<string>? Files,
    string Hash);

/// <summary>
/// 常驻后台 STA 线程：把剪贴板读取（含 PNG 编码、哈希）从 UI 线程搬走。
/// 只保留最新一次请求（latest wins）；读取前后比对剪贴板序列号，过期请求直接放弃。
/// </summary>
internal sealed class ClipboardCaptureWorker : IDisposable
{
    private static readonly int[] Backoffs = { 0, 200, 500, 1000 };
    private const int MaxPixelCount = 50_000_000;
    private const int CantOpen = unchecked((int)0x800401D0); // CLIPBRD_E_CANT_OPEN

    private readonly Action<CaptureResult> _onCaptured;
    private readonly Func<int> _maxTextLength;
    private readonly AutoResetEvent _signal = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _thread;
    private readonly object _gate = new();

    private CaptureRequest? _pending; // 最新槽位：新请求覆盖旧请求
    private volatile bool _disposed;

    public ClipboardCaptureWorker(Action<CaptureResult> onCaptured, Func<int> maxTextLength)
    {
        _onCaptured = onCaptured;
        _maxTextLength = maxTextLength;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "WinFinger.ClipboardCapture"
        };
        _thread.SetApartmentState(ApartmentState.STA); // System.Windows.Clipboard 要求 STA
        _thread.Start();
    }

    /// <summary>投递一次读取请求；只保留最新的一条。</summary>
    public void Enqueue(CaptureRequest request)
    {
        if (_disposed) return;
        lock (_gate) _pending = request;
        Signal();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _stop.Set();
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
            // 句柄已被回收
        }
        // 线程可能正卡在退避/PNG 编码里：只有确认退出后才释放句柄，否则留给 GC 终结
        if (_thread.Join(1500))
        {
            _signal.Dispose();
            _stop.Dispose();
        }
    }

    private void Signal()
    {
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略
        }
    }

    /// <summary>可被 Dispose 打断的等待；返回 false 表示应当退出。</summary>
    private bool Sleep(int milliseconds)
    {
        if (_disposed) return false;
        try
        {
            return !_stop.WaitOne(milliseconds);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void Loop()
    {
        while (true)
        {
            try
            {
                if (_disposed) return;
                int index;
                try
                {
                    index = WaitHandle.WaitAny(new WaitHandle[] { _stop, _signal });
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                if (index == 0 || _disposed) return;

                CaptureRequest? request;
                lock (_gate)
                {
                    request = _pending;
                    _pending = null;
                }
                if (request is null) continue;

                var result = ReadWithBackoff(request);
                if (result is not null && !_disposed) _onCaptured(result);
            }
            catch (Exception ex)
            {
                // 任何异常都不允许逃出后台线程（否则进程直接崩）
                Debug.WriteLine($"[ClipboardCaptureWorker] 读取失败: {ex.Message}");
            }
        }
    }

    /// <summary>退避重试：剪贴板可能被别的进程占用（CLIPBRD_E_CANT_OPEN）。</summary>
    private CaptureResult? ReadWithBackoff(CaptureRequest request)
    {
        for (int attempt = 0; attempt < Backoffs.Length; attempt++)
        {
            if (Backoffs[attempt] > 0 && !Sleep(Backoffs[attempt])) return null;
            if (_disposed) return null;

            // 序列号已经往前走了：后面还有更新的请求，本次直接放弃
            if (HasNewerRequest(request)) return null;

            try
            {
                return ReadOnce(request);
            }
            catch (COMException ex) when (ex.HResult == CantOpen || attempt < Backoffs.Length - 1)
            {
                // 被占用，下一轮再试
            }
            catch (ExternalException)
            {
                // 其他剪贴板 COM 故障：放弃本次
                return null;
            }
        }
        return null;
    }

    private bool HasNewerRequest(CaptureRequest request)
    {
        lock (_gate)
        {
            if (_pending is { } newer && newer.Sequence != request.Sequence) return true;
        }
        var current = NativeMethods.GetClipboardSequenceNumber();
        return current != 0 && request.Sequence != 0 && current != request.Sequence;
    }

    /// <summary>顺序：排除格式 → 文件 → 文本 → 图片（文本优先，避免 Excel 复制变成图片）。</summary>
    private CaptureResult? ReadOnce(CaptureRequest request)
    {
        try
        {
            // ① 排除格式
            if (NativeMethods.IsClipboardFormatAvailable(
                    NativeMethods.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing")))
                return null;
            if (NativeMethods.IsClipboardFormatAvailable(
                    NativeMethods.RegisterClipboardFormat("CanIncludeInClipboardHistory"))
                && ReadDwordFormat("CanIncludeInClipboardHistory") == 0)
                return null;

            // ② 文件
            if (Clipboard.ContainsFileDropList())
            {
                StringCollection drop = Clipboard.GetFileDropList();
                var paths = new List<string>();
                foreach (var raw in drop.Cast<string?>())
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    try
                    {
                        if (File.Exists(raw) || Directory.Exists(raw)) paths.Add(Path.GetFullPath(raw));
                    }
                    catch
                    {
                        // 单个畸形路径不影响本次其余文件
                    }
                }
                if (paths.Count > 0)
                    return new CaptureResult(request, ClipboardEntryKind.File, null, false, null, paths,
                        ClipboardStore.FileHash(paths));
            }

            // ③ 文本优先
            if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                var text = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    int limit = Math.Max(1, _maxTextLength());
                    bool truncated = text.Length > limit;
                    if (truncated) text = text[..limit];
                    return new CaptureResult(request, ClipboardEntryKind.Text, text, truncated, null, null,
                        ClipboardStore.TextHash(text, limit));
                }
            }

            // ④ 图片
            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image is null) return null;
                long pixels = (long)image.PixelWidth * image.PixelHeight;
                if (pixels <= 0 || pixels > MaxPixelCount) return null;

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                var png = stream.ToArray();
                if (png.Length == 0 || png.Length > ClipboardStore.MaxImageBytes) return null;
                return new CaptureResult(request, ClipboardEntryKind.Image, null, false, png, null,
                    ClipboardStore.Hash(png));
            }

            return null;
        }
        catch (COMException)
        {
            throw; // 交给退避重试
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardCaptureWorker] ReadOnce 异常: {ex.Message}");
            return null;
        }
    }

    /// <summary>读取一个 DWORD 型剪贴板格式；拿不到（或不是 MemoryStream）时返回 null。</summary>
    private static int? ReadDwordFormat(string format)
    {
        try
        {
            var data = Clipboard.GetDataObject()?.GetData(format);
            if (data is not MemoryStream ms) return null;
            var bytes = ms.ToArray();
            if (bytes.Length < 4) return null;
            return BitConverter.ToInt32(BitConverter.IsLittleEndian ? bytes : bytes.Reverse().ToArray(), 0);
        }
        catch
        {
            return null;
        }
    }
}
