using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SteadyDim;

internal sealed class TrayApp : ApplicationContext
{
    private readonly AppOptions _options;
    private readonly AppConfig _config;
    private readonly DimmerController _controller;
    private readonly MessageWindow _messages;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _startupItem;
    private readonly Control _dispatcher;
    private readonly Icon _icon;
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle _showWait;

    private DimmerForm? _form;
    private bool _exiting;

    public TrayApp(AppOptions options, AppConfig config)
    {
        _options = options;
        _config = config;
        _controller = new DimmerController(config);

        _dispatcher = new Control();
        _dispatcher.CreateControl();

        _messages = new MessageWindow();
        _messages.ShowRequested += () => Post(ShowForm);
        _messages.DisplayChanged += () =>
        {
            Log.Info("WM_DISPLAYCHANGE received.");
            _controller.ScheduleRescan("display change", 600);
        };
        _messages.DeviceChanged += () =>
        {
            Log.Info("WM_DEVICECHANGE received.");
            _controller.ScheduleRescan("device change", 900);
        };
        _messages.PowerChanged += () =>
        {
            Log.Info("Power/display notification received.");
            _controller.ScheduleRescan("power state", 1600);
        };
        _messages.SessionUnlocked += () =>
        {
            Log.Info("Session unlocked.");
            _controller.ScheduleRescan("session unlock", 800);
        };
        _messages.TaskbarCreated += () => Post(RestoreTrayIcon);

        _icon = IconFactory.CreateTrayIcon();
        _menu = BuildMenu(out _startupItem);
        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "SteadyDim",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _tray.MouseClick += Tray_MouseClick;
        _tray.DoubleClick += (_, _) => ToggleForm();

        _controller.MonitorsChanged += (_, _) => Post(UpdateMenu);
        _controller.StateChanged += (_, _) => Post(UpdateMenu);

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, AppInfo.ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    _messages.PostShow();
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        UpdateMenu();
        Log.Info($"Tray app starting (startup={options.Startup}, noStartup={options.NoStartup}).");

        if (config.StartWithWindows && !options.NoStartup)
        {
            _ = Task.Run(() =>
            {
                var result = StartupTask.Ensure(true);
                Log.Info("Startup task check: " + result.Message);

                if (!result.Success)
                {
                    Post(() => _tray.ShowBalloonTip(
                        8000,
                        AppInfo.Name,
                        "Could not register startup. Add the SteadyDim folder to your security software exclusions, or launch it via Start SteadyDim.vbs.",
                        ToolTipIcon.Warning));
                }
            });
        }

        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            await _controller.StartAsync(_options.Startup);
            Post(() =>
            {
                UpdateMenu();
                if (_config.IsNew)
                {
                    ShowForm();
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed.", ex);
            Post(() => _tray.ShowBalloonTip(5000, AppInfo.Name, "Startup failed. See the log for details.", ToolTipIcon.Error));
        }
    }

    private ContextMenuStrip BuildMenu(out ToolStripMenuItem startupItem)
    {
        var menu = new ContextMenuStrip();

        var open = new ToolStripMenuItem("Open SteadyDim");
        open.Font = new Font(open.Font, FontStyle.Bold);
        open.Click += (_, _) => ShowForm();

        startupItem = new ToolStripMenuItem("Start at logon")
        {
            CheckOnClick = false
        };
        startupItem.Click += (_, _) => ToggleStartup();

        var rescan = new ToolStripMenuItem("Rescan displays");
        rescan.Click += (_, _) => _controller.ScheduleRescan("manual tray rescan", 0);

        var reset = new ToolStripMenuItem("Reset to full brightness");
        reset.Click += (_, _) =>
        {
            _controller.ResetToFull();
            UpdateMenu();
        };

        var log = new ToolStripMenuItem("Open log");
        log.Click += (_, _) => OpenLog();

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitApp();

        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startupItem);
        menu.Items.Add(rescan);
        menu.Items.Add(reset);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(log);
        menu.Items.Add(exit);
        return menu;
    }

    private void Tray_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ToggleForm();
        }
    }

    private void ToggleForm()
    {
        if (_exiting)
        {
            return;
        }

        if (_form is { Visible: true })
        {
            _form.HidePanel();
            return;
        }

        if (_form is { WasJustHidden: true })
        {
            return;
        }

        ShowForm();
    }

    private void ShowForm()
    {
        if (_exiting)
        {
            return;
        }

        if (_form is null || _form.IsDisposed)
        {
            _form = new DimmerForm(_controller, ExitApp, OpenLog);
        }

        _form.ShowNearTray();
    }

    private void ToggleStartup()
    {
        var enabled = !_config.StartWithWindows;
        _config.StartWithWindows = enabled;
        _controller.QueueSave();
        UpdateMenu();

        _ = Task.Run(() =>
        {
            var result = StartupTask.Ensure(enabled);
            Log.Info("Startup toggle: " + result.Message);
            Post(() =>
            {
                if (!result.Success)
                {
                    _config.StartWithWindows = false;
                    _controller.QueueSave();
                    _tray.ShowBalloonTip(5000, AppInfo.Name, result.Message, ToolTipIcon.Warning);
                }

                UpdateMenu();
            });
        });
    }

    private void UpdateMenu()
    {
        _startupItem.Checked = _config.StartWithWindows;
        var text = $"SteadyDim — {Math.Clamp(_config.Level, 0, 100)}%";
        _tray.Text = text.Length <= 63 ? text : text[..63];
    }

    private void RestoreTrayIcon()
    {
        try
        {
            _tray.Visible = false;
            _tray.Visible = true;
            UpdateMenu();
            Log.Info("Tray icon restored after TaskbarCreated.");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not restore tray icon: " + ex.Message);
        }
    }

    private void OpenLog()
    {
        try
        {
            if (!File.Exists(AppInfo.LogPath))
            {
                Log.Info("Log opened before any messages were written.");
            }

            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppInfo.LogPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open log: " + ex.Message);
        }
    }

    private void Post(Action action)
    {
        if (_exiting || _dispatcher.IsDisposed)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(action);
        }
        catch
        {
            // The message loop may be shutting down.
        }
    }

    private void ExitApp()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        Log.Info("Exit requested.");

        try
        {
            _tray.Visible = false;
        }
        catch
        {
            // Best effort.
        }

        try
        {
            _form?.CloseForExit();
            _form?.Dispose();
            _form = null;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not close form: " + ex.Message);
        }

        try
        {
            _controller.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Controller dispose failed: " + ex.Message);
        }

        try
        {
            _showWait.Unregister(null);
            _showEvent.Dispose();
            _messages.Dispose();
            _tray.Dispose();
            _menu.Dispose();
            _icon.Dispose();
            _dispatcher.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Tray cleanup failed: " + ex.Message);
        }

        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_exiting)
        {
            ExitApp();
        }

        base.Dispose(disposing);
    }
}
