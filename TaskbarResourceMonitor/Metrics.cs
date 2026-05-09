using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using LibreHardwareMonitor.Hardware;

namespace TaskbarResourceMonitor;

internal sealed class Metrics : IDisposable
{
    private readonly PerformanceCounter _cpuCounter;
    private readonly Computer _computer;
    private readonly List<PerformanceCounter> _frequencyCounters = [];
    private readonly List<PerformanceCounter> _pctPerfCounters = [];
    private DateTime _nextHardwarePoll = DateTime.MinValue;
    private DateTime _nextWmiMaxMhzPoll = DateTime.MinValue;
    private double? _lastTempC;
    /// <summary>LibreHardwareMonitor clock sensors; refreshed each tick when possible.</summary>
    private double? _lhmCpuClockMhz;
    /// <summary>Win32_Processor MaxClockSpeed (MHz), not CurrentClockSpeed — Current is often stuck at base freq.</summary>
    private double? _wmiMaxMhz;
    private DateTime _lastNetSampleUtc = DateTime.MinValue;
    private long _lastRxBytes;
    private long _lastTxBytes;
    private long _lastIfSpeedBits;

    public Metrics()
    {
        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _ = _cpuCounter.NextValue(); // prime

        TryAttachProcessorFrequencyCounters();
        TryAttachProcessorPerformanceCounters();

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true
        };
        try { _computer.Open(); } catch { /* optional */ }
    }

    public (double cpu, double mem, double? tempC, double? cpuClockMhz, double netDownBps, double netUpBps) Sample()
    {
        var cpu = Safe(() => (double)_cpuCounter.NextValue(), 0);
        var mem = Safe(GetMemPercent, 0);

        if (DateTime.UtcNow >= _nextHardwarePoll)
        {
            _nextHardwarePoll = DateTime.UtcNow.AddSeconds(4);
            _lastTempC = TryGetTempC() ?? TryGetAcpiTempC();
        }

        // Clock: poll every tick so turbo changes show up (LHM + perf counters).
        _lhmCpuClockMhz = TryGetCpuMaxClockMhzLhm();

        var mhz = CombineCpuMegahertzPreferred();
        var (downBps, upBps) = SampleNetworkBytesPerSecond();

        return (cpu, mem, _lastTempC, mhz, downBps, upBps);
    }

    /// <summary>
    /// Prefer live sources. Avoid Win32 Processor CurrentClockSpeed — on many laptops it stays at ~base MHz (e.g. 1.9 GHz).
    /// </summary>
    private double? CombineCpuMegahertzPreferred()
    {
        // 1) Processor Information / Processor Frequency (MHz) when present.
        var freq = TryReadProcessorFrequencyCounterMhz();
        if (freq is { } f && double.IsFinite(f) && f >= 400 && f <= 9500)
            return f;

        // 2) % Processor Performance × WMI MaxClockSpeed (tracks turbo vs idle).
        var est = TryReadMhzFromProcessorPerformancePercent();
        if (est is { } e && double.IsFinite(e) && e >= 400 && e <= 9500)
            return e;

        // 3) LibreHardwareMonitor CPU clock sensors.
        if (_lhmCpuClockMhz is { } lh && lh >= 400 && lh <= 9500 && double.IsFinite(lh))
            return lh;

        // 4) Last resort: WMI max turbo cap only (never Win32 CurrentClockSpeed — often stuck at base).
        return TryGetWmiMaxClockMhzCached();
    }

    private void TryAttachProcessorFrequencyCounters()
    {
        if (!OperatingSystem.IsWindows()) return;
        TryAddFreq("Processor Information", "Processor Frequency", "0,_Total");
        TryAddFreq("Processor Information", "Processor Frequency", "0,0");
        TryAddFreq("Processor Information", "Processor Frequency", "_Total");

        try
        {
            if (!PerformanceCounterCategory.Exists("Processor Information")) return;
            var cat = new PerformanceCounterCategory("Processor Information");
            foreach (var name in cat.GetInstanceNames())
                TryAddFreq("Processor Information", "Processor Frequency", name);
        }
        catch { /* ignore */ }
    }

    private void TryAttachProcessorPerformanceCounters()
    {
        if (!OperatingSystem.IsWindows()) return;
        TryAddPct("Processor Information", "% Processor Performance", "0,_Total");
        TryAddPct("Processor Information", "% Processor Performance", "0,0");
        TryAddPct("Processor Information", "% Processor Performance", "_Total");

        try
        {
            if (!PerformanceCounterCategory.Exists("Processor Information")) return;
            var cat = new PerformanceCounterCategory("Processor Information");
            foreach (var name in cat.GetInstanceNames())
                TryAddPct("Processor Information", "% Processor Performance", name);
        }
        catch { /* ignore */ }
    }

    private void TryAddPct(string categoryName, string counterName, string instanceName)
    {
        try
        {
            var c = new PerformanceCounter(categoryName, counterName, instanceName, readOnly: true);
            _ = c.NextValue();
            _pctPerfCounters.Add(c);
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
                // Ignore bogus stuck-at-base readings some stacks surface as ~1900 when idle APIs lie.
                if (double.IsFinite(v) && v >= 400 && v <= 9500)
                    return v;
            }
            catch { /* disposed / access */ }
        }
        return null;
    }

    private double? TryReadMhzFromProcessorPerformancePercent()
    {
        var max = TryGetWmiMaxClockMhzCached();
        if (max is null || max < 400)
            return null;

        double bestPct = -1;
        foreach (var c in _pctPerfCounters)
        {
            try
            {
                var v = (double)c.NextValue();
                if (!double.IsFinite(v)) continue;
                // Typically 0–100; occasionally reported >100 on some builds.
                if (v > bestPct) bestPct = v;
            }
            catch { /* ignore */ }
        }

        if (bestPct < 0)
            return null;

        var pct = Math.Clamp(bestPct, 0.0, 200.0);
        return Math.Clamp(max.Value * (pct / 100.0), 100.0, 9500.0);
    }

    private double? TryGetWmiMaxClockMhzCached()
    {
        var now = DateTime.UtcNow;
        if (now < _nextWmiMaxMhzPoll && _wmiMaxMhz is not null)
            return _wmiMaxMhz;

        _wmiMaxMhz = TryGetWmiMaxClockMhz();
        _nextWmiMaxMhzPoll = now.AddSeconds(60);
        return _wmiMaxMhz;
    }

    /// <summary>Win32 Processor MaxClockSpeed only (MHz). Do not use CurrentClockSpeed for live frequency.</summary>
    private static double? TryGetWmiMaxClockMhz()
    {
        try
        {
            double best = 0;
            using var searcher = new ManagementObjectSearcher(@"SELECT MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject o in searcher.Get())
            {
                var maxRaw = Convert.ToUInt32(o["MaxClockSpeed"], CultureInfo.InvariantCulture);
                if (maxRaw > best) best = maxRaw;
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

    private (double downBps, double upBps) SampleNetworkBytesPerSecond()
    {
        try
        {
            var now = DateTime.UtcNow;
            var ni = PickPrimaryNetworkInterface();
            if (ni is null) return (0, 0);

            var stats = ni.GetIPStatistics();
            var rx = stats.BytesReceived;
            var tx = stats.BytesSent;
            var speedBits = ni.Speed;

            if (_lastNetSampleUtc == DateTime.MinValue)
            {
                _lastNetSampleUtc = now;
                _lastRxBytes = rx;
                _lastTxBytes = tx;
                _lastIfSpeedBits = speedBits;
                return (0, 0);
            }

            var dt = (now - _lastNetSampleUtc).TotalSeconds;
            if (dt <= 0.2) return (0, 0);

            var dRx = Math.Max(0, rx - _lastRxBytes);
            var dTx = Math.Max(0, tx - _lastTxBytes);
            _lastNetSampleUtc = now;
            _lastRxBytes = rx;
            _lastTxBytes = tx;
            _lastIfSpeedBits = speedBits;

            var downBps = dRx / dt;
            var upBps = dTx / dt;
            if (!double.IsFinite(downBps) || downBps < 0) downBps = 0;
            if (!double.IsFinite(upBps) || upBps < 0) upBps = 0;
            return (downBps, upBps);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static NetworkInterface? PickPrimaryNetworkInterface()
    {
        NetworkInterface? best = null;
        long bestSpeed = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (ni.Speed <= 0) continue;

                var ip = ni.GetIPProperties();
                var hasGateway = ip.GatewayAddresses.Any(g => g?.Address is not null && !g.Address.Equals(System.Net.IPAddress.Any));
                if (!hasGateway) continue;

                // Prefer higher link speed.
                if (ni.Speed > bestSpeed)
                {
                    best = ni;
                    bestSpeed = ni.Speed;
                }
            }
            catch { /* ignore interface errors */ }
        }
        return best;
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
        foreach (var p in _pctPerfCounters)
            try { p.Dispose(); } catch { /* ignore */ }
        try { _computer.Close(); } catch { }
    }
}

