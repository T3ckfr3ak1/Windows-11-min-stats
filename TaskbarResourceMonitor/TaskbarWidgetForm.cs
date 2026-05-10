using System.Drawing.Drawing2D;

namespace TaskbarResourceMonitor;

public sealed class TaskbarWidgetForm : Form
{
    /// <summary>Width for CPU+RAM + one rotating disk graph (client area).</summary>
    private const int BaseClientWidth = 404;

    /// <summary>Extra client width per drive beyond the first; applied to the disk column (not split across CPU/RAM/NET).</summary>
    private const int ClientWidthExtraPerAdditionalDrive = 52;

    private const int WidgetHeight = 60;

    private readonly Metrics _metrics = new();
    private readonly RingBuffer _cpu = new(60);
    private readonly RingBuffer _mem = new(60);
    // 10 minute history @ 1 sample/sec
    private readonly RingBuffer _netDown = new(600);
    private readonly RingBuffer _netUp = new(600);
    private RingBuffer[] _diskBuffers = [];
    private readonly SettingsStore _settings = new();
    private string[] _drives = ["C:\\"];

    private static readonly Color[] DiskLineColors =
    [
        Color.FromArgb(255, 185, 130, 255),
        Color.FromArgb(255, 90, 220, 220),
        Color.FromArgb(255, 255, 200, 90),
        Color.FromArgb(255, 140, 220, 120),
        Color.FromArgb(255, 255, 140, 200),
    ];

    private double? _tempC;
    private int _consecutiveTempMisses;
    private bool _showTemp;

    private double? _cpuClockMhz;

    private readonly System.Windows.Forms.Timer _timer;

    private static readonly Color BorderLine = Color.FromArgb(90, 255, 255, 255);
    private static readonly Color LabelBrush = Color.FromArgb(170, 230, 230, 230);

    public TaskbarWidgetForm()
    {
        _settings.Load();
        _drives = (_settings.Drives is { Length: > 0 }) ? _settings.Drives : ["C:\\"];

        Text = "Resource Monitor";
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.None;
        // Default: tray-driven widget should not create a taskbar button.
        ShowInTaskbar = false;
        TopMost = _settings.AlwaysOnTop;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        MinimizeBox = false;
        MaximizeBox = false;

        BackColor = Color.FromArgb(18, 18, 18);
        ForeColor = Color.Gainsboro;

        ClientSize = new Size(PreferredClientWidthForDriveCount(_drives.Length), WidgetHeight);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => SampleAndRedraw();
        _timer.Start();

        Shown += (_, _) =>
        {
            PositionNearTaskbar();
        };
        FormClosed += (_, _) =>
        {
            _timer.Stop();
            _metrics.Dispose();
        };

        Resize += (_, _) => PositionNearTaskbar();
        LocationChanged += (_, _) => PositionNearTaskbar();

        // Right click menu.
        var menu = new ContextMenuStrip();
        var storageMenu = new ToolStripMenuItem("Storage drives");
        var alwaysOnTop = new ToolStripMenuItem("Always on Top")
        {
            CheckOnClick = true,
            Checked = TopMost
        };
        alwaysOnTop.CheckedChanged += (_, _) =>
        {
            TopMost = alwaysOnTop.Checked;
            _settings.AlwaysOnTop = TopMost;
            _settings.Save();
        };
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => Close();
        menu.Items.Add(storageMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(alwaysOnTop);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);
        ContextMenuStrip = menu;

        BuildStorageMenu(storageMenu);
        RebuildDiskBuffers();
    }

