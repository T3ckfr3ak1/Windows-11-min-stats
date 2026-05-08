using System.Drawing.Drawing2D;

namespace TaskbarResourceMonitor;

public sealed class TaskbarWidgetForm : Form
{
    private readonly Metrics _metrics = new();
    private readonly RingBuffer _cpu = new(60);
    private readonly RingBuffer _mem = new(60);
    private readonly SettingsStore _settings = new();
    private string[] _drives = ["C:\\"];
    private int _driveIdx;
    private (string root, double usedPercent)? _disk;
    private int _diskTick;

    private double? _tempC;
    private int _consecutiveTempMisses;
    private bool _showTemp;

    private readonly System.Windows.Forms.Timer _timer;

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

        Width = 220;
        Height = 46;

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
        var (cpu, mem, tempC) = _metrics.Sample();
        _cpu.Add(cpu);
        _mem.Add(mem);

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

        Invalidate();
    }

    public void SetDrives(string[] drives)
    {
        _drives = (drives is { Length: > 0 }) ? drives : ["C:\\"];
        _driveIdx = 0;
        _diskTick = 0;
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

        var pad = 6;
        var inner = Rectangle.Inflate(ClientRectangle, -pad, -6);
        inner.Height = Math.Max(18, inner.Height);

        // Layout row 1: CPU graph left, MEM graph middle, TEMP text right (only if available)
        var row1 = new Rectangle(inner.Left, inner.Top, inner.Width, 22);
        var row2 = new Rectangle(inner.Left, inner.Top + 24, inner.Width, inner.Height - 24);

        var tempW = _showTemp ? 44 : 0;
        var gap = 8;
        var graphW = Math.Max(40, (row1.Width - tempW - gap) / 2);

        var cpuRect = new Rectangle(row1.Left, row1.Top, graphW, row1.Height);
        var memRect = new Rectangle(cpuRect.Right + gap, row1.Top, graphW, row1.Height);
        var tempRect = new Rectangle(memRect.Right + gap, row1.Top, tempW, row1.Height);

        DrawGraph(g, cpuRect, _cpu.Snapshot(), Color.FromArgb(255, 90, 220, 90), "CPU");
        DrawGraph(g, memRect, _mem.Snapshot(), Color.FromArgb(255, 110, 160, 255), "RAM");

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

        DrawDiskBar(g, row2, _disk);

        using var border = new Pen(Color.FromArgb(90, 255, 255, 255));
        g.DrawRectangle(border, 0, 0, ClientRectangle.Width - 1, ClientRectangle.Height - 1);
    }

    private void DrawDiskBar(Graphics g, Rectangle rect, (string root, double usedPercent)? disk)
    {
        using var border = new Pen(Color.FromArgb(90, 255, 255, 255));
        using var labelBrush = new SolidBrush(Color.FromArgb(190, 230, 230, 230));
        using var fillBrush = new SolidBrush(Color.FromArgb(255, 180, 140, 255)); // purple-ish
        using var bgBrush = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
        using var f = new Font("Segoe UI", 7, FontStyle.Regular);

        g.DrawRectangle(border, rect);
        var plot = Rectangle.Inflate(rect, -3, -3);

        if (disk is null)
        {
            g.DrawString("DISK n/a", f, labelBrush, plot.Left, plot.Top - 1);
            return;
        }

        var (root, used) = disk.Value;
        used = Math.Clamp(used, 0, 100);
        var label = $"DISK {root.TrimEnd('\\')}: {used:0}%";
        g.DrawString(label, f, labelBrush, plot.Left, plot.Top - 1);

        var bar = new Rectangle(plot.Left, plot.Top + 10, plot.Width, Math.Max(6, plot.Height - 10));
        g.FillRectangle(bgBrush, bar);
        var w = (int)Math.Round(bar.Width * (used / 100.0));
        if (w > 0)
        {
            g.FillRectangle(fillBrush, new Rectangle(bar.Left, bar.Top, w, bar.Height));
        }
    }

    private void DrawGraph(Graphics g, Rectangle rect, double[] series, Color color, string label)
    {
        using var border = new Pen(Color.FromArgb(90, 255, 255, 255));
        using var linePen = new Pen(color, 2f);
        using var labelBrush = new SolidBrush(Color.FromArgb(170, 230, 230, 230));

        g.DrawRectangle(border, rect);

        var plot = Rectangle.Inflate(rect, -3, -3);
        plot.Height = Math.Max(10, plot.Height);

        // Label (tiny)
        using var f = new Font("Segoe UI", 7, FontStyle.Regular);
        g.DrawString(label, f, labelBrush, plot.Left - 1, plot.Top - 2);

        var n = Math.Min(series.Length, plot.Width);
        if (n <= 1) return;

        var tail = series[^n..];
        PointF[] pts = new PointF[n];

        float YFor(double pct)
        {
            var t = Math.Clamp(pct / 100.0, 0.0, 1.0);
            return (float)(plot.Bottom - (t * plot.Height));
        }

        for (int i = 0; i < n; i++)
        {
            var x = plot.Right - (n - 1 - i);
            pts[i] = new PointF(x, YFor(tail[i]));
        }

        g.DrawLines(linePen, pts);

        // Current value text at bottom-right of plot (small)
        var latest = tail[^1];
        var txt = $"{latest:0}%";
        var sz = g.MeasureString(txt, f);
        g.DrawString(txt, f, labelBrush, plot.Right - sz.Width, plot.Bottom - sz.Height);
    }
}

