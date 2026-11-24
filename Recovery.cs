using System;

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
                var identity = new Native.MAGCOLOREFFECT { transform = new float[25] };
                identity.transform[0] = 1.0f;
                identity.transform[6] = 1.0f;
                identity.transform[12] = 1.0f;
                identity.transform[18] = 1.0f;
                identity.transform[24] = 1.0f;

                var set = Native.MagSetFullscreenColorEffect(ref identity);
                Log.Info($"Recovery software dim cleared: {set} (Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Recovery could not reset software dim.", ex);
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

                if (display.GammaAvailable && display.GammaDc != IntPtr.Zero)
                {
                    var identity = new ushort[768];
                    for (var i = 0; i < 256; i++)
                    {
                        var value = (ushort)(i * 257);
                        identity[i] = value;
                        identity[256 + i] = value;
                        identity[512 + i] = value;
                    }

                    var ok = Native.SetDeviceGammaRamp(display.GammaDc, identity);
                    if (ok)
                    {
                        display.OriginalGammaRamp = identity;
                        display.GammaApplied = true;
                    }

                    Log.Info($"Recovery gamma {display.Name} -> identity: {ok}.");
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
            config.Level = 0;
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
