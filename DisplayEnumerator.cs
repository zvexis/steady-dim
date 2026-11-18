using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SteadyDim;

internal sealed class DisplayInfo : IDisposable
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public Native.RECT Bounds { get; set; }
    public IntPtr DdcHandle { get; set; }
    public bool DdcAvailable { get; set; }
    public uint DdcMinimum { get; set; }
    public uint DdcMaximum { get; set; }
    public uint DdcCurrent { get; set; }
    public string DdcDescription { get; set; } = "";

    internal Native.PHYSICAL_MONITOR[]? PhysicalMonitors { get; set; }

    public void Dispose()
    {
        if (PhysicalMonitors is { Length: > 0 } monitors)
        {
            try
            {
                Native.DestroyPhysicalMonitors((uint)monitors.Length, monitors);
            }
            catch (Exception ex)
            {
                Log.Warn("DestroyPhysicalMonitors failed: " + ex.Message);
            }
        }

        PhysicalMonitors = null;
        DdcHandle = IntPtr.Zero;
        DdcAvailable = false;
    }
}

internal static class DisplayEnumerator
{
    public static List<DisplayInfo> Enumerate()
    {
        var devicesByDisplayName = GetMonitorDevices();
        var displays = new List<DisplayInfo>();

        Native.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref Native.RECT rect, IntPtr data) =>
        {
            var info = new Native.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<Native.MONITORINFOEX>(),
                szDevice = ""
            };

            if (!Native.GetMonitorInfo(hMonitor, ref info))
            {
                return true;
            }

            devicesByDisplayName.TryGetValue(info.szDevice, out var device);
            var display = new DisplayInfo
            {
                DeviceName = info.szDevice,
                Bounds = info.rcMonitor,
                Id = string.IsNullOrWhiteSpace(device.DeviceID) ? info.szDevice : device.DeviceID,
                Name = BuildName(device, info.rcMonitor),
                DdcDescription = ""
            };

            TryAttachDdc(display, hMonitor);
            displays.Add(display);
            return true;
        };

        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        displays.Sort((a, b) =>
        {
            var byX = a.Bounds.Left.CompareTo(b.Bounds.Left);
            return byX != 0 ? byX : a.Bounds.Top.CompareTo(b.Bounds.Top);
        });

        ApplyPositionNames(displays);
        return displays;
    }

    private static void TryAttachDdc(DisplayInfo display, IntPtr hMonitor)
    {
        try
        {
            if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
            {
                return;
            }

            var monitors = new Native.PHYSICAL_MONITOR[count];
            if (!Native.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
            {
                return;
            }

            display.PhysicalMonitors = monitors;
            var physical = monitors[0];
            display.DdcHandle = physical.hPhysicalMonitor;
            display.DdcDescription = physical.szPhysicalMonitorDescription;

            if (!Native.GetMonitorBrightness(
                    physical.hPhysicalMonitor,
                    out var minimum,
                    out var current,
                    out var maximum))
            {
                Log.Warn($"DDC read failed for {display.DeviceName} ({display.Name}).");
                return;
            }

            display.DdcAvailable = true;
            display.DdcMinimum = minimum;
            display.DdcMaximum = maximum;
            display.DdcCurrent = current;
        }
        catch (Exception ex)
        {
            Log.Error("DDC enumeration failed for " + display.DeviceName, ex);
        }
    }

    private static Dictionary<string, Native.DISPLAY_DEVICE> GetMonitorDevices()
    {
        var result = new Dictionary<string, Native.DISPLAY_DEVICE>(StringComparer.OrdinalIgnoreCase);

        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var adapter = NewDisplayDevice();
            if (!Native.EnumDisplayDevices(null, adapterIndex, ref adapter, 0))
            {
                break;
            }

            if ((adapter.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0 ||
                (adapter.StateFlags & Native.DISPLAY_DEVICE_MIRRORING_DRIVER) != 0)
            {
                continue;
            }

            for (uint monitorIndex = 0; ; monitorIndex++)
            {
                var monitor = NewDisplayDevice();
                if (!Native.EnumDisplayDevices(adapter.DeviceName, monitorIndex, ref monitor, 0))
                {
                    break;
                }

                if ((monitor.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0 ||
                    (monitor.StateFlags & Native.DISPLAY_DEVICE_MIRRORING_DRIVER) != 0)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(monitor.DeviceName))
                {
                    result[monitor.DeviceName] = monitor;
                }
            }
        }

        return result;
    }

    private static Native.DISPLAY_DEVICE NewDisplayDevice() => new()
    {
        cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>(),
        DeviceName = "",
        DeviceString = "",
        DeviceID = "",
        DeviceKey = ""
    };

    private static string BuildName(Native.DISPLAY_DEVICE device, Native.RECT bounds)
    {
        var model = "";

        if (!string.IsNullOrWhiteSpace(device.DeviceID))
        {
            var parts = device.DeviceID.Split('#');
            if (parts.Length >= 3)
            {
                model = parts[1];
            }
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            model = string.IsNullOrWhiteSpace(device.DeviceString)
                ? "Display"
                : device.DeviceString;
        }

        return $"{model} — {bounds.Width}×{bounds.Height}";
    }

    private static void ApplyPositionNames(List<DisplayInfo> displays)
    {
        if (displays.Count == 0)
        {
            return;
        }

        if (displays.Count == 1)
        {
            displays[0].Name += " — Primary";
            return;
        }

        if (displays.Count == 2)
        {
            displays[0].Name += " — Left";
            displays[1].Name += " — Right";
            return;
        }

        for (var index = 0; index < displays.Count; index++)
        {
            displays[index].Name += $" — Display {index + 1}";
        }
    }
}