    private void BuildStorageMenu(ToolStripMenuItem storageMenu)
    {
        storageMenu.DropDownItems.Clear();

        var available = new HashSet<string>(
            Storage.AvailableDriveRoots().Select(r => r.TrimEnd('\\') + "\\"),
            StringComparer.OrdinalIgnoreCase);

        // Default to C:\ if nothing selected
        if (_settings.Drives.Length == 0)
        {
            _settings.Drives = ["C:\\"];
            _settings.Save();
        }

        foreach (var root in available.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            var item = new ToolStripMenuItem(root)
            {
                CheckOnClick = true,
                Checked = _settings.Drives.Any(d => string.Equals(Norm(d), root, StringComparison.OrdinalIgnoreCase))
            };

            item.CheckedChanged += (_, _) =>
            {
                var selected = _settings.Drives.Select(Norm).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (item.Checked) selected.Add(root);
                else selected.Remove(root);
                if (selected.Count == 0) selected.Add("C:\\");

                _settings.Drives = selected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                _settings.Save();

                SetDrives(_settings.Drives);
            };

            storageMenu.DropDownItems.Add(item);
        }

        static string Norm(string d) => (d ?? "").Trim().TrimEnd('\\') + "\\";
    }

    private static int PreferredClientWidthForDriveCount(int selectedDriveCount)
    {
        var n = Math.Max(1, selectedDriveCount);
        return BaseClientWidth + (n - 1) * ClientWidthExtraPerAdditionalDrive;
    }

    /// <summary>CPU/RAM/graphs stay readable; widen when more drives are monitored (rotation).</summary>
    private void ApplyPreferredWidthFromDriveSelection()
    {
        var w = PreferredClientWidthForDriveCount(_drives.Length);
        if (ClientSize.Width != w || ClientSize.Height != WidgetHeight)
            ClientSize = new Size(w, WidgetHeight);
    }

    private void PositionNearTaskbar()
    {
        if (!IsHandleCreated) return;
        var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        var work = Screen.PrimaryScreen?.WorkingArea ?? screen;

        // Assume taskbar is on bottom (most common). Flush to the right edge of the working area (= screen edge when the taskbar spans the bottom).
        var taskbarHeight = Math.Max(0, screen.Height - work.Height);
        var x = Math.Max(work.Left, work.Right - Width);
        var y = screen.Bottom - taskbarHeight - Height;
        Location = new Point(Math.Max(work.Left, x), Math.Max(work.Top, y));
    }

    private void SampleAndRedraw()
    {
        var (cpu, mem, tempC, cpuMhz, netDownBps, netUpBps) = _metrics.Sample();
        _cpu.Add(cpu);
        _mem.Add(mem);
        _netDown.Add(netDownBps);
        _netUp.Add(netUpBps);
        _cpuClockMhz = cpuMhz;

        // Probe temps continually, but only display when reliably present.
        if (tempC is { } t)
        {
            _tempC = t;
            _consecutiveTempMisses = 0;
            _showTemp = true;
        }
        else
        {
            _consecutiveTempMisses++;
            // Hide after a few misses so we don't show stale/phantom values.
            if (_consecutiveTempMisses >= 5)
            {
                _tempC = null;
                _showTemp = false;
            }
        }

        EnsureDiskBuffers();
        if (_drives.Length == 0) _drives = ["C:\\"];

        for (var i = 0; i < _drives.Length; i++)
        {
            var usage = Storage.TryGetUsage(_drives[i]);
            _diskBuffers[i].Add(usage is { } u ? u.usedPercent : 0);
        }

        Invalidate();
    }

    public void SetDrives(string[] drives)
    {
        _drives = (drives is { Length: > 0 }) ? drives : ["C:\\"];
        RebuildDiskBuffers();
        ApplyPreferredWidthFromDriveSelection();
        PositionNearTaskbar();
        Invalidate();
    }

    private void EnsureDiskBuffers()
    {
        if (_diskBuffers.Length == _drives.Length) return;
        RebuildDiskBuffers();
    }

