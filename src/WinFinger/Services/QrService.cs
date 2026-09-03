using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace WinFinger.Services;

/// <summary>
/// 二维码生成 / 识别（ZXing.Net）。全部为纯计算，产出的位图都会 Freeze，可在后台线程调用。
/// </summary>
public static class QrService
{
    /// <summary>识别前的最长边上限，超过先等比缩小以控制耗时。</summary>
    private const int MaxDecodeSide = 1600;

    /// <summary>生成二维码位图（黑白，已 Freeze）。内容过长会抛 <see cref="InvalidOperationException"/>。</summary>
    public static BitmapSource Encode(string text, int size = 512, int margin = 1)
    {
        if (string.IsNullOrEmpty(text)) throw new InvalidOperationException("内容为空，无法生成二维码");
        if (size < 32) size = 32;

        BitMatrix matrix;
        try
        {
            var hints = new Dictionary<EncodeHintType, object>
            {
                [EncodeHintType.MARGIN] = margin,
                [EncodeHintType.CHARACTER_SET] = "UTF-8",
                [EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M
            };
            matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, size, size, hints);
        }
        catch (WriterException)
        {
            throw new InvalidOperationException("内容过长，无法生成二维码");
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("内容过长，无法生成二维码");
        }

        int width = matrix.Width;
        int height = matrix.Height;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                byte v = matrix[x, y] ? (byte)0 : (byte)255;
                int i = row + x * 4;
                pixels[i] = v;      // B
                pixels[i + 1] = v;  // G
                pixels[i + 2] = v;  // R
                pixels[i + 3] = 255;
            }
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, stride, 0);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>生成二维码并编码为 PNG 字节。</summary>
    public static byte[] EncodePng(string text, int size = 512)
    {
        var bitmap = Encode(text, size);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>从 PNG 字节中识别条码内容；识别不到或出错返回 null。</summary>
    public static string? Decode(byte[] png)
    {
        if (png is null || png.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(png, writable: false);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;

            BitmapSource source = decoder.Frames[0];
            int longest = Math.Max(source.PixelWidth, source.PixelHeight);
            if (longest > MaxDecodeSide)
            {
                double scale = (double)MaxDecodeSide / longest;
                source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();

            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            if (width <= 0 || height <= 0) return null;
            int stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            var luminance = new RGBLuminanceSource(pixels, width, height,
                RGBLuminanceSource.BitmapFormat.BGRA32);
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions
                {
                    TryHarder = true,
                    PossibleFormats = new[]
                    {
                        BarcodeFormat.QR_CODE,
                        BarcodeFormat.DATA_MATRIX,
                        BarcodeFormat.CODE_128,
                        BarcodeFormat.EAN_13
                    }
                }
            };
            return reader.Decode(luminance)?.Text;
        }
        catch
        {
            return null;
        }
    }
}
