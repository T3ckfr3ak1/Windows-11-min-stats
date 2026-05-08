namespace TaskbarResourceMonitor;

internal sealed class TrayContext : ApplicationContext
{
    private readonly SettingsStore _settings = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _showWidget;
    private readonly ToolStripMenuItem _alwaysOnTop;
    private readonly ToolStripMenuItem _storageMenu;

    private TaskbarWidgetForm? _widget;

    public TrayContext()
    {
        _settings.Load();

        _showWidget = new ToolStripMenuItem("Show widget");
        _alwaysOnTop = new ToolStripMenuItem("Always on Top") { CheckOnClick = true, Checked = _settings.AlwaysOnTop };
        _storageMenu = new ToolStripMenuItem("Storage drives");
        var exit = new ToolStripMenuItem("Exit");

        _showWidget.Click += (_, _) => ToggleWidget();
        _alwaysOnTop.CheckedChanged += (_, _) =>
        {
            _settings.AlwaysOnTop = _alwaysOnTop.Checked;
            _settings.Save();
            if (_widget is not null && !_widget.IsDisposed)
            {
                _widget.TopMost = _alwaysOnTop.Checked;
            }
        };
        exit.Click += (_, _) => Exit();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_showWidget);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_storageMenu);
        menu.Items.Add(_alwaysOnTop);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        _tray = new NotifyIcon
        {
            Visible = true,
            Text = "Taskbar Resource Monitor",
            ContextMenuStrip = menu,
            Icon = AppIcon.Load()
        };

        _tray.DoubleClick += (_, _) => ToggleWidget();

        BuildStorageMenu();
        var f = EnsureWidgetCreated();
        f.Show();

        UpdateMenuState();
    }

    private TaskbarWidgetForm EnsureWidgetCreated()
    {
        if (_widget is not null && !_widget.IsDisposed)
            return _widget;

        _widget = new TaskbarWidgetForm
        {
            ShowInTaskbar = false,
            TopMost = _settings.AlwaysOnTop,
        };
        _widget.SetDrives(_settings.Drives);
        _widget.FormClosed += OnWidgetClosed;
        return _widget;
    }

    private void OnWidgetClosed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(_widget, sender))
            _widget = null;
        UpdateMenuState();
    }

    private void BuildStorageMenu()
    {
        _storageMenu.DropDownItems.Clear();

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

                if (_widget is not null && !_widget.IsDisposed)
                {
                    _widget.SetDrives(_settings.Drives);
                }
            };
            _storageMenu.DropDownItems.Add(item);
        }

        static string Norm(string d) => (d ?? "").Trim().TrimEnd('\\') + "\\";
    }

    private void ToggleWidget()
    {
        var f = EnsureWidgetCreated();
        if (f.Visible)
            f.Hide();
        else
        {
            f.Show();
            f.Activate();
        }

        UpdateMenuState();
    }

    private void UpdateMenuState()
    {
        var visible = _widget is not null && !_widget.IsDisposed && _widget.Visible;
        _showWidget.Text = visible ? "Hide widget" : "Show widget";
    }

    private void Exit()
    {
        try
        {
            if (_widget is not null && !_widget.IsDisposed)
                _widget.Close();
        }
        catch { /* ignore */ }

        _tray.Visible = false;
        _tray.Dispose();

        ExitThread();
    }
}
