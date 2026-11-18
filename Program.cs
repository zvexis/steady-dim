using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace SteadyDim;

internal sealed record AppOptions(bool Startup, bool NoStartup, bool Diagnose, bool Reset);

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = Parse(args);

        if (options.Diagnose)
        {
            Diagnostics.Run();
            return 0;
        }

        if (options.Reset)
        {
            Recovery.ResetAll();
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var mutex = new Mutex(true, AppInfo.MutexName, out var createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            return 0;
        }

        var config = ConfigStore.Load();
        Log.Info($"Starting {AppInfo.Name} {AppInfo.Version}; exe={AppInfo.ExePath}; data={AppInfo.DataDir}; portable={AppInfo.UsingPortableDataDir}; startup={options.Startup}; newConfig={config.IsNew}.");

        Application.ThreadException += (_, e) =>
        {
            Log.Error("Unhandled UI exception.", e.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled exception.", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        };

        using var app = new TrayApp(options, config);
        Application.Run(app);
        Log.Info("Application exited.");
        return 0;
    }

    private static AppOptions Parse(string[] args)
    {
        var startup = args.Any(a => string.Equals(a, "--startup", StringComparison.OrdinalIgnoreCase));
        var noStartup = args.Any(a => string.Equals(a, "--no-startup", StringComparison.OrdinalIgnoreCase));
        var diagnose = args.Any(a => string.Equals(a, "--diagnose", StringComparison.OrdinalIgnoreCase));
        var reset = args.Any(a => string.Equals(a, "--reset", StringComparison.OrdinalIgnoreCase));
        return new AppOptions(startup, noStartup, diagnose, reset);
    }

    private static void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var showEvent = EventWaitHandle.OpenExisting(AppInfo.ShowEventName);
                showEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
            catch
            {
                return;
            }
        }
    }
}
