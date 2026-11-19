using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SteadyDim;

internal static class Diagnostics
{
    public static void Run()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"SteadyDim {AppInfo.Version} diagnostics");
        builder.AppendLine($"Time: {DateTime.Now:O}");
        builder.AppendLine($"OS: {Environment.OSVersion}");
        builder.AppendLine($"64-bit process: {Environment.Is64BitProcess}");
        builder.AppendLine($"Exe: {AppInfo.ExePath}");
        builder.AppendLine($"Config: {AppInfo.ConfigPath}");
        builder.AppendLine();

        try
        {
            var magInitialized = Native.MagInitialize();
            builder.AppendLine($"MagInitialize: {magInitialized} (Win32 {Marshal.GetLastWin32Error()})");

            if (magInitialized)
            {
                var effect = new Native.MAGCOLOREFFECT { transform = new float[25] };
                var read = Native.MagGetFullscreenColorEffect(ref effect);
                builder.AppendLine($"MagGetFullscreenColorEffect: {read} (Win32 {Marshal.GetLastWin32Error()})");
                if (read)
                {
                    builder.AppendLine($"  current RGB scale: {effect.transform[0]:0.000}, {effect.transform[6]:0.000}, {effect.transform[12]:0.000}");
                }

                // Deliberately do not call MagUninitialize; process exit is enough.
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine("Magnification diagnostic failed: " + ex);
        }

        builder.AppendLine();
        builder.AppendLine("Displays:");

        try
        {
            var displays = DisplayEnumerator.Enumerate();
            if (displays.Count == 0)
            {
                builder.AppendLine("  none");
            }

            foreach (var display in displays)
            {
                builder.AppendLine($"  {display.Name}");
                builder.AppendLine($"    id: {display.Id}");
                builder.AppendLine($"    device: {display.DeviceName}");
                builder.AppendLine($"    bounds: {display.Bounds.Left},{display.Bounds.Top} {display.Bounds.Width}x{display.Bounds.Height}");
                builder.AppendLine($"    ddc: {display.DdcAvailable} min={display.DdcMinimum} current={display.DdcCurrent} max={display.DdcMaximum}");
                builder.AppendLine($"    description: {display.DdcDescription}");
                display.Dispose();
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine("Display diagnostic failed: " + ex);
        }

        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllText(AppInfo.DiagnosePath, builder.ToString());
            Log.Info("Diagnostics written to " + AppInfo.DiagnosePath);
        }
        catch (Exception ex)
        {
            Log.Error("Could not write diagnostics.", ex);
        }
    }
}
