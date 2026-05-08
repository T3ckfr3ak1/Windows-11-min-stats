using System.Diagnostics;
using System.Globalization;
using System.Management;
using LibreHardwareMonitor.Hardware;

namespace TaskbarResourceMonitor;

internal sealed class Metrics : IDisposable
{
    private readonly PerformanceCounter _cpuCounter;
    private readonly Computer _computer;
    private readonly List<PerformanceCounter> _frequencyCounters = [];
    private DateTime _nextHardwarePoll = DateTime.MinValue;
    private DateTime _nextWmiCpuClockPoll = DateTime.MinValue;
    private double? _lastTempC;
    /// <summary>LibreHardwareMonitor clock sensors; refreshed every hardware poll (~4s).</summary>
    private double? _lhmCpuClockMhz;
    private double? _cachedWmiCpuMhz;

    public Metrics()
    {
        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _ = _cpuCounter.NextValue(); // prime

        TryAttachProcessorFrequencyCounters();

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
            _lhmCpuClockMhz = TryGetCpuMaxClockMhzLhm();
        }

        var mhz = CombineCpuMegahertzPreferred();

        return (cpu, mem, _lastTempC, mhz);
    }

    /// <summary>Prefer LHM (hardware poll), then live perf counters, then WMI nominal speed (slow path / cached).</summary>
    private double? CombineCpuMegahertzPreferred()
    {
        if (_lhmCpuClockMhz is { } lh && lh >= 300 && lh <= 9200 && double.IsFinite(lh))
            return lh;

        var pc = TryReadProcessorFrequencyCounterMhz();
        if (pc is { } f && double.IsFinite(f) && f >= 350 && f <= 9200)
            return f;

        var wmi = TryGetCpuMhzWmiCached();
        if (wmi is not null)
            return wmi;

        if (_lhmCpuClockMhz is { } lh2 && double.IsFinite(lh2))
            return lh2;

        return pc;
    }

    private void TryAttachProcessorFrequencyCounters()
    {
        if (!OperatingSystem.IsWindows()) return;
        TryAddFreq("Processor Information", "Processor Frequency", "0,_Total");
        TryAddFreq("Processor Information", "Processor Frequency", "_Total");

        try
        {
            if (!PerformanceCounterCategory.Exists("Processor Information")) return;
            var cat = new PerformanceCounterCategory("Processor Information");
            foreach (var name in cat.GetInstanceNames())
            {
                if (!name.AsSpan().EndsWith("_Total", StringComparison.OrdinalIgnoreCase))
                    continue;
                TryAddFreq("Processor Information", "Processor Frequency", name);
            }
        }
        catch { /* ignore */ }
    }

    private void TryAddFreq(string categoryName, string counterName, string instanceName)
    {
        try
        {
            var c = new PerformanceCounter(categoryName, counterName, instanceName, readOnly: true);
            _ = c.NextValue();
            _frequencyCounters.Add(c);
        }
        catch { /* instance may not exist on this SKU */ }
    }

    private double? TryReadProcessorFrequencyCounterMhz()
    {
        foreach (var c in _frequencyCounters)
        {
            try
            {
                var v = (double)c.NextValue();
                if (double.IsFinite(v) && v > 150 && v < 15000)
                    return v;
            }
            catch { /* disposed / access */ }
        }
        return null;
    }

    private double? TryGetCpuMhzWmiCached()
    {
        var now = DateTime.UtcNow;
        if (now < _nextWmiCpuClockPoll)
            return _cachedWmiCpuMhz;

        _cachedWmiCpuMhz = TryGetCpuMhzWmi();
        _nextWmiCpuClockPoll = now.AddSeconds(5);
        return _cachedWmiCpuMhz;
    }

    /// <summary>Win32 Processor CurrentClockSpeed / MaxClockSpeed (MHz nominally).</summary>
    private static double? TryGetCpuMhzWmi()
    {
        try
        {
            double best = 0;
            using var searcher = new ManagementObjectSearcher(@"SELECT CurrentClockSpeed, MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject o in searcher.Get())
            {
                var curRaw = Convert.ToUInt32(o["CurrentClockSpeed"], CultureInfo.InvariantCulture);
                var maxRaw = Convert.ToUInt32(o["MaxClockSpeed"], CultureInfo.InvariantCulture);
                var sel = Math.Max(curRaw, maxRaw);
                if (sel > best) best = sel;
            }

            return best > 150 ? best : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Max reported sensible CPU-ish clock MHz (LibreHardwareMonitor).</summary>
    private double? TryGetCpuMaxClockMhzLhm()
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
                    if (!double.IsFinite(mhz) || mhz < 100 || mhz > 9000)
                        continue;
                    var nm = (s.Name ?? "").ToUpperInvariant();
                    // Prefer CPU domain over DRAM / memory clocks when possible.
                    if (nm.Contains("DRAM", StringComparison.Ordinal) ||
                        nm.Contains("MEMORY", StringComparison.Ordinal))
                        continue;
                    max = max is null ? mhz : Math.Max(max.Value, mhz);
                }
            }

            return max is > 0 ? max : null;
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
        foreach (var f in _frequencyCounters)
            try { f.Dispose(); } catch { /* ignore */ }
        try { _computer.Close(); } catch { }
    }
}

