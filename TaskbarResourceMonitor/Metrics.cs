using System.Diagnostics;
using System.Globalization;
using System.Management;
using LibreHardwareMonitor.Hardware;

namespace TaskbarResourceMonitor;

internal sealed class Metrics : IDisposable
{
    private readonly PerformanceCounter _cpuCounter;
    private readonly Computer _computer;

    private DateTime _nextHardwarePoll = DateTime.MinValue;
    private double? _lastTempC;
    private double? _lastCpuClockMhz;

    public Metrics()
    {
        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _ = _cpuCounter.NextValue(); // prime

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true
        };
        try { _computer.Open(); } catch { /* optional */ }
    }

    public (double cpu, double mem, double? tempC, double? cpuClockMhz) Sample()
    {
        var cpu = Safe(() => (double)_cpuCounter.NextValue(), 0);
        var mem = Safe(GetMemPercent, 0);

        if (DateTime.UtcNow >= _nextHardwarePoll)
        {
            _nextHardwarePoll = DateTime.UtcNow.AddSeconds(4);
            _lastTempC = TryGetTempC() ?? TryGetAcpiTempC();
            _lastCpuClockMhz = TryGetCpuMaxClockMhz();
        }

        return (cpu, mem, _lastTempC, _lastCpuClockMhz);
    }

    /// <summary>Max reported core/bus clock in MHz (LibreHardwareMonitor), refreshed on hardware poll interval.</summary>
    private double? TryGetCpuMaxClockMhz()
    {
        try
        {
            double? max = null;
            foreach (var hw in _computer.Hardware)
            {
                if (hw.HardwareType != HardwareType.Cpu) continue;
                TryUpdate(hw);
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Clock) continue;
                    var v = s.Value;
                    if (v is null) continue;
                    var mhz = (double)v.Value;
                    if (!double.IsFinite(mhz) || mhz < 200) continue;
                    max = max is null ? mhz : Math.Max(max.Value, mhz);
                }
            }

            return max;
        }
        catch
        {
            return null;
        }
    }

    private static double GetMemPercent()
    {
        using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
        foreach (var o in searcher.Get().Cast<ManagementObject>())
        {
            var total = Convert.ToDouble(o["TotalVisibleMemorySize"], CultureInfo.InvariantCulture);
            var free = Convert.ToDouble(o["FreePhysicalMemory"], CultureInfo.InvariantCulture);
            if (total <= 0) return 0;
            return (1.0 - (free / total)) * 100.0;
        }
        return 0;
    }

    private double? TryGetTempC()
    {
        try
        {
            double? best = null;
            foreach (var hw in _computer.Hardware)
            {
                TryUpdate(hw);
                best = Max(best, ScanTemps(hw));
                foreach (var sh in hw.SubHardware)
                {
                    TryUpdate(sh);
                    best = Max(best, ScanTemps(sh));
                }
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    private static void TryUpdate(IHardware hw)
    {
        try { hw.Update(); } catch { }
    }

    private static double? ScanTemps(IHardware hw)
    {
        double? best = null;
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType != SensorType.Temperature) continue;
            var v = s.Value;
            if (v is null) continue;
            var c = (double)v.Value;
            if (!double.IsFinite(c) || c < -30 || c > 130) continue;
            best = Max(best, c);
        }
        return best;
    }

    private static double? TryGetAcpiTempC()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            double? best = null;
            foreach (var o in searcher.Get().Cast<ManagementObject>())
            {
                var raw = Convert.ToDouble(o["CurrentTemperature"], CultureInfo.InvariantCulture);
                var c = (raw / 10.0) - 273.15;
                if (!double.IsFinite(c) || c < -30 || c > 130) continue;
                best = Max(best, c);
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }

    private static double? Max(double? a, double? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return Math.Max(a.Value, b.Value);
    }

    public void Dispose()
    {
        _cpuCounter.Dispose();
        try { _computer.Close(); } catch { }
    }
}

