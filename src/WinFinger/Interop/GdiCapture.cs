using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinFinger.Interop;

/// <summary>
/// GDI 屏幕抓取的共享底座：创建 32 位自顶向下 DIB、StretchBlt 拷贝屏幕像素、
/// 再转成 WPF 位图或 PNG 字节。区域截图与 Liquid Glass 取景共用这里的 DIB 逻辑。
/// </summary>
public static class GdiCapture
{
    /// <summary>创建一个 32bpp、自顶向下的 DIB 并选入 <paramref name="memDc"/>；失败返回 IntPtr.Zero。</summary>
    internal static IntPtr CreateBgraDib(IntPtr memDc, int width, int height, out IntPtr bits)
    {
        var bmi = new NativeMethods.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32
        };
        return NativeMethods.CreateDIBSection(memDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
    }

    /// <summary>1:1 抓取屏幕矩形（设备像素，虚拟屏坐标）为冻结的 BitmapSource；失败返回 null。</summary>
    public static BitmapSource? CaptureBitmap(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;

        IntPtr screen = IntPtr.Zero;
        IntPtr memDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr oldSel = IntPtr.Zero;
        try
        {
            screen = NativeMethods.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            memDc = NativeMethods.CreateCompatibleDC(screen);
            if (memDc == IntPtr.Zero) return null;
            dib = CreateBgraDib(memDc, width, height, out IntPtr bits);
            if (dib == IntPtr.Zero) return null;
            oldSel = NativeMethods.SelectObject(memDc, dib);

            bool ok = NativeMethods.StretchBlt(memDc, 0, 0, width, height, screen, x, y, width, height,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
            if (!ok) return null;

            int stride = width * 4;
            var buffer = new byte[stride * height];
            Marshal.Copy(bits, buffer, 0, buffer.Length);
            var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            if (memDc != IntPtr.Zero)
            {
                if (oldSel != IntPtr.Zero) NativeMethods.SelectObject(memDc, oldSel);
                NativeMethods.DeleteDC(memDc);
            }
            if (dib != IntPtr.Zero) NativeMethods.DeleteObject(dib);
            if (screen != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>1:1 抓取屏幕矩形并编码为 PNG 字节；失败返回 null。</summary>
    public static byte[]? CapturePng(int x, int y, int width, int height)
    {
        var bmp = CaptureBitmap(x, y, width, height);
        return bmp is null ? null : EncodePng(bmp);
    }

    /// <summary>把（已冻结的）位图编码成 PNG 字节。</summary>
    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
