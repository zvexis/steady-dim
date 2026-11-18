using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SteadyDim;

internal sealed class MonitorState
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public bool DdcAvailable { get; init; }
    public uint DdcMinimum { get; init; }
    public uint DdcMaximum { get; init; }
    public int TargetPercent { get; set; }
    public int CurrentPercent { get; set; }
    public string? DdcError { get; set; }
    internal DisplayInfo Display { get; init; } = null!;
}

internal sealed class DimmerController : IDisposable
{
    private readonly object _stateGate = new();
    private readonly object _magGate = new();
    private readonly SemaphoreSlim _ddcGate = new(1, 1);
    private readonly AppConfig _config;
    private readonly System.Threading.Timer _saveTimer;
    private readonly System.Threading.Timer _magRetryTimer;

    private List<DisplayInfo> _displays = new();
    private List<MonitorState> _monitors = new();
    private CancellationTokenSource? _pendingRescan;
    private bool _disposed;
    private bool _magInitialized;
    private bool _panelOpen;
    private bool _savePending;
    private float? _lastMagScale;
    private int _magRetryAttempts;

    public DimmerController(AppConfig config)
    {
        _config = config;
        _saveTimer = new System.Threading.Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        _magRetryTimer = new System.Threading.Timer(
            _ =>
            {
                if (!_disposed)
                {
                    ApplyMag();
                }
            },
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public event EventHandler? MonitorsChanged;
    public event EventHandler? StateChanged;

    public AppConfig Config => _config;
    public bool DeepDimAvailable { get; private set; } = true;
    public bool MagInitialized
    {
        get
        {
            lock (_magGate)
            {
                return _magInitialized;
            }
        }
    }

    public IReadOnlyList<MonitorState> GetMonitors()
    {
        lock (_stateGate)
        {
            return _monitors.ToArray();
        }
    }

    public MonitorState? FindMonitor(string id)
    {
        lock (_stateGate)
        {
            return _monitors.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task StartAsync(bool startup)
    {
        if (startup)
        {
            // Task Scheduler already delays 10s; this is a second cushion for the
            // shell/display stack, which is still settling right after logon.
            await Task.Delay(1500).ConfigureAwait(false);
        }

        await RescanWithRetryAsync(10, 1000).ConfigureAwait(false);
        ApplyMag();
    }

    public void ScheduleRescan(string reason, int delayMilliseconds = 750)
    {
        if (_disposed)
        {
            return;
        }

        CancellationToken token;
        lock (_stateGate)
        {
            _pendingRescan?.Cancel();
            _pendingRescan?.Dispose();
            _pendingRescan = new CancellationTokenSource();
            token = _pendingRescan.Token;
        }

        Log.Info($"Scheduling rescan ({reason}) in {delayMilliseconds} ms.");
        _ = RescanAfterDelayAsync(reason, delayMilliseconds, token);
    }

    private async Task RescanAfterDelayAsync(string reason, int delayMilliseconds, CancellationToken token)
    {
        try
        {
            await Task.Delay(delayMilliseconds, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || _disposed)
            {
                return;
            }

            Log.Info("Rescan triggered: " + reason);
            await RescanWithRetryAsync(4, 700).ConfigureAwait(false);
            ApplyMag();
        }
        catch (OperationCanceledException)
        {
            // A newer display event superseded this one.
        }
        catch (Exception ex)
        {
            Log.Error("Rescan failed: " + reason, ex);
        }
    }

    public async Task<bool> RescanWithRetryAsync(int attempts, int delayMilliseconds)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (_disposed)
            {
                return false;
            }

            try
            {
                var anyDdc = await RescanAsync().ConfigureAwait(false);
                if (anyDdc)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Rescan attempt failed.", ex);
            }

            if (attempt + 1 < attempts)
            {
                await Task.Delay(delayMilliseconds).ConfigureAwait(false);
            }
        }

        return false;
    }

    private async Task<bool> RescanAsync()
    {
        await _ddcGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return false;
            }

            var newDisplays = DisplayEnumerator.Enumerate();
            var newStates = new List<MonitorState>();
            var anyDdc = false;
            var configChanged = false;

            foreach (var display in newDisplays)
            {
                if (!_config.Monitors.TryGetValue(display.Id, out var monitorConfig))
                {
                    monitorConfig = FindByName(display.Name);
                    if (monitorConfig is null)
                    {
                        var defaultBrightness = _config.DeepDimEnabled
                            ? _config.NewMonitorBrightness
                            : PercentFromRaw(display, display.DdcCurrent);

                        monitorConfig = new MonitorConfig
                        {
                            Name = display.Name,
                            Brightness = defaultBrightness
                        };

                        Log.Info($"New monitor discovered: {display.Name} ({display.Id}); default DDC {defaultBrightness}%.");
                    }

                    _config.Monitors[display.Id] = monitorConfig;
                    configChanged = true;
                }

                var state = new MonitorState
                {
                    Id = display.Id,
                    Name = display.Name,
                    DeviceName = display.DeviceName,
                    DdcAvailable = display.DdcAvailable,
                    DdcMinimum = display.DdcMinimum,
                    DdcMaximum = display.DdcMaximum,
                    TargetPercent = Math.Clamp(monitorConfig.Brightness, 0, 100),
                    CurrentPercent = PercentFromRaw(display, display.DdcCurrent),
                    Display = display
                };

                if (display.DdcAvailable)
                {
                    anyDdc = true;
                    var applied = await ApplyDdcCoreAsync(display, state.TargetPercent).ConfigureAwait(false);
                    if (!applied)
                    {
                        state.DdcError = "DDC write failed";
                    }

                    state.CurrentPercent = PercentFromRaw(display, display.DdcCurrent);
                }
                else
                {
                    state.DdcError = "DDC unavailable";
                }

                newStates.Add(state);
            }

            List<DisplayInfo> oldDisplays;
            lock (_stateGate)
            {
                oldDisplays = _displays;
                _displays = newDisplays;
                _monitors = newStates;
            }

            foreach (var old in oldDisplays)
            {
                old.Dispose();
            }

            if (configChanged)
            {
                QueueSave();
            }

            Log.Info($"Rescan complete: {newStates.Count} display(s), DDC available on {newStates.Count(s => s.DdcAvailable)}.");
            MonitorsChanged?.Invoke(this, EventArgs.Empty);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return anyDdc;
        }
        finally
        {
            _ddcGate.Release();
        }
    }

