using System.Drawing.Drawing2D;

namespace TaskbarResourceMonitor;

public sealed class TaskbarWidgetForm : Form
{
    /// <summary>Width for CPU+RAM + one rotating disk graph (client area).</summary>
    private const int BaseClientWidth = 326;

    private const int ClientWidthExtraPerAdditionalDrive = 44;

    private const int WidgetHeight = 60;

    private readonly Metrics _metrics = new();
    private readonly RingBuffer _cpu = new(60);
    private readonly RingBuffer _mem = new(60);
    private readonly RingBuffer _diskSamples = new(60);
    private readonly SettingsStore _settings = new();
    private string[] _drives = ["C:\\"];
    private int _driveIdx;
    private (string root, double usedPercent)? _disk;
    private int _diskTick;

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

        // Exit on middle click for convenience; right click menu too.
        var menu = new ContextMenuStrip();
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
        menu.Items.Add(alwaysOnTop);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);
        ContextMenuStrip = menu;
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

        // Assume taskbar is on bottom (most common). We'll sit on top of it, aligned right.
        var taskbarHeight = Math.Max(0, screen.Height - work.Height);
        var x = work.Right - Width - 8;
        var y = screen.Bottom - taskbarHeight - Height;
        Location = new Point(Math.Max(work.Left, x), Math.Max(work.Top, y));
    }

    private void SampleAndRedraw()
    {
        var (cpu, mem, tempC, cpuMhz) = _metrics.Sample();
        _cpu.Add(cpu);
        _mem.Add(mem);
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

        // Disk usage: cycle through selected drives every ~4 seconds
        _diskTick++;
        if (_diskTick % 4 == 0)
        {
            if (_drives.Length == 0) _drives = ["C:\\"];
            _driveIdx = (_driveIdx + 1) % _drives.Length;
        }
        var root = _drives.Length > 0 ? _drives[_driveIdx % _drives.Length] : "C:\\";
        var usage = Storage.TryGetUsage(root);
        _disk = usage is { } u ? (root, u.usedPercent) : null;
        _diskSamples.Add(_disk is { } d ? d.usedPercent : 0);

        Invalidate();
    }

    public void SetDrives(string[] drives)
    {
        _drives = (drives is { Length: > 0 }) ? drives : ["C:\\"];
        _driveIdx = 0;
        _diskTick = 0;
        ApplyPreferredWidthFromDriveSelection();
        Invalidate();
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
        var w1 = stripW / 3;
        var w2 = stripW / 3;
        var w3 = stripW - w1 - w2;

        var cpuRect = new Rectangle(inner.Left, inner.Top, w1, inner.Height);
        var memRect = new Rectangle(cpuRect.Right, inner.Top, w2, inner.Height);
        var diskRect = new Rectangle(memRect.Right, inner.Top, w3, inner.Height);
        var tempRect = new Rectangle(diskRect.Right + (tempW > 0 ? tempPad : 0), inner.Top, tempW, inner.Height);

        var cpuSpeed = FormatCpuSpeed(_cpuClockMhz);
        DrawGraph(g, cpuRect, _cpu.Snapshot(), Color.FromArgb(255, 90, 220, 90), "CPU", cpuSpeed);
        DrawGraph(g, memRect, _mem.Snapshot(), Color.FromArgb(255, 110, 160, 255), "RAM");
        DrawDiskGraph(g, diskRect, _diskSamples.Snapshot(), _disk);

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

        using (var rule = new Pen(BorderLine))
        {
            // Shared vertical edges only (no inner boxes around each graph).
            if (memRect.Left > inner.Left)
                g.DrawLine(rule, memRect.Left, inner.Top, memRect.Left, inner.Bottom);
            if (diskRect.Left > memRect.Left)
                g.DrawLine(rule, diskRect.Left, inner.Top, diskRect.Left, inner.Bottom);
            if (tempW > 0 && tempRect.Left > diskRect.Right)
                g.DrawLine(rule, tempRect.Left - 1, inner.Top, tempRect.Left - 1, inner.Bottom);
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

    /// <summary>Third mini graph matching CPU/RAM; label shows rotating drive letter.</summary>
    private void DrawDiskGraph(
        Graphics g,
        Rectangle rect,
        double[] series,
        (string root, double usedPercent)? disk)
    {
        var color = Color.FromArgb(255, 185, 130, 255);
        string label = disk is { } d
            ? $"DSK {d.root.TrimEnd('\\')}" // e.g. DSK C: or DSK D:
            : "DSK";

        if (disk is null)
        {
            DrawGraphUnavailable(g, rect, label);
            return;
        }

        DrawGraph(g, rect, series, color, label);
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
            g.SetClip(cell);

            // Waveform uses the full column; text is overlaid after so the graph fills the box.
            var plot = new Rectangle(cell.Left + 1, cell.Top + 1, Math.Max(1, cell.Width - 2), Math.Max(1, cell.Height - 2));
            var n = Math.Min(series.Length, Math.Max(2, plot.Width));
            if (n >= 2)
            {
                var tail = series[^n..];
                var pts = new PointF[n];

                float YFor(double pct)
                {
                    var t = Math.Clamp(pct / 100.0, 0.0, 1.0);
                    return plot.Bottom - (float)(t * plot.Height);
                }

                for (var i = 0; i < n; i++)
                {
                    var x = plot.Left + (plot.Width - 1) * (float)i / (n - 1);
                    pts[i] = new PointF(x, YFor(tail[i]));
                }

                g.DrawLines(linePen, pts);

                var latest = tail[^1];
                var txt = $"{latest:0}%";
                var szPct = g.MeasureString(txt, f);
                DrawShadowString(g, txt, f, labelBr, cell.Right - szPct.Width - 2, cell.Bottom - szPct.Height - 2);
            }

            DrawShadowString(g, label, f, labelBr, cell.Left + 2, cell.Top + 2);
            if (subtitle is not null)
                DrawShadowString(g, subtitle, fSub, labelBr, cell.Left + 2, cell.Top + 2 + f.Height - 1);
        }
        finally
        {
            g.Restore(state);
        }
    }
}
