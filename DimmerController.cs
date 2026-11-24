using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
    private readonly System.Threading.Timer _watchdogTimer;

    private List<DisplayInfo> _displays = new();
    private List<MonitorState> _monitors = new();
    private CancellationTokenSource? _pendingRescan;
    private bool _disposed;
    private bool _savePending;
    private bool _magInitialized;
    private DateTime _lastWatchdogRescan = DateTime.MinValue;

    public DimmerController(AppConfig config)
    {
        _config = config;
        _saveTimer = new System.Threading.Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        _watchdogTimer = new System.Threading.Timer(_ => WatchdogTick(), null, 5000, 10000);
    }

    public event EventHandler? MonitorsChanged;
    public event EventHandler? StateChanged;

    public AppConfig Config => _config;
    public int Level => Math.Clamp(_config.Level, 0, 100);

    public static int HardwarePercentForLevel(int level)
    {
        level = Math.Clamp(level, 0, 100);
        return level <= 50 ? 100 - level * 2 : 0;
    }

    public static int SoftwarePercentForLevel(int level)
    {
        level = Math.Clamp(level, 0, 100);
        return level <= 50 ? 0 : (int)Math.Round((level - 50) * 1.8);
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
            await Task.Delay(1500).ConfigureAwait(false);
        }

        await RescanWithRetryAsync(10, 1000).ConfigureAwait(false);

        var snapshot = GetMonitors();
        if (snapshot.Count == 0 || snapshot.Any(m => !m.DdcAvailable))
        {
            Log.Info("Some displays are not DDC-ready yet; scheduling a quick retry.");
            ScheduleRescan("startup DDC retry", 1500);
        }

        ApplyMag(SoftwarePercentForLevel(Level));
        QueueSave();
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

            List<DisplayInfo> oldDisplays;
            lock (_stateGate)
            {
                oldDisplays = _displays;
                _displays = new List<DisplayInfo>();
                _monitors = new List<MonitorState>();
            }

            foreach (var old in oldDisplays)
            {
                old.Dispose();
            }

            var newDisplays = DisplayEnumerator.Enumerate();
            var newStates = new List<MonitorState>();
            var anyDdc = false;
            var configChanged = false;
            var hardware = HardwarePercentForLevel(Level);

            foreach (var display in newDisplays)
            {
                if (!_config.Monitors.TryGetValue(display.Id, out var monitorConfig))
                {
                    monitorConfig = FindByName(display.Name);
                    if (monitorConfig is null)
                    {
                        monitorConfig = new MonitorConfig { Name = display.Name };
                        Log.Info($"New monitor discovered: {display.Name} ({display.Id}).");
                    }

                    _config.Monitors[display.Id] = monitorConfig;
                    configChanged = true;
                }

                if (!string.Equals(monitorConfig.Name, display.Name, StringComparison.Ordinal))
                {
                    monitorConfig.Name = display.Name;
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
                    TargetPercent = hardware,
                    CurrentPercent = PercentFromRaw(display, display.DdcCurrent),
                    Display = display
                };

                Log.Info($"Display state: {display.Name}; id={display.Id}; device={display.DeviceName}; ddc={display.DdcAvailable}; handle=0x{display.DdcHandle.ToInt64():X}.");

                if (display.DdcAvailable)
                {
                    anyDdc = true;
                    var applied = await ApplyDdcCoreAsync(display, hardware).ConfigureAwait(false);
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

            lock (_stateGate)
            {
                _displays = newDisplays;
                _monitors = newStates;
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

    public void SetLevel(int level)
    {
        level = Math.Clamp(level, 0, 100);
        _config.Level = level;

        var hardware = HardwarePercentForLevel(level);
        var software = SoftwarePercentForLevel(level);

        foreach (var monitor in GetMonitors())
        {
            monitor.TargetPercent = hardware;
            monitor.DdcError = null;
        }

        Log.Info($"Level {level}%: hardware {hardware}%, software {software}%.");
        QueueSave();
        StateChanged?.Invoke(this, EventArgs.Empty);
        _ = ApplyLevelAsync(hardware, software);
    }

    private async Task ApplyLevelAsync(int hardware, int software)
    {
        await _ddcGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            var ids = GetMonitors().Select(m => m.Id).ToList();
            foreach (var id in ids)
            {
                var current = FindMonitor(id);
                if (current is null || !current.DdcAvailable)
                {
                    continue;
                }

                var applied = await ApplyDdcCoreAsync(current.Display, hardware).ConfigureAwait(false);
                current.CurrentPercent = PercentFromRaw(current.Display, current.Display.DdcCurrent);
                current.DdcError = applied ? null : "DDC write failed";
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("Apply level failed.", ex);
        }
        finally
        {
            _ddcGate.Release();
        }

        ApplyMag(software);
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
                Log.Info("Magnification API initialized for software dim.");
                return true;
            }

            var error = Marshal.GetLastWin32Error();
            Log.Warn($"MagInitialize failed (Win32 {error}). Software dim is unavailable.");
        }
        catch (Exception ex)
        {
            Log.Warn("MagInitialize threw: " + ex.Message);
        }

        return false;
    }

    private void ApplyMag(int reduction)
    {
        if (_disposed)
        {
            return;
        }

        lock (_magGate)
        {
            if (reduction <= 0)
            {
                if (!EnsureMagInitialized())
                {
                    return;
                }

                var identity = new Native.MAGCOLOREFFECT { transform = new float[25] };
                identity.transform[0] = 1.0f;
                identity.transform[6] = 1.0f;
                identity.transform[12] = 1.0f;
                identity.transform[18] = 1.0f;
                identity.transform[24] = 1.0f;

                if (!Native.MagSetFullscreenColorEffect(ref identity))
                {
                    Log.Warn($"Mag identity reset failed (Win32 {Marshal.GetLastWin32Error()}).");
                }

                return;
            }

            if (!EnsureMagInitialized())
            {
                return;
            }

            var scale = Math.Clamp(1.0f - reduction / 100.0f, 0.10f, 1.0f);
            var effect = new Native.MAGCOLOREFFECT { transform = new float[25] };
            effect.transform[0] = scale;
            effect.transform[6] = scale;
            effect.transform[12] = scale;
            effect.transform[18] = 1.0f;
            effect.transform[24] = 1.0f;

            if (Native.MagSetFullscreenColorEffect(ref effect))
            {
                Log.Info($"Software dim {reduction}% (scale={scale:0.00}).");
            }
            else
            {
                Log.Warn($"MagSetFullscreenColorEffect failed (Win32 {Marshal.GetLastWin32Error()}) at {reduction}% software dim.");
            }
        }
    }

    private static async Task<bool> ApplyDdcCoreAsync(DisplayInfo display, int percent)
    {
        if (!display.DdcAvailable)
        {
            Log.Warn($"DDC unavailable for {display.Name} ({display.DeviceName}).");
            return false;
        }

        var raw = MapPercentToRaw(display, percent);
        if (display.DdcCurrent == raw)
        {
            return true;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Native.SetMonitorBrightness(display.DdcHandle, raw))
                {
                    await Task.Delay(80).ConfigureAwait(false);

                    if (Native.GetMonitorBrightness(
                            display.DdcHandle,
                            out _,
                            out var readback,
                            out _))
                    {
                        display.DdcCurrent = readback;
                        Log.Info($"DDC set {display.Name} ({display.DeviceName}) handle=0x{display.DdcHandle.ToInt64():X}: target={percent}% raw={raw}, readback={readback}.");

                        if (Math.Abs((long)readback - raw) <= 1)
                        {
                            return true;
                        }

                        Log.Warn($"DDC readback mismatch for {display.Name}: wrote {raw}, read {readback}.");
                    }
                    else
                    {
                        display.DdcCurrent = raw;
                        Log.Info($"DDC set {display.Name} ({display.DeviceName}) handle=0x{display.DdcHandle.ToInt64():X}: target={percent}% raw={raw}, readback unavailable.");
                        return true;
                    }
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

        Log.Warn($"SetMonitorBrightness failed for {display.Name} ({display.DeviceName}) at {percent}% (handle=0x{display.DdcHandle.ToInt64():X}).");
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
        SetLevel(0);
    }

    private void WatchdogTick()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var expected = Native.GetSystemMetrics(Native.SM_CMONITORS);
            List<MonitorState> snapshot;
            lock (_stateGate)
            {
                snapshot = _monitors.ToList();
            }

            var missingDdc = snapshot.Any(m => !m.DdcAvailable);
            var countMismatch = expected > 0 && snapshot.Count != expected;
            if (!missingDdc && !countMismatch)
            {
                return;
            }

            if ((DateTime.UtcNow - _lastWatchdogRescan).TotalSeconds < 25)
            {
                return;
            }

            _lastWatchdogRescan = DateTime.UtcNow;
            Log.Info($"Watchdog rescan: expected={expected}, known={snapshot.Count}, missingDdc={missingDdc}.");
            ScheduleRescan("watchdog", 250);
        }
        catch (Exception ex)
        {
            Log.Warn("Watchdog failed: " + ex.Message);
        }
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
            _watchdogTimer.Dispose();
        }
        catch
        {
            // Best effort.
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
