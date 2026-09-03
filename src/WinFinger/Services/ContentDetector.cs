using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaColor = System.Windows.Media.Color;

namespace WinFinger.Services;

/// <summary>纯静态内容类型识别（可单测，无 UI/IO 依赖，路径类型除外）。</summary>
public static class ContentDetector
{
    public const string Url = "url";
    public const string Email = "email";
    public const string Phone = "phone";
    public const string Color = "color";
    public const string Json = "json";
    public const string Timestamp = "timestamp";
    public const string DateText = "date";
    public const string Path = "path";
    public const string Markdown = "markdown";
    public const string Code = "code";
    public const string Plain = "plain";

    /// <summary>超过该长度直接判为纯文本（避免大文本上跑正则/JSON 解析）。</summary>
    private const int MaxDetectLength = 64 * 1024;

    /// <summary>JSON 解析的体积上限。</summary>
    private const int MaxJsonLength = 256 * 1024;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly Regex UrlSchemeRegex = new(@"^(https?|ftp)://\S+$", Opts | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex UrlWwwRegex = new(@"^www\.\S+\.\S+$", Opts | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex EmailRegex = new(@"^[^\s@]+@[^\s@.]+(\.[^\s@.]+)+$", Opts, RegexTimeout);
    private static readonly Regex PhoneShapeRegex = new(@"^\+?[\d\s\-()]{7,20}$", Opts, RegexTimeout);
    private static readonly Regex HexColorRegex = new(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", Opts, RegexTimeout);
    private static readonly Regex RgbColorRegex = new(
        @"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*([01]?(?:\.\d+)?)\s*)?\)$", Opts | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex HslColorRegex = new(
        @"^hsla?\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(\d{1,3}(?:\.\d+)?)%\s*,\s*(\d{1,3}(?:\.\d+)?)%\s*(?:,\s*([01]?(?:\.\d+)?)\s*)?\)$", Opts | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex TimestampRegex = new(@"^\d{10}$|^\d{13}$", Opts, RegexTimeout);
    private static readonly Regex DrivePathRegex = new(@"^[a-zA-Z]:\\", Opts, RegexTimeout);
    private static readonly Regex UncPathRegex = new(@"^\\\\", Opts, RegexTimeout);
    private static readonly Regex CjkRegex = new("[一-鿿㐀-䶿぀-ヿ가-힯]", Opts, RegexTimeout);
    private static readonly Regex MarkdownInlineLinkRegex = new(@"\]\(", Opts, RegexTimeout);

    // ── 电话：只认典型号码形态，避免把 "1234567"、"20260903" 这类裸数字串误判 ──
    /// <summary>中国大陆手机号。</summary>
    private static readonly Regex CnMobileRegex = new(@"^1[3-9]\d{9}$", Opts, RegexTimeout);
    /// <summary>400/800 服务号。</summary>
    private static readonly Regex ServiceNumberRegex = new(@"^[48]00\d{7}$", Opts, RegexTimeout);
    /// <summary>0 开头的区号 + 号码。</summary>
    private static readonly Regex TrunkNumberRegex = new(@"^0\d{9,11}$", Opts, RegexTimeout);
    /// <summary>用分隔符隔开的两段数字（至少各 2 位），如 "010-12345678"。</summary>
    private static readonly Regex SeparatedDigitGroupsRegex = new(@"\d{2,}[\s\-()]+\d{2,}", Opts, RegexTimeout);
    /// <summary>紧凑日期 yyyyMMdd，用于把它排除在电话之外。</summary>
    private static readonly Regex CompactDateRegex = new(@"^(?:19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])$", Opts, RegexTimeout);

    // ── 日期：必须是完整日期形态，"1-2"、"10-20" 这类不算 ──
    private static readonly Regex IsoDateRegex = new(
        @"^\d{4}[-/]\d{1,2}[-/]\d{1,2}(?:[ T]\d{1,2}:\d{2}(?::\d{2})?(?:\.\d+)?\s*(?:Z|[+-]\d{2}:?\d{2})?)?$", Opts, RegexTimeout);
    private static readonly Regex DayMonthYearDateRegex = new(
        @"^\d{1,2}[-/]\d{1,2}[-/]\d{4}(?:[ T]\d{1,2}:\d{2}(?::\d{2})?)?$", Opts, RegexTimeout);
    private static readonly Regex CjkDateRegex = new(@"^(\d{4})年(\d{1,2})月(\d{1,2})日", Opts, RegexTimeout);

    // ── 代码：结构性特征（非普通散文会出现的写法） ──
    private static readonly Regex ImportFromRegex = new(@"\bimport\s+.+\s+from\b|(?m)^\s*(?:import|from)\s+\w", Opts, RegexTimeout);
    private static readonly Regex DefRegex = new(@"\bdef\s+\w+\s*\(", Opts, RegexTimeout);
    private static readonly Regex FunctionDeclRegex = new(@"\bfunction\s*\w*\s*\(", Opts, RegexTimeout);

    /// <summary>识别文本内容类型；任何异常（含正则超时）都退回 plain，绝不抛出。</summary>
    public static string Detect(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxDetectLength) return Plain;
        try
        {
            return DetectCore(text);
        }
        catch
        {
            return Plain;
        }
    }

    private static string DetectCore(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return Plain;

        bool singleLine = trimmed.IndexOf('\n') < 0 && trimmed.IndexOf('\r') < 0;
        if (singleLine)
        {
            if (IsUrl(trimmed)) return Url;
            if (EmailRegex.IsMatch(trimmed)) return Email;
            // 时间戳先于电话：10/13 位纯数字同时符合电话形状，需优先归为时间戳。
            if (TryParseTimestamp(trimmed, out _, out _)) return Timestamp;
            if (TryParseColor(trimmed, out _)) return Color;
            // 日期先于电话："2026-09-03" 也符合"数字-数字"的电话分组形态
            if (IsDateText(trimmed)) return DateText;
            if (IsPhone(trimmed)) return Phone;
            if (IsPath(trimmed)) return Path;
        }

        if (IsJson(trimmed)) return Json;
        if (IsMarkdown(trimmed)) return Markdown;
        if (IsCode(trimmed)) return Code;
        return Plain;
    }

    private static bool IsUrl(string s)
    {
        if (UrlSchemeRegex.IsMatch(s))
            return Uri.TryCreate(s, UriKind.Absolute, out _);
        if (UrlWwwRegex.IsMatch(s))
            return Uri.TryCreate("http://" + s, UriKind.Absolute, out _);
        return false;
    }

    private static bool IsPhone(string s)
    {
        if (!PhoneShapeRegex.IsMatch(s)) return false;
        int digits = s.Count(char.IsAsciiDigit);
        if (digits is < 7 or > 15) return false;

        // 带国际区号前缀，直接认可
        if (s.StartsWith('+')) return true;

        // 带分隔符：要求确实是"数字段 + 分隔符 + 数字段"，而非零散的连字符
        bool hasSeparator = s.Any(c => c is ' ' or '-' or '(' or ')');
        if (hasSeparator) return SeparatedDigitGroupsRegex.IsMatch(s);

        // 纯数字串：排除 yyyyMMdd，且只接受手机号 / 400-800 服务号 / 0 开头的固话
        if (CompactDateRegex.IsMatch(s)) return false;
        return CnMobileRegex.IsMatch(s) || ServiceNumberRegex.IsMatch(s) || TrunkNumberRegex.IsMatch(s);
    }

    private static bool IsDateText(string s)
    {
        if (s.Length is < 8 or > 40) return false;

        var cjk = CjkDateRegex.Match(s);
        if (cjk.Success)
        {
            var normalized = $"{cjk.Groups[1].Value}-{cjk.Groups[2].Value}-{cjk.Groups[3].Value}";
            return DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        }

        if (!IsoDateRegex.IsMatch(s) && !DayMonthYearDateRegex.IsMatch(s)) return false;
        return DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out _)
            || DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static bool IsPath(string s)
    {
        if (UncPathRegex.IsMatch(s)) return true;   // UNC 路径不做存在性检查（可能离线）
        if (!DrivePathRegex.IsMatch(s)) return false;
        try
        {
            return File.Exists(s) || Directory.Exists(s);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsJson(string s)
    {
        if (s.Length > MaxJsonLength) return false;
        char first = s[0];
        if (first != '{' && first != '[') return false;
        try
        {
            using var _ = JsonDocument.Parse(s);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMarkdown(string s)
    {
        if (MarkdownInlineLinkRegex.IsMatch(s)) return true;
        int hits = 0;
        foreach (var raw in s.Split('\n'))
        {
            var line = raw.TrimStart();
            if (line.StartsWith('#') || line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("```"))
                hits++;
            if (hits >= 2) return true;
        }
        return false;
    }

    /// <summary>
    /// 代码判定：单独的关键词不作数（"Please return the item; thanks." 是散文不是代码）。
    /// 必须有 ≥2 个结构性特征，或者 1 个结构性特征 + 1 个语言关键词。
    /// </summary>
    private static bool IsCode(string s)
    {
        var lines = s.Split('\n');

        int structural = 0;
        if (s.Contains('{') && s.Contains('}')) structural++;
        if (s.Contains("=>")) structural++;
        if (s.Contains("#include")) structural++;
        if (ImportFromRegex.IsMatch(s)) structural++;
        if (DefRegex.IsMatch(s)) structural++;
        if (FunctionDeclRegex.IsMatch(s)) structural++;
        if (lines.Count(l => l.TrimEnd().EndsWith(';')) >= 2) structural++;
        if (lines.Count(l => l.Length > 0 && (l[0] == '\t' || l.StartsWith("  ")) && l.Trim().Length > 0) >= 2) structural++;

        if (structural >= 2) return true;
        if (structural == 0) return false;

        int keywords = 0;
        if (s.Contains("public ")) keywords++;
        if (s.Contains("private ")) keywords++;
        if (s.Contains("static ")) keywords++;
        if (s.Contains("class ")) keywords++;
        if (s.Contains("const ")) keywords++;
        if (s.Contains("return")) keywords++;
        if (s.Contains("void ")) keywords++;
        return keywords >= 1;
    }

    /// <summary>类型的中文短标签；plain / 未知 / null 返回 null（卡片上不显示 pill）。</summary>
    public static string? Label(string? type) => type switch
    {
        Url => "链接",
        Email => "邮箱",
        Phone => "电话",
        Color => "颜色",
        Json => "JSON",
        Timestamp => "时间戳",
        DateText => "日期",
        Path => "路径",
        Markdown => "Markdown",
        Code => "代码",
        _ => null
    };

    /// <summary>解析 #rgb / #rrggbb / #rrggbbaa / rgb() / rgba() / hsl() / hsla()。</summary>
    public static bool TryParseColor(string s, out MediaColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        try
        {
            if (HexColorRegex.IsMatch(t))
            {
                var hex = t[1..];
                if (hex.Length == 3)
                {
                    byte r3 = (byte)(Convert.ToInt32($"{hex[0]}{hex[0]}", 16));
                    byte g3 = (byte)(Convert.ToInt32($"{hex[1]}{hex[1]}", 16));
                    byte b3 = (byte)(Convert.ToInt32($"{hex[2]}{hex[2]}", 16));
                    color = MediaColor.FromRgb(r3, g3, b3);
                    return true;
                }
                if (hex.Length == 6)
                {
                    color = MediaColor.FromRgb(
                        Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
                    return true;
                }
                // #rrggbbaa：末两位是 alpha
                color = MediaColor.FromArgb(
                    Convert.ToByte(hex.Substring(6, 2), 16),
                    Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
                return true;
            }

            var rgb = RgbColorRegex.Match(t);
            if (rgb.Success)
            {
                if (!TryByte(rgb.Groups[1].Value, out var r) ||
                    !TryByte(rgb.Groups[2].Value, out var g) ||
                    !TryByte(rgb.Groups[3].Value, out var b)) return false;
                byte a = ParseAlpha(rgb.Groups[4]);
                color = MediaColor.FromArgb(a, r, g, b);
                return true;
            }

            var hsl = HslColorRegex.Match(t);
            if (hsl.Success)
            {
                double h = double.Parse(hsl.Groups[1].Value, CultureInfo.InvariantCulture);
                double sat = double.Parse(hsl.Groups[2].Value, CultureInfo.InvariantCulture) / 100.0;
                double lig = double.Parse(hsl.Groups[3].Value, CultureInfo.InvariantCulture) / 100.0;
                if (sat > 1 || lig > 1) return false;
                byte a = ParseAlpha(hsl.Groups[4]);
                color = FromHsl(h, sat, lig, a);
                return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private static bool TryByte(string s, out byte value)
    {
        value = 0;
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n is < 0 or > 255) return false;
        value = (byte)n;
        return true;
    }

    private static byte ParseAlpha(Group group)
    {
        if (!group.Success || group.Value.Length == 0) return 255;
        if (!double.TryParse(group.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double a)) return 255;
        a = Math.Clamp(a, 0, 1);
        return (byte)Math.Round(a * 255);
    }

    private static MediaColor FromHsl(double h, double s, double l, byte alpha)
    {
        h = ((h % 360) + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        return MediaColor.FromArgb(alpha,
            (byte)Math.Round(Math.Clamp(r + m, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(g + m, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(b + m, 0, 1) * 255));
    }

    /// <summary>解析 10 位（秒）或 13 位（毫秒）Unix 时间戳，年份需落在 2001–2100。</summary>
    public static bool TryParseTimestamp(string s, out DateTimeOffset dt, out bool wasMillis)
    {
        dt = default;
        wasMillis = false;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        try
        {
            if (!TimestampRegex.IsMatch(t)) return false;
        }
        catch
        {
            return false;
        }
        if (!long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)) return false;

        try
        {
            wasMillis = t.Length == 13;
            dt = wasMillis ? DateTimeOffset.FromUnixTimeMilliseconds(n) : DateTimeOffset.FromUnixTimeSeconds(n);
        }
        catch
        {
            return false;
        }
        int year = dt.UtcDateTime.Year;
        if (year is < 2001 or > 2100)
        {
            dt = default;
            wasMillis = false;
            return false;
        }
        return true;
    }

    /// <summary>是否含中日韩字符（OCR 语言选择等场景用）。</summary>
    public static bool HasCjk(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        try
        {
            return CjkRegex.IsMatch(s);
        }
        catch
        {
            return false;
        }
    }
}
