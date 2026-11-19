using System;
using System.Runtime.InteropServices;

namespace SteadyDim;

internal static class Recovery
{
    public static void ResetAll()
    {
        Log.Info("Recovery reset requested.");

        try
        {
            if (Native.MagInitialize())
            {
                var identity = new Native.MAGCOLOREFFECT
                {
                    transform = new float[25]
                };
                identity.transform[0] = 1.0f;
                identity.transform[6] = 1.0f;
                identity.transform[12] = 1.0f;
                identity.transform[18] = 1.0f;
                identity.transform[24] = 1.0f;

                var set = Native.MagSetFullscreenColorEffect(ref identity);
                Log.Info($"Recovery Mag identity set: {set} (Win32 {Marshal.GetLastWin32Error()}).");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Recovery could not reset Magnification effect.", ex);
        }

        try
        {
            var displays = DisplayEnumerator.Enumerate();
            foreach (var display in displays)
            {
                if (display.DdcAvailable)
                {
                    var ok = Native.SetMonitorBrightness(display.DdcHandle, display.DdcMaximum);
                    Log.Info($"Recovery DDC {display.Name} -> {display.DdcMaximum}: {ok}.");
                }

                display.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Recovery could not reset DDC brightness.", ex);
        }

        try
        {
            var config = ConfigStore.Load();
            config.DeepDimEnabled = false;
            config.DeepDimPercent = 80;
            foreach (var monitor in config.Monitors.Values)
            {
                monitor.Brightness = 100;
            }

            ConfigStore.Save(config);
            Log.Info("Recovery config reset.");
        }
        catch (Exception ex)
        {
            Log.Error("Recovery could not reset config.", ex);
        }

        Log.Info("Recovery reset complete.");
    }
}