    private MonitorConfig? FindByName(string name)
    {
        foreach (var pair in _config.Monitors)
        {
            if (string.Equals(pair.Value.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    public void SetBrightness(string id, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        var state = FindMonitor(id);
        if (state is null)
        {
            return;
        }

        state.TargetPercent = percent;
        state.DdcError = null;
        _config.Monitors[id] = new MonitorConfig
        {
            Name = state.Name,
            Brightness = percent
        };

        QueueSave();
        _ = ApplyBrightnessForStateAsync(state, percent);
    }

    private async Task ApplyBrightnessForStateAsync(MonitorState state, int percent)
    {
        await _ddcGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            var current = FindMonitor(state.Id);
            if (current is null)
            {
                return;
            }

            var applied = await ApplyDdcCoreAsync(current.Display, current.TargetPercent).ConfigureAwait(false);
            current.CurrentPercent = PercentFromRaw(current.Display, current.Display.DdcCurrent);
            current.DdcError = applied ? null : "DDC write failed";
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            state.DdcError = "DDC write failed";
            Log.Error("Set brightness failed for " + state.Name, ex);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _ddcGate.Release();
        }
    }

    private static async Task<bool> ApplyDdcCoreAsync(DisplayInfo display, int percent)
    {
        if (!display.DdcAvailable || display.DdcHandle == IntPtr.Zero)
        {
            return false;
        }

        var raw = MapPercentToRaw(display, percent);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Native.SetMonitorBrightness(display.DdcHandle, raw))
                {
                    display.DdcCurrent = raw;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("SetMonitorBrightness threw: " + ex.Message);
            }

            if (attempt < 2)
            {
                await Task.Delay(150).ConfigureAwait(false);
            }
        }

        Log.Warn($"SetMonitorBrightness failed for {display.Name} ({display.DeviceName}) at {percent}%.");
        return false;
    }

    private static uint MapPercentToRaw(DisplayInfo display, int percent)
    {
        if (display.DdcMaximum <= display.DdcMinimum)
        {
            return display.DdcMinimum;
        }

        var range = display.DdcMaximum - display.DdcMinimum;
        return display.DdcMinimum + (uint)Math.Round(range * (percent / 100.0));
    }

    private static int PercentFromRaw(DisplayInfo display, uint raw)
    {
        if (display.DdcMaximum <= display.DdcMinimum)
        {
            return 0;
        }

        var range = display.DdcMaximum - display.DdcMinimum;
        var percent = (int)Math.Round((raw - display.DdcMinimum) * 100.0 / range);
        return Math.Clamp(percent, 0, 100);
    }

    public void ResetToFull()
    {
        var monitors = GetMonitors();
        _config.DeepDimEnabled = false;
        _config.DeepDimPercent = 80;

        foreach (var monitor in monitors)
        {
            monitor.TargetPercent = 100;
            monitor.DdcError = null;
            _config.Monitors[monitor.Id] = new MonitorConfig
            {
                Name = monitor.Name,
                Brightness = 100
            };

            _ = ApplyBrightnessForStateAsync(monitor, 100);
        }

        QueueSave();
        ApplyMag();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetDeepDimEnabled(bool enabled)
    {
        _config.DeepDimEnabled = enabled;
        QueueSave();
        ApplyMag();
    }

    public void SetDeepDimPercent(int percent)
    {
        _config.DeepDimPercent = Math.Clamp(percent, 0, 90);
        QueueSave();
        ApplyMag();
    }

    public void SetPanelOpen(bool open)
    {
        _panelOpen = open;
        if (_config.PauseDeepDimWhilePanelOpen)
        {
            ApplyMag();
        }
    }

    public void ApplyMag()
    {
        if (_disposed)
        {
            return;
        }

        lock (_magGate)
        {
            if (_disposed)
            {
                return;
            }

            var shouldDim = _config.DeepDimEnabled &&
                            _config.DeepDimPercent > 0 &&
                            !(_panelOpen && _config.PauseDeepDimWhilePanelOpen);

            if (!shouldDim)
            {
                if (_magInitialized)
                {
                    SetMagEffect(1.0f);
                }

                return;
            }

            if (!EnsureMagInitialized())
            {
                return;
            }

            var scale = (float)(1.0 - _config.DeepDimPercent / 100.0);
            SetMagEffect(scale);
        }
    }

    private bool EnsureMagInitialized()
    {
        if (_magInitialized)
        {
            return true;
        }

        try
        {
            if (Native.MagInitialize())
            {
                _magInitialized = true;
                DeepDimAvailable = true;
                Log.Info("Magnification API initialized.");
                return true;
            }

            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            DeepDimAvailable = false;
            Log.Error($"MagInitialize failed (Win32 {error}). Deep dim is unavailable.");
        }
        catch (Exception ex)
        {
            DeepDimAvailable = false;
            Log.Error("MagInitialize threw.", ex);
        }

        return false;
    }

    private bool SetMagEffect(float scale)
    {
        scale = Math.Clamp(scale, 0.05f, 1.0f);

        // Reapplying an unchanged effect right after a display/DDC change can
        // return ERROR_NOT_READY even though the existing effect is still live.
        // Treat the cached scale as authoritative unless the user changes it.
        if (_lastMagScale is { } last && Math.Abs(last - scale) < 0.001f)
        {
            _magRetryAttempts = 0;
            return true;
        }

        var effect = new Native.MAGCOLOREFFECT
        {
            transform = new float[25]
        };

        effect.transform[0] = scale;
        effect.transform[6] = scale;
        effect.transform[12] = scale;
        effect.transform[18] = 1.0f;
        effect.transform[24] = 1.0f;

        try
        {
            if (Native.MagSetFullscreenColorEffect(ref effect))
            {
                _lastMagScale = scale;
                _magRetryAttempts = 0;
                Log.Info($"Mag effect scale={scale:0.00} ({(1 - scale) * 100:0}% reduction).");
                return true;
            }

            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            _lastMagScale = null;
            Log.Warn($"MagSetFullscreenColorEffect failed (Win32 {error}) at scale {scale:0.00}.");

            if (!_disposed && _magRetryAttempts < 3)
            {
                _magRetryAttempts++;
                _magRetryTimer.Change(1000, Timeout.Infinite);
                Log.Info($"Scheduling Mag retry {_magRetryAttempts}/3.");
            }
        }
        catch (Exception ex)
        {
            _lastMagScale = null;
            Log.Warn("MagSetFullscreenColorEffect threw: " + ex.Message);
        }

        return false;
    }

    public void QueueSave()
    {
        lock (_stateGate)
        {
            _savePending = true;
        }

        _saveTimer.Change(500, Timeout.Infinite);
    }

    public void SaveNow()
    {
        lock (_stateGate)
        {
            if (!_savePending)
            {
                return;
            }

            _savePending = false;
        }

        ConfigStore.Save(_config);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _pendingRescan?.Cancel();
            _pendingRescan?.Dispose();
            _saveTimer.Dispose();
            _magRetryTimer.Dispose();
        }
        catch
        {
            // Best effort.
        }

        lock (_magGate)
        {
            if (_magInitialized)
            {
                SetMagEffect(1.0f);
                try
                {
                    Native.MagUninitialize();
                }
                catch (Exception ex)
                {
                    Log.Warn("MagUninitialize failed: " + ex.Message);
                }

                _magInitialized = false;
            }
        }

        lock (_stateGate)
        {
            foreach (var display in _displays)
            {
                display.Dispose();
            }

            _displays.Clear();
            _monitors.Clear();
        }

        ConfigStore.Save(_config);
        _ddcGate.Dispose();
        Log.Info("DimmerController disposed.");
    }
}
