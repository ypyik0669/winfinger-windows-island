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

    private void Sample()
    {
        long received = 0, sent = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var stats = nic.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
        }
        catch
        {
            // Interface enumeration can transiently fail (adapter change); skip this tick.
            return;
        }

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
            MemoryUsedRatio = Math.Clamp(ratio, 0, 1);
            MemoryLoadPercent = (int)Math.Round(MemoryUsedRatio * 100);
        }

        DownloadText = CompactBytesPerSecond(DownloadBytesPerSecond);
        UploadText = CompactBytesPerSecond(UploadBytesPerSecond);
        MemoryText = $"{MemoryLoadPercent}%";
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
