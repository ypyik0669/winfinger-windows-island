using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using WinFinger.Interop;
using WinFinger.ViewModels;
using WinFinger.Views;

namespace WinFinger.Services;

/// <summary>
/// 区域截图：冻结整块虚拟屏 → 每显示器铺一层遮罩供拖选 → 裁剪入库、写剪贴板，
/// 识字变体再接一次 OCR 把文字也放到剪贴板。
/// </summary>
public sealed class ScreenshotService
{
    private readonly AppViewModel _model;

    public ScreenshotService(AppViewModel model) => _model = model;

    /// <summary>正在选区中；期间再次触发热键会被忽略。</summary>
    public bool IsCapturing { get; private set; }

    /// <summary>整块虚拟屏的 PNG（设备像素 1:1）；失败返回 null。</summary>
    public static byte[]? CaptureFullVirtualScreen()
    {
        var (x, y, w, h) = VirtualScreen();
        return GdiCapture.CapturePng(x, y, w, h);
    }

    private static (int X, int Y, int Width, int Height) VirtualScreen() => (
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

    /// <summary>拉起选区界面，返回所选区域的 PNG；取消或失败返回 null。</summary>
    public async Task<byte[]?> CaptureRegionAsync()
    {
        if (IsCapturing) return null;
        IsCapturing = true;
        var overlays = new List<Window>();
        try
        {
            if (_model.IsExpanded)
            {
                _model.CollapseWithoutFocusRestore();
                await Task.Delay(150).ConfigureAwait(true);
            }

            var (vx, vy, vw, vh) = VirtualScreen();
            var frozen = GdiCapture.CaptureBitmap(vx, vy, vw, vh);
            if (frozen is null) return null;

            var tcs = new TaskCompletionSource<Int32Rect?>(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var rc in NativeMethods.MonitorRects())
            {
                int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
                if (w <= 0 || h <= 0) continue;
                var crop = Clip(frozen, new Int32Rect(rc.Left - vx, rc.Top - vy, w, h));
                if (crop is null) continue;
                var overlay = new ScreenCaptureOverlay(crop, rc, tcs);
                overlay.Closed += (_, _) => tcs.TrySetResult(null); // 被外力关掉也别把调用方吊死
                overlays.Add(overlay);
                overlay.Show();
            }
            if (overlays.Count == 0) return null;
            overlays[0].Activate();

            var selection = await tcs.Task.ConfigureAwait(true);
            CloseAll(overlays);
            if (selection is not { Width: > 0, Height: > 0 } sel) return null;

            var local = new Int32Rect(sel.X - vx, sel.Y - vy, sel.Width, sel.Height);
            var cropped = Clip(frozen, local);
            if (cropped is null) return null;
            return await Task.Run(() => GdiCapture.EncodePng(cropped)).ConfigureAwait(true);
        }
        finally
        {
            CloseAll(overlays);
            IsCapturing = false;
        }
    }

    /// <summary>截图 → 入历史 + 写剪贴板；<paramref name="ocr"/> 为真时再识字并把文字也复制走。</summary>
    public async Task CaptureToHistoryAsync(bool ocr)
    {
        if (IsCapturing) return;

        var png = await CaptureRegionAsync().ConfigureAwait(true);
        if (png is null || png.Length == 0) return;

        var entry = _model.ClipboardStore.AppendImage(png, "截图", "winfinger.screenshot");
        _model.ClipboardMonitor.CopyPng(png);

        int width = 0, height = 0;
        try
        {
            var probe = new BitmapImage();
            probe.BeginInit();
            probe.StreamSource = new MemoryStream(png);
            probe.CacheOption = BitmapCacheOption.OnLoad;
            probe.EndInit();
            width = probe.PixelWidth;
            height = probe.PixelHeight;
        }
        catch
        {
            // 尺寸只用于提示文案，取不到就不显示
        }
        _model.Notifications.Post("📷", width > 0 ? $"已截图 {width}×{height}" : "已截图");

        if (!ocr || entry is null) return;

        var lang = _model.SettingsStore.Settings.OcrLanguage;
        var hint = string.IsNullOrWhiteSpace(lang) || lang == "auto" ? null : lang;
        OcrResult? result;
        try
        {
            result = await _model.Ocr.RecognizeEntryAsync(entry, _model.ClipboardStore, hint, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch
        {
            result = null;
        }

        var text = result?.Text?.Trim() ?? string.Empty;
        if (text.Length > 0)
        {
            _model.ClipboardMonitor.CopyText(text);
            var preview = text.Length > 24 ? text[..24] + "…" : text;
            _model.Notifications.Post("🔤", preview);
        }
        else if (_model.Ocr.LastStatus == OcrStatus.NoEngine)
        {
            _model.Notifications.Post("🔤", OcrService.UnavailableMessage);
        }
        else
        {
            _model.Notifications.Post("🔤", "未识别到文字");
        }
    }

    private static BitmapSource? Clip(BitmapSource source, Int32Rect rect)
    {
        int x = Math.Clamp(rect.X, 0, source.PixelWidth);
        int y = Math.Clamp(rect.Y, 0, source.PixelHeight);
        int w = Math.Clamp(rect.Width, 0, source.PixelWidth - x);
        int h = Math.Clamp(rect.Height, 0, source.PixelHeight - y);
        if (w <= 0 || h <= 0) return null;
        var cropped = new CroppedBitmap(source, new Int32Rect(x, y, w, h));
        cropped.Freeze();
        return cropped;
    }

    private static void CloseAll(List<Window> overlays)
    {
        foreach (var w in overlays)
        {
            try
            {
                w.Close();
            }
            catch
            {
                // 已经关掉了
            }
        }
        overlays.Clear();
    }
}
