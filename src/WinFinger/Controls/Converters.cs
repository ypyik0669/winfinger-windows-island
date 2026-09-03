using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinFinger.Controls;

/// <summary>"刚刚 / 5分钟 / 2小时 / 3天" style relative timestamps (mac SwiftUI .relative).</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime time) return "";
        var delta = DateTime.Now - time;
        if (delta.TotalSeconds < 60) return "刚刚";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}分钟";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}小时";
        if (delta.TotalDays < 30) return $"{(int)delta.TotalDays}天";
        if (delta.TotalDays < 365) return $"{(int)(delta.TotalDays / 30)}个月";
        return $"{(int)(delta.TotalDays / 365)}年";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Loads a decode-limited, frozen thumbnail from an image path (null-safe).</summary>
public sealed class ImagePathToThumbnailConverter : IValueConverter
{
    public int DecodeWidth { get; set; } = 240;

    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = DecodeWidth;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Shell icon for a file path (mac NSWorkspace.icon(forFile:)), cached per extension/path.</summary>
public sealed class FilePathToIconConverter : IValueConverter
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return null;
        string key = Directory.Exists(path) ? "<dir>" : Path.GetExtension(path) is { Length: > 0 } ext && ext != ".exe" && ext != ".lnk" ? ext : path;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }
        ImageSource? result = null;
        try
        {
            if (Directory.Exists(path))
            {
                result = ShellIcon(path, Interop.NativeMethods.FILE_ATTRIBUTE_DIRECTORY);
            }
            else if (File.Exists(path))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    result = src;
                }
            }
        }
        catch
        {
            result = null;
        }
        lock (Cache)
        {
            Cache[key] = result;
        }
        return result;
    }

    /// <summary>Shell 图标（文件夹等）：SHGetFileInfo + USEFILEATTRIBUTES，不碰真实磁盘属性。</summary>
    private static ImageSource? ShellIcon(string path, uint attributes)
    {
        var info = new Interop.NativeMethods.SHFILEINFO();
        IntPtr handle = Interop.NativeMethods.SHGetFileInfo(path, attributes, ref info,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<Interop.NativeMethods.SHFILEINFO>(),
            Interop.NativeMethods.SHGFI_ICON | Interop.NativeMethods.SHGFI_SMALLICON |
            Interop.NativeMethods.SHGFI_USEFILEATTRIBUTES);
        if (handle == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
        finally
        {
            Interop.NativeMethods.DestroyIcon(info.hIcon);
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Note body → single-line preview, "空便签" when empty (mac notes list row).</summary>
public sealed class NotePreviewConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var body = (value as string ?? "").ReplaceLineEndings(" ").Trim();
        return body.Length == 0 ? "空便签" : body;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Null/empty → Collapsed.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Enum equality → Visibility (parameter = enum member name).</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is not null && parameter is string name && string.Equals(value.ToString(), name, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True → Visible, false → Collapsed (parameter "invert" flips).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is true;
        if (parameter is string p && p == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>把 ContentDetector 认出的颜色文本变成色点画刷（认不出时透明）。</summary>
public sealed class ColorStringToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s && Services.ContentDetector.TryParseColor(s, out var color))
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>卡片上一个内联动作按钮的数据（动作 + 它作用的条目）。</summary>
public sealed record InlineActionItem(Models.ActionDefinition Definition, Models.ClipboardEntry Entry)
{
    public string Title => Definition.Title;

    /// <summary>Icon 是 4 位十六进制时当 Segoe 字形，否则原样当文字 / emoji。</summary>
    public string Glyph => ActionGlyph.Text(Definition.Icon);

    public bool IsGlyphFont => ActionGlyph.IsGlyph(Definition.Icon);
}

/// <summary>动作图标解析：4 位十六进制码 → Segoe 字形，其余原样。</summary>
public static class ActionGlyph
{
    public static bool IsGlyph(string? icon) =>
        icon is { Length: 4 } && icon.All(Uri.IsHexDigit);

    public static string Text(string? icon)
    {
        if (string.IsNullOrEmpty(icon)) return ""; // More
        if (!IsGlyph(icon)) return icon!;
        return char.ConvertFromUtf32(int.Parse(icon!, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}

/// <summary>条目 → 最多 3 个内联动作（卡片第三列的图标按钮）。</summary>
public sealed class EntryInlineActionsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Models.ClipboardEntry entry) return Array.Empty<InlineActionItem>();
        var catalog = Services.ActionCatalogService.Current;
        if (catalog is null) return Array.Empty<InlineActionItem>();
        try
        {
            return catalog.For(entry)
                .Where(d => d.Inline)
                .Take(3)
                .Select(d => new InlineActionItem(d, entry))
                .ToList();
        }
        catch
        {
            return Array.Empty<InlineActionItem>();
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>字符串等于参数时 Visible（大小写不敏感），否则 Collapsed。</summary>
public sealed class StringEqualsVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