    private void RebuildDiskBuffers()
    {
        var n = Math.Max(1, _drives.Length);
        _diskBuffers = new RingBuffer[n];
        for (var i = 0; i < n; i++)
            _diskBuffers[i] = new RingBuffer(60);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        using var bg = new SolidBrush(BackColor);
        g.FillRectangle(bg, ClientRectangle);

        var pad = 8;
        var inner = Rectangle.Inflate(ClientRectangle, -pad, -pad);

        var tempW = _showTemp ? 44 : 0;
        const int tempPad = 4;
        var stripW = inner.Width - tempW - (tempW > 0 ? tempPad : 0);
        // Extra width for multiple drives is split into separate disk columns (one graph per drive).
        var driveCount = Math.Max(1, _drives.Length);
        var diskBump = (driveCount - 1) * ClientWidthExtraPerAdditionalDrive;
        const int minTripleCol = 46;
        var diskTotalW = stripW / 4 + diskBump;
        var maxDisk = Math.Max(stripW / 4, stripW - 3 * minTripleCol);
        diskTotalW = Math.Clamp(diskTotalW, stripW / 4, maxDisk);
        // Storage graphs use half the previous disk strip width each (remainder goes to CPU/RAM/NET).
        const int minDiskColPx = 28;
        var nDrive = Math.Max(1, driveCount);
        diskTotalW = Math.Max(minDiskColPx * nDrive, diskTotalW / 2);
        var rest = stripW - diskTotalW;
        var w1 = rest / 3;
        var w2 = rest / 3;
        var w3 = rest - w1 - w2;

        var cpuRect = new Rectangle(inner.Left, inner.Top, w1, inner.Height);
        var memRect = new Rectangle(cpuRect.Right, inner.Top, w2, inner.Height);
        var netRect = new Rectangle(memRect.Right, inner.Top, w3, inner.Height);

        var cpuSpeed = FormatCpuSpeed(_cpuClockMhz);
        DrawGraph(g, cpuRect, _cpu.Snapshot(), Color.FromArgb(255, 90, 220, 90), "CPU", cpuSpeed);
        DrawGraph(g, memRect, _mem.Snapshot(), Color.FromArgb(255, 110, 160, 255), "RAM");
        DrawDualGraph(g, netRect, _netDown.Snapshot(), _netUp.Snapshot(), "NET", "DL", "UL");

        EnsureDiskBuffers();
        var roots = _drives.Length > 0 ? _drives : ["C:\\"];
        var nDisk = roots.Length;
        var dwBase = diskTotalW / nDisk;
        var dwRem = diskTotalW % nDisk;
        var xDisk = netRect.Right;
        for (var di = 0; di < nDisk; di++)
        {
            var colW = dwBase + (di < dwRem ? 1 : 0);
            var diskColRect = new Rectangle(xDisk, inner.Top, colW, inner.Height);
            DrawDiskDriveColumn(g, diskColRect, di);
            xDisk += colW;
        }

        var tempRect = new Rectangle(xDisk + (tempW > 0 ? tempPad : 0), inner.Top, tempW, inner.Height);

        if (_showTemp && _tempC is { } t)
        {
            var s = $"{t:0}°";
            using var f = new Font("Segoe UI", 10, FontStyle.Bold);
            var sz = g.MeasureString(s, f);
            var x = tempRect.Left + (tempRect.Width - sz.Width) / 2;
            var y = tempRect.Top + (tempRect.Height - sz.Height) / 2 - 1;
            using var br = new SolidBrush(ForeColor);
            g.DrawString(s, f, br, x, y);
        }

        // Dividers & outer rim last so waveform can run flush to edges under them.
        using (var rule = new Pen(BorderLine))
        {
            if (memRect.Left > inner.Left)
                g.DrawLine(rule, memRect.Left, inner.Top, memRect.Left, inner.Bottom - 1);
            if (netRect.Left > memRect.Left)
                g.DrawLine(rule, netRect.Left, inner.Top, netRect.Left, inner.Bottom - 1);
            // NET | first disk column
            if (netRect.Right > netRect.Left)
                g.DrawLine(rule, netRect.Right, inner.Top, netRect.Right, inner.Bottom - 1);
            // Between disk columns
            var xRule = netRect.Right;
            for (var di = 0; di < nDisk; di++)
            {
                var colW = dwBase + (di < dwRem ? 1 : 0);
                xRule += colW;
                if (di < nDisk - 1)
                    g.DrawLine(rule, xRule, inner.Top, xRule, inner.Bottom - 1);
            }
            if (tempW > 0 && tempRect.Left > netRect.Right)
                g.DrawLine(rule, tempRect.Left - 1, inner.Top, tempRect.Left - 1, inner.Bottom - 1);
        }

        using var outer = new Pen(BorderLine);
        g.DrawRectangle(outer, inner.Left, inner.Top, inner.Width - 1, inner.Height - 1);
    }

