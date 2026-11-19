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
    public int GammaTrim { get; set; }
    public bool GammaAvailable { get; init; }
    public string? DdcError { get; set; }
    public string? GammaError { get; set; }
    internal DisplayInfo Display { get; init; } = null!;
}

internal sealed class DimmerController : IDisposable
{
    private readonly object _stateGate = new();
    private readonly object _magGate = new();
    private readonly SemaphoreSlim _ddcGate = new(1, 1);
    private readonly SemaphoreSlim _gammaGate = new(1, 1);
    private readonly AppConfig _config;
    private readonly System.Threading.Timer _saveTimer;
    private readonly System.Threading.Timer _magRetryTimer;
    private readonly System.Threading.Timer _watchdogTimer;

    private List<DisplayInfo> _displays = new();
    private List<MonitorState> _monitors = new();
    private CancellationTokenSource? _pendingRescan;
    private bool _disposed;
    private bool _magInitialized;
    private bool _panelOpen;
    private bool _savePending;
    private float? _lastMagScale;
    private int _magRetryAttempts;
    private string _topologySignature = "";
    private DateTime _lastWatchdogRescan = DateTime.MinValue;

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

        _watchdogTimer = new System.Threading.Timer(
            _ => WatchdogTick(),
            null,
            5000,
            10000);
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

    public event EventHandler? MonitorsChanged;
    public event EventHandler? StateChanged;
    public event EventHandler? TopologyChanged;

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

        var snapshot = GetMonitors();
        if (snapshot.Count == 0 || snapshot.Any(m => !m.DdcAvailable))
        {
            Log.Info("Some displays are not DDC-ready yet; scheduling a quick retry.");
            ScheduleRescan("startup DDC retry", 1500);
        }

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

            // Restore gamma and release DDC handles before creating new display
            // contexts, so a new gamma baseline is not captured while dimmed.
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
            var shared = Math.Clamp(_config.SharedHardwareBrightness ?? 50, 0, 100);

