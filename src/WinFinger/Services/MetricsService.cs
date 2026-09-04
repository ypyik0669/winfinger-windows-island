using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using WinFinger.Interop;

namespace WinFinger.Services;

/// <summary>Samples network throughput and memory load once per second (mac MetricsMonitor).</summary>
public sealed partial class MetricsService : ObservableObject
{
    [ObservableProperty] private double _downloadBytesPerSecond;
    [ObservableProperty] private double _uploadBytesPerSecond;
    /// <summary>Used physical memory ratio 0..1 (excludes reclaimable standby cache, like mac).</summary>
    [ObservableProperty] private double _memoryUsedRatio;
    [ObservableProperty] private int _memoryLoadPercent;
    [ObservableProperty] private string _downloadText = "0 B/s";
    [ObservableProperty] private string _uploadText = "0 B/s";
    [ObservableProperty] private string _memoryText = "--%";

    private readonly DispatcherTimer _timer;
    private long _previousReceived;
    private long _previousSent;
    private DateTime _previousSampleAt;
    private bool _hasBaseline;

    public MetricsService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => Sample();
    }

    public void Start()
    {
        Sample(); // establishes baseline; first tick reports 0
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    /// <summary>省电模式下两秒采一次：每次采样都可能带来一次整窗重绘，频率减半开销就减半。</summary>
    public void SetPowerSaver(bool on) => _timer.Interval = TimeSpan.FromSeconds(on ? 2 : 1);

    private void Sample()
    {
        if (!TryReadNetworkTotals(out long received, out long sent))
            return; // 瞬时失败（网卡正在增删）：跳过这一拍

        var now = DateTime.UtcNow;
        if (_hasBaseline)
        {
            var elapsed = Math.Max((now - _previousSampleAt).TotalSeconds, 0.2);
            DownloadBytesPerSecond = received >= _previousReceived ? (received - _previousReceived) / elapsed : 0;
            UploadBytesPerSecond = sent >= _previousSent ? (sent - _previousSent) / elapsed : 0;
        }
        _previousReceived = received;
        _previousSent = sent;
        _previousSampleAt = now;
        _hasBaseline = true;

        var status = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (NativeMethods.GlobalMemoryStatusEx(ref status) && status.ullTotalPhys > 0)
        {
            // ullAvailPhys already counts standby (cache) pages as available → "in use" excludes reclaimable cache
            double ratio = (status.ullTotalPhys - status.ullAvailPhys) / (double)status.ullTotalPhys;
            MemoryLoadPercent = (int)Math.Round(Math.Clamp(ratio, 0, 1) * 100);
            // 量化到整数百分比再发布：原始比例每秒都有微小变化，内存环每秒都会跑一次 200ms 动画，
            // 而这个透明分层窗口每一帧都要整窗回读，一秒十几帧就是待机时最大的 CPU 开销
            MemoryUsedRatio = MemoryLoadPercent / 100.0;
        }

        DownloadText = CompactBytesPerSecond(DownloadBytesPerSecond);
        UploadText = CompactBytesPerSecond(UploadBytesPerSecond);
        MemoryText = $"{MemoryLoadPercent}%";
    }

    /// <summary>
    /// 全部「已连接、非回环、非过滤镜像」接口的累计收发字节。走 GetIfTable2，一次调用几十微秒；
    /// 口径与 NetworkInterface.GetAllNetworkInterfaces() 一致（后者就是这张表去掉过滤接口后的视图）。
    /// </summary>
    public static bool TryReadNetworkTotals(out long received, out long sent)
    {
        received = 0;
        sent = 0;
        IntPtr table = IntPtr.Zero;
        try
        {
            if (NativeMethods.GetIfTable2(out table) != 0 || table == IntPtr.Zero) return false;
            int count = System.Runtime.InteropServices.Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                IntPtr row = table + NativeMethods.MibIfRow2.TableHeaderSize + i * NativeMethods.MibIfRow2.Size;
                if (System.Runtime.InteropServices.Marshal.ReadInt32(row, NativeMethods.MibIfRow2.OperStatusOffset) != NativeMethods.MibIfRow2.IfOperStatusUp) continue;
                if (System.Runtime.InteropServices.Marshal.ReadInt32(row, NativeMethods.MibIfRow2.TypeOffset) == NativeMethods.MibIfRow2.IfTypeSoftwareLoopback) continue;
                if ((System.Runtime.InteropServices.Marshal.ReadByte(row, NativeMethods.MibIfRow2.FlagsOffset) & NativeMethods.MibIfRow2.FlagFilterInterface) != 0) continue;
                received += System.Runtime.InteropServices.Marshal.ReadInt64(row, NativeMethods.MibIfRow2.InOctetsOffset);
                sent += System.Runtime.InteropServices.Marshal.ReadInt64(row, NativeMethods.MibIfRow2.OutOctetsOffset);
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (table != IntPtr.Zero) NativeMethods.FreeMibTable(table);
        }
    }

    /// <summary>mac MacFingerMetricFormatter.bytesPerSecond: "0 B/s", "12.34 KB/s", "2.1 MB/s", "120 MB/s".</summary>
    public static string BytesPerSecond(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return "0 B/s";
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        double number = value;
        int index = 0;
        while (number >= 1024 && index < units.Length - 1)
        {
            number /= 1024;
            index++;
        }
        var ci = CultureInfo.InvariantCulture;
        if (number >= 100) return string.Format(ci, "{0:0} {1}", number, units[index]);
        if (number >= 10) return string.Format(ci, "{0:0.0} {1}", number, units[index]);
        return string.Format(ci, "{0:0.00} {1}", number, units[index]);
    }

    /// <summary>mac MacFingerMetricFormatter.compactBytesPerSecond: short form for the island bar.</summary>
    public static string CompactBytesPerSecond(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return "0 B/s";
        var ci = CultureInfo.InvariantCulture;
        if (value < 1024) return string.Format(ci, "{0:0} B/s", value);
        string[] units = { "KB/s", "MB/s", "GB/s" };
        double number = value / 1024;
        int index = 0;
        while (number >= 1024 && index < units.Length - 1)
        {
            number /= 1024;
            index++;
        }
        return string.Format(ci, "{0:0.0} {1}", number, units[index]);
    }

    /// <summary>Legacy short format kept for callers that need a very narrow label.</summary>
    public static string FormatRate(double bytesPerSecond) => CompactBytesPerSecond(bytesPerSecond);
}