    private static string? FormatCpuSpeed(double? mhz)
    {
        if (mhz is not { } m || !double.IsFinite(m) || m < 150)
            return null;
        if (m >= 1000)
            return $"{m / 1000.0:0.00} GHz";
        return $"{m:0} MHz";
    }

    /// <summary>Small shadow so overlays stay readable across the waveform.</summary>
    private static void DrawShadowString(Graphics g, string text, Font font, Brush fill, float x, float y)
    {
        using var sh = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
        g.DrawString(text, font, sh, x + 1, y + 1);
        g.DrawString(text, font, fill, x, y);
    }

    private void DrawDiskDriveColumn(Graphics g, Rectangle rect, int driveIndex)
    {
        EnsureDiskBuffers();
        if (driveIndex < 0 || driveIndex >= _drives.Length || driveIndex >= _diskBuffers.Length)
            return;

        var root = _drives[driveIndex];
        var label = root.TrimEnd('\\');
        var color = DiskLineColors[driveIndex % DiskLineColors.Length];

        if (Storage.TryGetUsage(root) is null)
        {
            DrawGraphUnavailable(g, rect, label);
            return;
        }

        DrawGraph(g, rect, _diskBuffers[driveIndex].Snapshot(), color, label);
    }

    private void DrawGraphUnavailable(Graphics g, Rectangle cell, string label)
    {
        using var labelBr = new SolidBrush(LabelBrush);
        using var muted = new SolidBrush(Color.FromArgb(120, 200, 200, 200));
        using var f = new Font("Segoe UI", 7, FontStyle.Regular);
        var state = g.Save();
        try
        {
            g.SetClip(cell);
            var na = "n/a";
            var szNa = g.MeasureString(na, f);
            DrawShadowString(g, label, f, labelBr, cell.Left + 2, cell.Top + 2);
            DrawShadowString(g, na, f, muted, cell.Right - szNa.Width - 2, cell.Bottom - szNa.Height - 2);
        }
        finally
        {
            g.Restore(state);
        }
    }

    private void DrawGraph(Graphics g, Rectangle cell, double[] series, Color color, string label, string? subtitle = null)
    {
        using var linePen = new Pen(color, 2f);
        using var labelBr = new SolidBrush(LabelBrush);
        using var f = new Font("Segoe UI", 7, FontStyle.Regular);
        using var fSub = new Font("Segoe UI", 6.25f, FontStyle.Regular);

        var state = g.Save();
        try
        {
            // Clip exactly to column; polyline spans the full interior (no inset) so it meets the bordered box.
            g.SetClip(cell);

            float plotLeft = cell.Left;
            float plotRight = cell.Right - 1f;
            float plotTop = cell.Top;
            float plotBottom = cell.Bottom - 1f;
            float plotH = Math.Max(1f, plotBottom - plotTop);

            var n = Math.Min(series.Length, Math.Max(2, cell.Width));
            if (n >= 2)
            {
                var tail = series[^n..];
                var pts = new PointF[n];

                float YFor(double pct)
                {
                    var t = Math.Clamp(pct / 100.0, 0.0, 1.0);
                    return plotBottom - (float)(t * plotH);
                }

                float span = Math.Max(0f, plotRight - plotLeft);
                for (var i = 0; i < n; i++)
                {
                    var x = n <= 1 ? plotLeft : plotLeft + span * i / (n - 1);
                    pts[i] = new PointF(x, YFor(tail[i]));
                }

                g.DrawLines(linePen, pts);

                var latest = tail[^1];
                var txt = $"{latest:0}%";
                var szPct = g.MeasureString(txt, f);
                DrawShadowString(g, txt, f, labelBr, cell.Right - szPct.Width - 2f, cell.Bottom - szPct.Height - 2f);
            }

            DrawShadowString(g, label, f, labelBr, cell.Left + 2f, cell.Top + 2f);
            if (subtitle is not null)
                DrawShadowString(g, subtitle, fSub, labelBr, cell.Left + 2f, cell.Top + 2f + f.Height - 1f);
        }
        finally
        {
            g.Restore(state);
        }
    }

