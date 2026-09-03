using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinFinger.Tests;

/// <summary>测试用的极简 PNG 生成器。</summary>
internal static class TestImages
{
    public static byte[] SolidPng(int width, int height, byte r, byte g, byte b)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return ToPng(source);
    }

    public static byte[] ToPng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
