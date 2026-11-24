using System;
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

    private TrackBar _levelSlider = null!;
    private Label _levelValue = null!;
    private Label _mappingLabel = null!;
    private Label _hardwareStatus = null!;
    private readonly CheckBox _startupCheck;
    private readonly Label _statusLabel;
    private readonly System.Windows.Forms.Timer _levelTimer;

    private int? _pendingLevel;
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

        _levelTimer = new System.Windows.Forms.Timer { Interval = 120 };
        _levelTimer.Tick += (_, _) => FlushPendingLevel();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(14),
            BackColor = SystemColors.Window
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var i = 0; i < 6; i++)
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
            Text = "Hardware first, then software dim.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 10)
        };

        var levelGroup = BuildLevelGroup();

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
        root.Controls.Add(levelGroup, 0, 2);
        root.Controls.Add(startup, 0, 3);
        root.Controls.Add(_statusLabel, 0, 4);
        root.Controls.Add(footer, 0, 5);
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
            UpdateFromController();
            UpdateLevelLabels();
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

        UpdateLevelLabels();
    }

    private GroupBox BuildLevelGroup()
    {
        var group = new GroupBox
        {
            Text = "Dimming level",
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

        _levelValue = new Label
        {
            Text = "",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 0)
        };

        _levelSlider = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 10,
            SmallChange = 1,
            LargeChange = 5,
            Value = Math.Clamp(_controller.Level, 0, 100),
            Dock = DockStyle.Top,
            Height = 45,
            Margin = new Padding(0, 0, 0, 0)
        };
        _levelSlider.ValueChanged += LevelSlider_ValueChanged;

        _mappingLabel = new Label
        {
            Text = "0–50: hardware 100→0%.  50–100: software 0→90%.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(340, 0),
            Font = new Font("Segoe UI", 8F),
            Margin = new Padding(0, 3, 0, 0)
        };

        _hardwareStatus = new Label
        {
            Text = "",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 3, 0, 0)
        };

        layout.Controls.Add(_levelValue, 0, 0);
        layout.Controls.Add(_levelSlider, 0, 1);
        layout.Controls.Add(_mappingLabel, 0, 2);
        layout.Controls.Add(_hardwareStatus, 0, 3);
        group.Controls.Add(layout);
        return group;
    }

    private void LevelSlider_ValueChanged(object? sender, EventArgs e)
    {
        UpdateLevelLabels();

        if (_updating)
        {
            return;
        }

        _pendingLevel = _levelSlider.Value;
        _levelTimer.Stop();
        _levelTimer.Start();
    }

    private void FlushPendingLevel()
    {
        _levelTimer.Stop();
        if (_pendingLevel is not { } value)
        {
            return;
        }

        _pendingLevel = null;
        _controller.SetLevel(value);
    }

    private void UpdateLevelLabels()
    {
        var level = Math.Clamp(_levelSlider.Value, 0, 100);
        var hardware = DimmerController.HardwarePercentForLevel(level);
        var software = DimmerController.SoftwarePercentForLevel(level);

        _levelValue.Text = software > 0
            ? $"{level}% — hardware {hardware}%, software {software}%"
            : $"{level}% — hardware {hardware}%";

        var monitors = _controller.GetMonitors();
        var ddcCount = monitors.Count(m => m.DdcAvailable);
        _hardwareStatus.Text = monitors.Count == 0
            ? "No displays detected yet."
            : $"DDC/CI — {ddcCount}/{monitors.Count} monitors available.";
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
            _levelSlider.Value = Math.Clamp(_controller.Level, 0, 100);
            UpdateLevelLabels();

            var monitors = _controller.GetMonitors();
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
            UpdateFromController();
            Show();
        }

        Activate();
        BringToFront();
    }

    public void HidePanel()
    {
        FlushPendingLevel();
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
            _levelTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