    private void DrawDualGraph(
        Graphics g,
        Rectangle cell,
        double[] seriesA,
        double[] seriesB,
        string label,
        string aTag,
        string bTag)
    {
        var aColor = Color.FromArgb(255, 90, 200, 255); // down
        var bColor = Color.FromArgb(255, 255, 160, 90); // up
        using var penA = new Pen(aColor, 2f);
        using var penB = new Pen(bColor, 2f);
        using var labelBr = new SolidBrush(LabelBrush);
        using var f = new Font("Segoe UI", 7, FontStyle.Regular);
        using var fSub = new Font("Segoe UI", 6.25f, FontStyle.Regular);

        var state = g.Save();
        try
        {
            g.SetClip(cell);

            float plotLeft = cell.Left;
            float plotRight = cell.Right - 1f;
            float plotTop = cell.Top;
            float plotBottom = cell.Bottom - 1f;
            float plotH = Math.Max(1f, plotBottom - plotTop);
            float span = Math.Max(0f, plotRight - plotLeft);

            // Scale to peak of last 10 minutes (max of either series).
            var peak = 0.0;
            for (var i = 0; i < seriesA.Length; i++) peak = Math.Max(peak, seriesA[i]);
            for (var i = 0; i < seriesB.Length; i++) peak = Math.Max(peak, seriesB[i]);
            if (!double.IsFinite(peak) || peak <= 1) peak = 1;

            static PointF[] BuildPts(double[] series, int n, float left, float span, float bottom, float h, double peak)
            {
                var tail = series[^n..];
                var pts = new PointF[n];
                for (var i = 0; i < n; i++)
                {
                    var x = n <= 1 ? left : left + span * i / (n - 1);
                    var t = Math.Clamp(tail[i] / peak, 0.0, 1.0);
                    var y = bottom - (float)(t * h);
                    pts[i] = new PointF(x, y);
                }
                return pts;
            }

            var nA = Math.Min(seriesA.Length, Math.Max(2, cell.Width));
            if (nA >= 2)
                g.DrawLines(penA, BuildPts(seriesA, nA, plotLeft, span, plotBottom, plotH, peak));

            var nB = Math.Min(seriesB.Length, Math.Max(2, cell.Width));
            if (nB >= 2)
                g.DrawLines(penB, BuildPts(seriesB, nB, plotLeft, span, plotBottom, plotH, peak));

            DrawShadowString(g, label, f, labelBr, cell.Left + 2f, cell.Top + 2f);
            DrawShadowString(g, $"{aTag}/{bTag}", fSub, labelBr, cell.Left + 2f, cell.Top + 2f + f.Height - 1f);

            var latestA = seriesA.Length > 0 ? seriesA[^1] : 0;
            var latestB = seriesB.Length > 0 ? seriesB[^1] : 0;
            static string Fmt(double bps)
            {
                if (!double.IsFinite(bps) || bps < 0) bps = 0;
                var mbps = (bps * 8.0) / 1_000_000.0;
                return mbps >= 100 ? $"{mbps:0}M" : $"{mbps:0.0}M";
            }
            var txt = $"{Fmt(latestA)}/{Fmt(latestB)}";
            var sz = g.MeasureString(txt, f);
            DrawShadowString(g, txt, f, labelBr, cell.Right - sz.Width - 2f, cell.Bottom - sz.Height - 2f);
        }
        finally
        {
            g.Restore(state);
        }
    }
}
