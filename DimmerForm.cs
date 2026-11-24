using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SteadyDim;

internal sealed class DimmerForm : Form
{
    private readonly DimmerController _controller;
    private readonly Action _exitAction;
    private readonly Action _showLogAction;

    private FlowLayoutPanel _hardwarePanel = null!;
    private TrackBar _masterSlider = null!;
    private Label _masterValue = null!;
    private TrackBar _softwareSlider = null!;
    private Label _softwareValue = null!;
    private Label _hardwareStatus = null!;
    private readonly CheckBox _startupCheck;
    private readonly Label _statusLabel;
    private readonly Dictionary<string, HardwareRow> _hardwareRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _pendingHardware = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _hardwareTimer;
    private readonly System.Windows.Forms.Timer _softwareTimer;

    private int? _pendingMaster;
    private int? _pendingSoftware;
    private bool _updating;
    private bool _allowClose;
    private DateTime _shownAt;
    private DateTime _hiddenAt;

    public DimmerForm(DimmerController controller, Action exitAction, Action showLogAction)
    {
        _controller = controller;
        _exitAction = exitAction;
        _showLogAction = showLogAction;

        Text = AppInfo.Name;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(390, 0);
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.WindowText;
        Font = new Font("Segoe UI", 9F);
        KeyPreview = true;
        DoubleBuffered = true;
        Padding = new Padding(1);

        _hardwareTimer = new System.Windows.Forms.Timer { Interval = 140 };
        _hardwareTimer.Tick += (_, _) => FlushPendingHardware();

        _softwareTimer = new System.Windows.Forms.Timer { Interval = 120 };
        _softwareTimer.Tick += (_, _) => FlushPendingSoftware();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(14),
            BackColor = SystemColors.Window
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var i = 0; i < 7; i++)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        var title = new Label
        {
            Text = AppInfo.Name,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 11F),
            Margin = new Padding(0, 0, 0, 2)
        };

        var subtitle = new Label
        {
            Text = "Per-monitor hardware brightness, plus global software dim.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 10)
        };

        var hardwareGroup = BuildHardwareGroup();
        var softwareGroup = BuildSoftwareGroup();

        var startup = new CheckBox
        {
            Text = "Start at logon (scheduled task, 10 s delay)",
            AutoSize = true,
            Checked = _controller.Config.StartWithWindows,
            Margin = new Padding(0, 10, 0, 4)
        };
        startup.CheckedChanged += StartupCheck_CheckedChanged;
        _startupCheck = startup;

        _statusLabel = new Label
        {
            Text = "",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(360, 0),
            Margin = new Padding(0, 2, 0, 4)
        };

        var footer = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 4, 0, 0)
        };

        var exit = new LinkLabel
        {
            Text = "Exit",
            AutoSize = true,
            Margin = new Padding(18, 0, 0, 0)
        };
        exit.LinkClicked += (_, _) => _exitAction();

        var log = new LinkLabel
        {
            Text = "Log",
            AutoSize = true,
            Margin = new Padding(12, 0, 0, 0)
        };
        log.LinkClicked += (_, _) => _showLogAction();

        footer.Controls.Add(exit);
        footer.Controls.Add(log);

        root.Controls.Add(title, 0, 0);
        root.Controls.Add(subtitle, 0, 1);
        root.Controls.Add(hardwareGroup, 0, 2);
        root.Controls.Add(softwareGroup, 0, 3);
        root.Controls.Add(startup, 0, 4);
        root.Controls.Add(_statusLabel, 0, 5);
        root.Controls.Add(footer, 0, 6);
        Controls.Add(root);

        Paint += (_, e) => e.Graphics.DrawRectangle(SystemPens.ActiveBorder, 0, 0, Width - 1, Height - 1);
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                HidePanel();
                e.Handled = true;
            }
        };

        Deactivate += (_, _) =>
        {
            if ((DateTime.UtcNow - _shownAt).TotalMilliseconds < 250)
            {
                return;
            }

            HidePanel();
        };

        Shown += (_, _) =>
        {
            _shownAt = DateTime.UtcNow;
            RebuildHardwareRows();
            UpdateFromController();
            PositionNearTray();
        };

        FormClosing += (_, e) =>
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                HidePanel();
            }
        };

        _controller.MonitorsChanged += Controller_MonitorsChanged;
        _controller.StateChanged += Controller_StateChanged;

        RebuildHardwareRows();
        UpdateFromController();
    }

    private GroupBox BuildHardwareGroup()
    {
        var group = new GroupBox
        {
            Text = "Hardware brightness (DDC/CI)",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 8)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 5
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var i = 0; i < 5; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        _masterValue = new Label
        {
            Text = $"All monitors: {_controller.Config.HardwareMaster}%",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 0)
        };

        _masterSlider = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 10,
            SmallChange = 1,
            LargeChange = 5,
            Value = Math.Clamp(_controller.Config.HardwareMaster, 0, 100),
            Dock = DockStyle.Top,
            Height = 45,
            Margin = new Padding(0, 0, 0, 0)
        };
        _masterSlider.ValueChanged += MasterSlider_ValueChanged;

        _hardwarePanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top
        };

        _hardwareStatus = new Label
        {
            Text = "",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 3, 0, 0)
        };

        var note = new Label
        {
            Text = "Master pushes all three; individual sliders fine-tune.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(340, 0),
            Font = new Font("Segoe UI", 8F),
            Margin = new Padding(0, 3, 0, 0)
        };

        layout.Controls.Add(_masterValue, 0, 0);
        layout.Controls.Add(_masterSlider, 0, 1);
        layout.Controls.Add(_hardwarePanel, 0, 2);
        layout.Controls.Add(_hardwareStatus, 0, 3);
        layout.Controls.Add(note, 0, 4);
        group.Controls.Add(layout);
        return group;
    }

    private GroupBox BuildSoftwareGroup()
    {
        var group = new GroupBox
        {
            Text = "Software dim (all monitors)",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 8)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var i = 0; i < 3; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        _softwareValue = new Label
        {
            Text = $"{_controller.Config.SoftwarePercent}% reduction",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 0)
        };

        _softwareSlider = new TrackBar
        {
            Minimum = 0,
            Maximum = 90,
            TickFrequency = 10,
            SmallChange = 1,
            LargeChange = 5,
            Value = Math.Clamp(_controller.Config.SoftwarePercent, 0, 90),
            Dock = DockStyle.Top,
            Height = 45,
            Margin = new Padding(0, 0, 0, 0)
        };
        _softwareSlider.ValueChanged += SoftwareSlider_ValueChanged;

        var note = new Label
        {
            Text = "Global compositor layer. Deep dim, but shared by all monitors.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(340, 0),
            Font = new Font("Segoe UI", 8F),
            Margin = new Padding(0, 3, 0, 0)
        };

        layout.Controls.Add(_softwareValue, 0, 0);
        layout.Controls.Add(_softwareSlider, 0, 1);
        layout.Controls.Add(note, 0, 2);
        group.Controls.Add(layout);
        return group;
    }

    private void RebuildHardwareRows()
    {
        _updating = true;
        _hardwarePanel.SuspendLayout();

        foreach (Control control in _hardwarePanel.Controls)
        {
            control.Dispose();
        }

        _hardwarePanel.Controls.Clear();
        _hardwareRows.Clear();

        var monitors = _controller.GetMonitors();
        if (monitors.Count == 0)
        {
            _hardwarePanel.Controls.Add(new Label
            {
                Text = "No displays detected yet. SteadyDim will rescan when Windows reports a display change.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                MaximumSize = new Size(310, 0),
                Margin = new Padding(0, 6, 0, 6)
            });
        }
        else
        {
            foreach (var monitor in monitors)
            {
                var row = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Margin = new Padding(0, 4, 0, 4)
                };

                var header = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Margin = new Padding(0, 0, 0, 0)
                };

                var name = new Label
                {
                    Text = monitor.Name,
                    AutoSize = true,
                    Margin = new Padding(0, 0, 8, 0)
                };

                var value = new Label
                {
                    Text = $"{monitor.TargetPercent}%",
                    AutoSize = true,
                    Margin = new Padding(0, 0, 0, 0)
                };

                var slider = new TrackBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    TickFrequency = 10,
                    SmallChange = 1,
                    LargeChange = 5,
                    Value = Math.Clamp(monitor.TargetPercent, 0, 100),
                    Width = 207,
                    Height = 45,
                    Tag = monitor.Id,
                    Enabled = monitor.DdcAvailable,
                    Margin = new Padding(0, 0, 0, 0)
                };
                slider.ValueChanged += HardwareSlider_ValueChanged;

                header.Controls.Add(name);
                header.Controls.Add(value);
                row.Controls.Add(header);
                row.Controls.Add(slider);
                _hardwarePanel.Controls.Add(row);
                _hardwareRows[monitor.Id] = new HardwareRow(slider, value);
            }
        }

        _hardwarePanel.ResumeLayout();
        _updating = false;
    }

    private void MasterSlider_ValueChanged(object? sender, EventArgs e)
    {
        _masterValue.Text = $"All monitors: {_masterSlider.Value}%";
        if (_updating)
        {
            return;
        }

        var previous = _updating;
        _updating = true;
        foreach (var row in _hardwareRows.Values)
        {
            row.Slider.Value = _masterSlider.Value;
            row.Value.Text = $"{_masterSlider.Value}%";
        }
        _updating = previous;

        _pendingMaster = _masterSlider.Value;
        _hardwareTimer.Stop();
        _hardwareTimer.Start();
    }

    private void HardwareSlider_ValueChanged(object? sender, EventArgs e)
    {
        if (_updating || sender is not TrackBar slider || slider.Tag is not string id)
        {
            return;
        }

        if (_hardwareRows.TryGetValue(id, out var row))
        {
            row.Value.Text = $"{slider.Value}%";
        }

        _pendingHardware[id] = slider.Value;
        _hardwareTimer.Stop();
        _hardwareTimer.Start();
    }

    private void SoftwareSlider_ValueChanged(object? sender, EventArgs e)
    {
        _softwareValue.Text = $"{_softwareSlider.Value}% reduction";
        if (_updating)
        {
            return;
        }

        _pendingSoftware = _softwareSlider.Value;
        _softwareTimer.Stop();
        _softwareTimer.Start();
    }

    private void FlushPendingHardware()
    {
        _hardwareTimer.Stop();

        if (_pendingMaster is { } master)
        {
            _pendingMaster = null;
            _pendingHardware.Clear();
            _controller.SetAllHardwareBrightness(master);
            return;
        }

        if (_pendingHardware.Count == 0)
        {
            return;
        }

        var pending = _pendingHardware.ToArray();
        _pendingHardware.Clear();
        foreach (var pair in pending)
        {
            _controller.SetHardwareBrightness(pair.Key, pair.Value);
        }
    }

    private void FlushPendingSoftware()
    {
        _softwareTimer.Stop();
        if (_pendingSoftware is not { } value)
        {
            return;
        }

        _pendingSoftware = null;
        _controller.SetSoftwareDim(value);
    }

    private void UpdateFromController()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(UpdateFromController));
            }
            catch
            {
                // Form handle not ready or closing.
            }

            return;
        }

        _updating = true;
        try
        {
            _masterSlider.Value = Math.Clamp(_controller.Config.HardwareMaster, 0, 100);
            _masterValue.Text = $"All monitors: {_masterSlider.Value}%";

            foreach (var monitor in _controller.GetMonitors())
            {
                if (!_hardwareRows.TryGetValue(monitor.Id, out var row))
                {
                    continue;
                }

                row.Slider.Value = Math.Clamp(monitor.TargetPercent, 0, 100);
                row.Slider.Enabled = monitor.DdcAvailable;
                row.Value.Text = string.IsNullOrEmpty(monitor.DdcError)
                    ? $"{monitor.TargetPercent}%"
                    : $"{monitor.TargetPercent}% ({monitor.DdcError})";
                row.Value.ForeColor = string.IsNullOrEmpty(monitor.DdcError)
                    ? SystemColors.WindowText
                    : Color.Firebrick;
            }

            _softwareSlider.Value = Math.Clamp(_controller.Config.SoftwarePercent, 0, 90);
            _softwareValue.Text = $"{_softwareSlider.Value}% reduction";

            var monitors = _controller.GetMonitors();
            var ddcCount = monitors.Count(m => m.DdcAvailable);
            _hardwareStatus.Text = monitors.Count == 0
                ? "No displays detected yet."
                : $"DDC/CI — {ddcCount}/{monitors.Count} monitors available.";
            _hardwareStatus.ForeColor = ddcCount == monitors.Count
                ? SystemColors.GrayText
                : Color.Firebrick;

            var unavailable = monitors.Where(m => !m.DdcAvailable).Select(m => m.Name).ToList();
            if (unavailable.Count > 0)
            {
                SetStatus("DDC unavailable: " + string.Join(", ", unavailable));
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private void StartupCheck_CheckedChanged(object? sender, EventArgs e)
    {
        if (_updating)
        {
            return;
        }

        var enabled = _startupCheck.Checked;
        _controller.Config.StartWithWindows = enabled;
        _controller.QueueSave();
        SetStatus(enabled ? "Registering startup task..." : "Removing startup task...");

        _ = ApplyStartupChangeAsync(enabled);
    }

    private async Task ApplyStartupChangeAsync(bool enabled)
    {
        try
        {
            var result = await Task.Run(() => StartupTask.Ensure(enabled));
            if (IsDisposed)
            {
                return;
            }

            SetStatus(result.Message);
            if (!result.Success)
            {
                _updating = true;
                _startupCheck.Checked = false;
                _controller.Config.StartWithWindows = false;
                _controller.QueueSave();
                _updating = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Startup toggle failed.", ex);
            if (!IsDisposed)
            {
                SetStatus("Startup update failed: " + ex.Message);
            }
        }
    }

    private void SetStatus(string text)
    {
        _statusLabel.Text = text;
    }

    private void Controller_MonitorsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    RebuildHardwareRows();
                    UpdateFromController();
                }));
            }
            catch
            {
                // Form handle not ready or closing.
            }

            return;
        }

        RebuildHardwareRows();
        UpdateFromController();
    }

    private void Controller_StateChanged(object? sender, EventArgs e)
    {
        UpdateFromController();
    }

    public bool WasJustHidden =>
        (DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 250;

    public void ShowNearTray()
    {
        if (!Visible)
        {
            RebuildHardwareRows();
            UpdateFromController();
            Show();
        }

        Activate();
        BringToFront();
    }

    public void HidePanel()
    {
        FlushPendingHardware();
        FlushPendingSoftware();
        _hiddenAt = DateTime.UtcNow;
        Hide();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void PositionNearTray()
    {
        try
        {
            var screen = Screen.FromPoint(Cursor.Position);
            var area = screen.WorkingArea;
            var x = Math.Max(area.Left + 8, area.Right - Width - 12);
            var y = Math.Max(area.Top + 8, area.Bottom - Height - 12);
            Location = new Point(x, y);
        }
        catch
        {
            // Keep the default location.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _controller.MonitorsChanged -= Controller_MonitorsChanged;
            _controller.StateChanged -= Controller_StateChanged;
            _hardwareTimer.Dispose();
            _softwareTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed record HardwareRow(TrackBar Slider, Label Value);
}
