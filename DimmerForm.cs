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

    private FlowLayoutPanel _gammaPanel = null!;
    private TrackBar _hardwareSlider = null!;
    private Label _hardwareValue = null!;
    private Label _hardwareStatus = null!;
    private CheckBox _deepDimCheck = null!;
    private TrackBar _deepDimSlider = null!;
    private Label _deepDimValue = null!;
    private readonly CheckBox _startupCheck;
    private readonly Label _statusLabel;
    private readonly Dictionary<string, GammaRow> _gammaRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _pendingGamma = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _hardwareTimer;
    private readonly System.Windows.Forms.Timer _gammaTimer;

    private int? _pendingHardware;
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

        _gammaTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _gammaTimer.Tick += (_, _) => FlushPendingGamma();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 8,
            Padding = new Padding(14),
            BackColor = SystemColors.Window
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var i = 0; i < 8; i++)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        var title = new Label
        {
            Text = "SteadyDim",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 13F),
            Margin = new Padding(0, 0, 0, 2)
        };

        var subtitle = new Label
        {
            Text = "Shared hardware backlight, per-monitor gamma trim, global deep-night layer.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 10)
        };

        var hardwareGroup = BuildHardwareGroup();
        var gammaGroup = BuildGammaGroup();
        var deepGroup = BuildDeepDimGroup();

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
        root.Controls.Add(gammaGroup, 0, 3);
        root.Controls.Add(deepGroup, 0, 4);
        root.Controls.Add(startup, 0, 5);
        root.Controls.Add(_statusLabel, 0, 6);
        root.Controls.Add(footer, 0, 7);
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
            PositionNearTray();
            _controller.SetPanelOpen(true);
        };

        VisibleChanged += (_, _) =>
        {
            if (!Visible)
            {
                _controller.SetPanelOpen(false);
            }
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

        RebuildGammaRows();
        UpdateFromController();
    }

    public bool WasJustHidden =>
        (DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 250;

    public void ShowNearTray()
    {
        if (!Visible)
        {
            UpdateFromController();
            Show();
        }

        Activate();
        BringToFront();
    }

    public void HidePanel()
    {
        FlushPendingHardware();
        FlushPendingGamma();
        _hiddenAt = DateTime.UtcNow;
        Hide();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private GroupBox BuildHardwareGroup()
    {
        var group = new GroupBox
        {
            Text = "Hardware brightness (all monitors)",
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

        _hardwareValue = new Label
        {
            Text = "50%",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 0)
        };

        _hardwareSlider = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 10,
            SmallChange = 1,
            LargeChange = 10,
            Value = Math.Clamp(_controller.Config.SharedHardwareBrightness ?? 50, 0, 100),
            Dock = DockStyle.Top,
            Height = 45,
            Margin = new Padding(0, 0, 0, 0)
        };
        _hardwareSlider.ValueChanged += HardwareSlider_ValueChanged;

        _hardwareStatus = new Label
        {
            Text = "DDC/CI — applied to every active monitor.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(340, 0),
            Font = new Font("Segoe UI", 8F),
            Margin = new Padding(0, 3, 0, 0)
        };

        layout.Controls.Add(_hardwareValue, 0, 0);
        layout.Controls.Add(_hardwareSlider, 0, 1);
        layout.Controls.Add(_hardwareStatus, 0, 2);
        group.Controls.Add(layout);
        return group;
    }

    private GroupBox BuildGammaGroup()
    {
        var group = new GroupBox
        {
            Text = "Per-monitor gamma trim",
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
            RowCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _gammaPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top
        };

        var note = new Label
        {
            Text = "Per-display GPU gamma, 0–50%. SDR only; use the global layer for deeper dimming.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(340, 0),
            Font = new Font("Segoe UI", 8F),
            Margin = new Padding(0, 3, 0, 0)
        };

        layout.Controls.Add(_gammaPanel, 0, 0);
        layout.Controls.Add(note, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private GroupBox BuildDeepDimGroup()
    {
        var group = new GroupBox
        {
            Text = "Deep night dim (all monitors)",
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
            RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var i = 0; i < 4; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        _deepDimCheck = new CheckBox
        {
            Text = "Enabled",
            AutoSize = true,
            Checked = _controller.Config.DeepDimEnabled,
            Margin = new Padding(0, 0, 0, 2)
        };
        _deepDimCheck.CheckedChanged += DeepDimCheck_CheckedChanged;

        _deepDimSlider = new TrackBar
        {
            Minimum = 0,
            Maximum = 90,
            TickFrequency = 10,
            SmallChange = 1,
            LargeChange = 10,
            Value = Math.Clamp(_controller.Config.DeepDimPercent, 0, 90),
            Dock = DockStyle.Top,
            Height = 45,
            Enabled = _controller.Config.DeepDimEnabled,
            Margin = new Padding(0, 0, 0, 0)
        };
        _deepDimSlider.ValueChanged += DeepDimSlider_ValueChanged;

        _deepDimValue = new Label
        {
            Text = FormatDeepDim(_deepDimSlider.Value),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 0)
        };

        var note = new Label
        {
            Text = "Global compositor layer. Pauses while this panel is open; close it to apply.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(340, 0),
            Font = new Font("Segoe UI", 8F),
            Margin = new Padding(0, 3, 0, 0)
        };

        layout.Controls.Add(_deepDimCheck, 0, 0);
        layout.Controls.Add(_deepDimSlider, 0, 1);
        layout.Controls.Add(_deepDimValue, 0, 2);
        layout.Controls.Add(note, 0, 3);
        group.Controls.Add(layout);
        return group;
    }

    private void RebuildGammaRows()
    {
        _updating = true;
        _gammaPanel.SuspendLayout();

        foreach (Control control in _gammaPanel.Controls)
        {
            control.Dispose();
        }

        _gammaPanel.Controls.Clear();
        _gammaRows.Clear();

        var monitors = _controller.GetMonitors();
        if (monitors.Count == 0)
        {
            _gammaPanel.Controls.Add(new Label
            {
                Text = "No displays detected yet. SteadyDim will rescan when Windows reports a display change.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                MaximumSize = new Size(340, 0),
                Margin = new Padding(0, 0, 0, 4)
            });
        }
        else
        {
            foreach (var monitor in monitors)
            {
                var row = new TableLayoutPanel
                {
                    ColumnCount = 2,
                    RowCount = 2,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Top,
                    Width = 340,
                    Margin = new Padding(0, 0, 0, 8)
                };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                row.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                var name = new Label
                {
                    Text = monitor.Name,
                    AutoSize = true,
                    AutoEllipsis = true,
                    MaximumSize = new Size(250, 0),
                    Margin = new Padding(0, 0, 0, 0)
                };

                var value = new Label
                {
                    Text = GammaValueText(monitor),
                    AutoSize = true,
                    TextAlign = ContentAlignment.MiddleRight,
                    ForeColor = monitor.GammaAvailable && string.IsNullOrEmpty(monitor.GammaError)
                        ? SystemColors.WindowText
                        : Color.Firebrick,
                    Margin = new Padding(6, 0, 0, 0)
                };

                var slider = new TrackBar
                {
                    Minimum = 0,
                    Maximum = 50,
                    TickFrequency = 10,
                    SmallChange = 1,
                    LargeChange = 5,
                    Value = Math.Clamp(monitor.GammaTrim, 0, 50),
                    Dock = DockStyle.Top,
                    Height = 42,
                    Enabled = monitor.GammaAvailable,
                    Tag = monitor.Id,
                    Margin = new Padding(0, 0, 0, 0)
                };
                slider.ValueChanged += GammaSlider_ValueChanged;

                row.Controls.Add(name, 0, 0);
                row.Controls.Add(value, 1, 0);
                row.Controls.Add(slider, 0, 1);
                row.SetColumnSpan(slider, 2);
                _gammaPanel.Controls.Add(row);

                _gammaRows[monitor.Id] = new GammaRow(slider, value);
            }
        }

        _gammaPanel.ResumeLayout();
        _updating = false;
    }

    private void UpdateFromController()
    {
        if (IsDisposed)
        {
            return;
        }

        _updating = true;
        try
        {
            var shared = Math.Clamp(_controller.Config.SharedHardwareBrightness ?? 50, 0, 100);
            _hardwareSlider.Value = shared;
            _hardwareValue.Text = $"{shared}%";

            var monitors = _controller.GetMonitors();
            var ddcAvailable = monitors.Count(m => m.DdcAvailable);
            _hardwareStatus.Text = monitors.Count == 0
                ? "DDC/CI — no active monitors yet."
                : $"DDC/CI — {ddcAvailable}/{monitors.Count} monitors available.";

            if (_gammaRows.Count != monitors.Count)
            {
                RebuildGammaRows();
            }

            foreach (var monitor in monitors)
            {
                if (!_gammaRows.TryGetValue(monitor.Id, out var row))
                {
                    continue;
                }

                row.Slider.Value = Math.Clamp(monitor.GammaTrim, 0, 50);
                row.Slider.Enabled = monitor.GammaAvailable;
                row.Value.Text = GammaValueText(monitor);
                row.Value.ForeColor = monitor.GammaAvailable && string.IsNullOrEmpty(monitor.GammaError)
                    ? SystemColors.WindowText
                    : Color.Firebrick;
            }

            _deepDimCheck.Checked = _controller.Config.DeepDimEnabled;
            _deepDimSlider.Value = Math.Clamp(_controller.Config.DeepDimPercent, 0, 90);
            _deepDimSlider.Enabled = _controller.Config.DeepDimEnabled;
            _deepDimValue.Text = FormatDeepDim(_deepDimSlider.Value);
        }
        finally
        {
            _updating = false;
        }
    }

    private void HardwareSlider_ValueChanged(object? sender, EventArgs e)
    {
        _hardwareValue.Text = $"{_hardwareSlider.Value}%";
        if (_updating)
        {
            return;
        }

        _pendingHardware = _hardwareSlider.Value;
        _hardwareTimer.Stop();
        _hardwareTimer.Start();
    }

    private void GammaSlider_ValueChanged(object? sender, EventArgs e)
    {
        if (_updating || sender is not TrackBar slider || slider.Tag is not string id)
        {
            return;
        }

        if (_gammaRows.TryGetValue(id, out var row))
        {
            row.Value.Text = $"{slider.Value}%";
        }

        _pendingGamma[id] = slider.Value;
        _gammaTimer.Stop();
        _gammaTimer.Start();
    }

    private void FlushPendingHardware()
    {
        _hardwareTimer.Stop();
        if (_pendingHardware is not { } value)
        {
            return;
        }

        _pendingHardware = null;
        _controller.SetAllHardwareBrightness(value);
    }

    private void FlushPendingGamma()
    {
        _gammaTimer.Stop();
        if (_pendingGamma.Count == 0)
        {
            return;
        }

        var pending = _pendingGamma.ToArray();
        _pendingGamma.Clear();
        foreach (var pair in pending)
        {
            _controller.SetGammaTrim(pair.Key, pair.Value);
        }
    }

    private void DeepDimCheck_CheckedChanged(object? sender, EventArgs e)
    {
        if (_updating)
        {
            return;
        }

        _deepDimSlider.Enabled = _deepDimCheck.Checked;
        _controller.SetDeepDimEnabled(_deepDimCheck.Checked);
    }

    private void DeepDimSlider_ValueChanged(object? sender, EventArgs e)
    {
        _deepDimValue.Text = FormatDeepDim(_deepDimSlider.Value);
        if (_updating)
        {
            return;
        }

        _controller.SetDeepDimPercent(_deepDimSlider.Value);
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
                    RebuildGammaRows();
                    UpdateFromController();
                }));
            }
            catch
            {
                // Form handle not ready or closing.
            }

            return;
        }

        RebuildGammaRows();
        UpdateFromController();
    }

    private void Controller_StateChanged(object? sender, EventArgs e)
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

        UpdateFromController();
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

    private static string FormatDeepDim(int reduction)
    {
        var output = 100 - reduction;
        return $"{reduction}% reduction ({output}% output)";
    }

    private static string GammaValueText(MonitorState monitor)
    {
        if (!monitor.GammaAvailable)
        {
            return "Gamma unavailable";
        }

        return string.IsNullOrEmpty(monitor.GammaError)
            ? $"{monitor.GammaTrim}%"
            : $"{monitor.GammaTrim}% (rejected)";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _controller.MonitorsChanged -= Controller_MonitorsChanged;
            _controller.StateChanged -= Controller_StateChanged;
            _hardwareTimer.Dispose();
            _gammaTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed record GammaRow(TrackBar Slider, Label Value);
}
