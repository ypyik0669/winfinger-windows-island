using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinFinger.Interop;

/// <summary>
/// Self-made frosted glass: grabs the screen region behind the island into a small
/// DIB (downscale = strong pre-blur), box-blurs it, boosts saturation and brightness,
/// and writes the result into a reusable WriteableBitmap for an ImageBrush.
/// The island window is WDA_EXCLUDEFROMCAPTURE so it never captures itself.
/// </summary>
internal sealed class LiveGlassCapture : IDisposable
{
    private const int W = 128;
    private const int H = 84;
    private const int Lift = 14;

    /// <summary>Saturation boost applied to captured frames (user-adjustable).</summary>
    public double Saturation { get; set; } = 1.6;

    private readonly IntPtr _memDc;
    private readonly IntPtr _dib;
    private readonly IntPtr _oldSel;
    private readonly IntPtr _bits;
    private readonly byte[] _buf = new byte[W * H * 4];
    private readonly byte[] _tmp = new byte[W * H * 4];
    private bool _dumped;

    public WriteableBitmap Bitmap { get; } = new(W, H, 96, 96, PixelFormats.Bgra32, null);

    public LiveGlassCapture()
    {
        IntPtr screen = NativeMethods.GetDC(IntPtr.Zero);
        _memDc = NativeMethods.CreateCompatibleDC(screen);
        _dib = GdiCapture.CreateBgraDib(_memDc, W, H, out _bits);
        _oldSel = NativeMethods.SelectObject(_memDc, _dib);
        NativeMethods.SetStretchBltMode(_memDc, NativeMethods.HALFTONE);
        NativeMethods.ReleaseDC(IntPtr.Zero, screen);
    }

    /// <summary>Captures the given screen rect (device px) and refreshes <see cref="Bitmap"/>.</summary>
    public void Capture(int x, int y, int width, int height)
    {
        if (_dib == IntPtr.Zero || width <= 0 || height <= 0) return;

        IntPtr screen = NativeMethods.GetDC(IntPtr.Zero);
        bool ok = NativeMethods.StretchBlt(_memDc, 0, 0, W, H, screen, x, y, width, height, NativeMethods.SRCCOPY);
        NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        if (!ok) return;

        Marshal.Copy(_bits, _buf, 0, _buf.Length);
        BoxBlur(_buf, _tmp, horizontal: true);
        BoxBlur(_tmp, _buf, horizontal: false);
        Grade(_buf);
        Bitmap.WritePixels(new Int32Rect(0, 0, W, H), _buf, W * 4, 0);

        if (!_dumped && Environment.GetEnvironmentVariable("WINFINGER_GLASS_DUMP") == "1")
        {
            _dumped = true;
            DumpFrame();
        }
    }

    /// <summary>Radius-2 box blur, one axis per call.</summary>
    private static void BoxBlur(byte[] src, byte[] dst, bool horizontal)
    {
        const int r = 2;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int sb = 0, sg = 0, sr = 0, n = 0;
                for (int k = -r; k <= r; k++)
                {
                    int xx = horizontal ? x + k : x;
                    int yy = horizontal ? y : y + k;
                    if (xx < 0 || xx >= W || yy < 0 || yy >= H) continue;
                    int j = (yy * W + xx) * 4;
                    sb += src[j];
                    sg += src[j + 1];
                    sr += src[j + 2];
                    n++;
                }
                int i = (y * W + x) * 4;
                dst[i] = (byte)(sb / n);
                dst[i + 1] = (byte)(sg / n);
                dst[i + 2] = (byte)(sr / n);
                dst[i + 3] = 255;
            }
        }
    }

    /// <summary>Saturation boost + brightness lift — the "liquid" look Windows acrylic destroys.</summary>
    private void Grade(byte[] buf)
    {
        double s = Saturation;
        for (int i = 0; i < buf.Length; i += 4)
        {
            int b = buf[i], g = buf[i + 1], r = buf[i + 2];
            int gray = (r + g + b) / 3;
            buf[i] = Clamp(gray + (int)((b - gray) * s) + Lift);
            buf[i + 1] = Clamp(gray + (int)((g - gray) * s) + Lift);
            buf[i + 2] = Clamp(gray + (int)((r - gray) * s) + Lift);
            buf[i + 3] = 255;
        }
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    private void DumpFrame()
    {
        try
        {
            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "winfinger-glass-dump.png"),
                GdiCapture.EncodePng(Bitmap.Clone()));
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_memDc != IntPtr.Zero)
        {
            NativeMethods.SelectObject(_memDc, _oldSel);
            NativeMethods.DeleteDC(_memDc);
        }
        if (_dib != IntPtr.Zero) NativeMethods.DeleteObject(_dib);
    }
}