            foreach (var display in newDisplays)
            {
                if (!_config.Monitors.TryGetValue(display.Id, out var monitorConfig))
                {
                    monitorConfig = FindByName(display.Name);
                    if (monitorConfig is null)
                    {
                        monitorConfig = new MonitorConfig
                        {
                            Name = display.Name,
                            Brightness = shared,
                            GammaTrim = 0
                        };

                        Log.Info($"New monitor discovered: {display.Name} ({display.Id}); default DDC {shared}%, gamma 0%.");
                    }

                    _config.Monitors[display.Id] = monitorConfig;
                    configChanged = true;
                }

                if (!string.Equals(monitorConfig.Name, display.Name, StringComparison.Ordinal))
                {
                    monitorConfig.Name = display.Name;
                    configChanged = true;
                }

                monitorConfig.Brightness = shared;
                var gammaTrim = Math.Clamp(monitorConfig.GammaTrim, 0, 50);
                var state = new MonitorState
                {
                    Id = display.Id,
                    Name = display.Name,
                    DeviceName = display.DeviceName,
                    DdcAvailable = display.DdcAvailable,
                    DdcMinimum = display.DdcMinimum,
                    DdcMaximum = display.DdcMaximum,
                    TargetPercent = shared,
                    CurrentPercent = PercentFromRaw(display, display.DdcCurrent),
                    GammaTrim = gammaTrim,
                    GammaAvailable = display.GammaAvailable,
                    Display = display
                };

                Log.Info($"Display state: {display.Name}; id={display.Id}; device={display.DeviceName}; ddc={display.DdcAvailable}; gamma={display.GammaAvailable}; handle=0x{display.DdcHandle.ToInt64():X}.");

                if (display.DdcAvailable)
                {
                    anyDdc = true;
                    var applied = await ApplyDdcCoreAsync(display, shared).ConfigureAwait(false);
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

                var gammaApplied = ApplyGammaCore(display, gammaTrim);
                state.GammaError = gammaApplied
                    ? null
                    : (display.GammaAvailable ? "Gamma rejected" : "Gamma unavailable");

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

            var topologySignature = string.Join(
                "|",
                newStates
                    .OrderBy(s => s.Id)
                    .Select(s => $"{s.Id}:{s.Display.Bounds.Left},{s.Display.Bounds.Top},{s.Display.Bounds.Width}x{s.Display.Bounds.Height}"));

            var topologyChanged = !string.IsNullOrEmpty(_topologySignature) &&
                                  !string.Equals(_topologySignature, topologySignature, StringComparison.Ordinal);
            _topologySignature = topologySignature;

            Log.Info($"Rescan complete: {newStates.Count} display(s), DDC available on {newStates.Count(s => s.DdcAvailable)}, gamma available on {newStates.Count(s => s.GammaAvailable)}.");
            MonitorsChanged?.Invoke(this, EventArgs.Empty);
            StateChanged?.Invoke(this, EventArgs.Empty);

            if (topologyChanged)
            {
                Log.Info("Display topology changed; requesting a process restart to reinitialize Magnification.");
                TopologyChanged?.Invoke(this, EventArgs.Empty);
            }

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

    public void SetAllHardwareBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        _config.SharedHardwareBrightness = percent;

        foreach (var monitor in GetMonitors())
        {
            monitor.TargetPercent = percent;
            monitor.DdcError = null;

            if (_config.Monitors.TryGetValue(monitor.Id, out var monitorConfig))
            {
                monitorConfig.Name = monitor.Name;
                monitorConfig.Brightness = percent;
            }
        }

        Log.Info($"Shared hardware brightness request: all monitors -> {percent}%.");
        QueueSave();
        _ = ApplyAllHardwareAsync(percent);
    }

    private async Task ApplyAllHardwareAsync(int percent)
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

                var applied = await ApplyDdcCoreAsync(current.Display, percent).ConfigureAwait(false);
                current.CurrentPercent = PercentFromRaw(current.Display, current.Display.DdcCurrent);
                current.DdcError = applied ? null : "DDC write failed";
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("Set shared hardware brightness failed.", ex);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _ddcGate.Release();
        }
    }

    public void SetGammaTrim(string id, int percent)
    {
        percent = Math.Clamp(percent, 0, 50);
        var state = FindMonitor(id);
        if (state is null)
        {
            Log.Warn($"Gamma request for unknown monitor id '{id}'.");
            return;
        }

        state.GammaTrim = percent;
        state.GammaError = null;

        if (!_config.Monitors.TryGetValue(id, out var monitorConfig))
        {
            monitorConfig = new MonitorConfig
            {
                Name = state.Name,
                Brightness = _config.SharedHardwareBrightness ?? state.TargetPercent
            };
            _config.Monitors[id] = monitorConfig;
        }

        monitorConfig.Name = state.Name;
        monitorConfig.GammaTrim = percent;

        Log.Info($"Gamma trim request: {state.Name} ({id}) -> {percent}%.");
        QueueSave();
        _ = ApplyGammaForStateAsync(state, percent);
    }

    private async Task ApplyGammaForStateAsync(MonitorState state, int percent)
    {
        await _gammaGate.WaitAsync().ConfigureAwait(false);
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

            var applied = ApplyGammaCore(current.Display, percent);
            current.GammaTrim = percent;
            current.GammaError = applied
                ? null
                : (current.GammaAvailable ? "Gamma rejected" : "Gamma unavailable");

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("Set gamma trim failed for " + state.Name, ex);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gammaGate.Release();
        }
    }

    private static bool ApplyGammaCore(DisplayInfo display, int trim)
    {
        if (!display.GammaAvailable || display.GammaDc == IntPtr.Zero)
        {
            return false;
        }

        trim = Math.Clamp(trim, 0, 50);

        if (trim == 0)
        {
            return display.OriginalGammaRamp is not null &&
                   SetGammaVerified(display, display.OriginalGammaRamp, "restore", logSuccess: false);
        }

        var factor = 1.0 - trim / 100.0;
        var ramp = new ushort[768];
        for (var i = 0; i < 256; i++)
        {
            var value = (ushort)Math.Round(i * 257.0 * factor);
            ramp[i] = value;
            ramp[256 + i] = value;
            ramp[512 + i] = value;
        }

        return SetGammaVerified(display, ramp, $"{trim}% trim", logSuccess: true);
    }

    private static bool SetGammaVerified(DisplayInfo display, ushort[] ramp, string label, bool logSuccess)
    {
        try
        {
            if (!Native.SetDeviceGammaRamp(display.GammaDc, ramp))
            {
                Log.Warn($"SetDeviceGammaRamp failed for {display.Name} ({label}).");
                return false;
            }

            var check = new ushort[768];
            if (!Native.GetDeviceGammaRamp(display.GammaDc, check))
            {
                Log.Warn($"Gamma readback unavailable for {display.Name} ({label}).");
                return false;
            }

            var whiteDelta = Math.Abs((int)check[255] - ramp[255]);
            var midDelta = Math.Abs((int)check[128] - ramp[128]);
            if (whiteDelta > 1024 || midDelta > 1024)
            {
                Log.Warn($"Gamma readback mismatch for {display.Name} ({label}): white={check[255]} target={ramp[255]}.");
                return false;
            }

            if (logSuccess)
            {
                Log.Info($"Gamma {display.Name}: {label}, white={check[255]}.");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Gamma apply threw for {display.Name}: {ex.Message}");
            return false;
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
        var monitors = GetMonitors();
        _config.DeepDimEnabled = false;
        _config.DeepDimPercent = 80;
        _config.SharedHardwareBrightness = 100;

        foreach (var monitor in monitors)
        {
            monitor.TargetPercent = 100;
            monitor.GammaTrim = 0;
            monitor.DdcError = null;
            monitor.GammaError = null;

            _config.Monitors[monitor.Id] = new MonitorConfig
            {
                Name = monitor.Name,
                Brightness = 100,
                GammaTrim = 0
            };

            _ = ApplyGammaForStateAsync(monitor, 0);
        }

        QueueSave();
        ApplyMag();
        _ = ApplyAllHardwareAsync(100);
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

        if (_lastMagScale is { } last && Math.Abs(last - scale) < 0.001f)
        {
            _magRetryAttempts = 0;
            return true;
        }

        if (TryApplyMagEffect(scale))
        {
            return true;
        }

        _lastMagScale = null;

        if (!_disposed && _magRetryAttempts < 3)
        {
            _magRetryAttempts++;
            _magRetryTimer.Change(1000, Timeout.Infinite);
            Log.Info($"Scheduling Mag retry {_magRetryAttempts}/3.");
        }

        return false;
    }

    private bool TryApplyMagEffect(float scale)
    {
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
            Log.Warn($"MagSetFullscreenColorEffect failed (Win32 {error}) at scale {scale:0.00}.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("MagSetFullscreenColorEffect threw: " + ex.Message);
            return false;
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
            _magRetryTimer.Dispose();
            _watchdogTimer.Dispose();
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
        _gammaGate.Dispose();
        Log.Info("DimmerController disposed.");
    }
}
