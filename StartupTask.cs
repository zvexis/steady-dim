using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SteadyDim;

internal sealed record StartupResult(bool Success, bool UsedFallback, string Message);

internal static class StartupTask
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = AppInfo.Name;

    public static StartupResult Ensure(bool enabled)
    {
        if (!enabled)
        {
            Disable();
            return new StartupResult(true, false, "Startup disabled.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return new StartupResult(false, false, "Startup task is Windows-only.");
        }

        try
        {
            var xml = BuildTaskXml();
            Directory.CreateDirectory(AppInfo.DataDir);
            var temp = Path.Combine(AppInfo.DataDir, AppInfo.Name + "-task-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(temp, xml, new UnicodeEncoding(false, true));

            var result = RunProcess(
                "schtasks.exe",
                new[]
                {
                    "/Create",
                    "/TN", AppInfo.TaskName,
                    "/XML", temp,
                    "/F"
                });

            TryDelete(temp);

            if (result.ExitCode == 0)
            {
                RemoveRunKey();
                Log.Info("Startup scheduled task registered.");
                return new StartupResult(true, false, "Startup task registered.");
            }

            Log.Warn($"schtasks /Create failed ({result.ExitCode}): {result.Output} {result.Error}");
        }
        catch (Exception ex)
        {
            Log.Error("Could not create scheduled task; falling back to Run key.", ex);
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key?.SetValue(
                RunValueName,
                $"\"{AppInfo.StartupCommand}\" {AppInfo.StartupArguments}",
                RegistryValueKind.String);
            Log.Warn("Startup fallback registered in HKCU Run.");
            return new StartupResult(true, true, "Scheduled task failed; using HKCU Run fallback.");
        }
        catch (Exception ex)
        {
            Log.Error("Could not register startup fallback.", ex);
            return new StartupResult(false, false, "Could not register startup: " + ex.Message);
        }
    }

    public static void Disable()
    {
        try
        {
            var result = RunProcess("schtasks.exe", new[] { "/Delete", "/TN", AppInfo.TaskName, "/F" });
            if (result.ExitCode == 0)
            {
                Log.Info("Startup scheduled task removed.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not remove scheduled task: " + ex.Message);
        }

        RemoveRunKey();
    }

    public static bool IsTaskPresent()
    {
        try
        {
            var result = RunProcess("schtasks.exe", new[] { "/Query", "/TN", AppInfo.TaskName });
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) is not null)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                Log.Info("HKCU Run fallback removed.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not remove HKCU Run fallback: " + ex.Message);
        }
    }

    private static string BuildTaskXml()
    {
        var userId = WindowsIdentity.GetCurrent().Name;
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(ns + "Task",
                new XAttribute("version", "1.4"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Description", "SteadyDim - DDC hardware brightness plus deep dim."),
                    new XElement(ns + "Author", userId)),
                new XElement(ns + "Triggers",
                    new XElement(ns + "LogonTrigger",
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "UserId", userId),
                        new XElement(ns + "Delay", "PT10S"))),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(ns + "UserId", userId),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "LeastPrivilege"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "AllowHardTerminate", "true"),
                    new XElement(ns + "StartWhenAvailable", "true"),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(ns + "IdleSettings",
                        new XElement(ns + "StopOnIdleEnd", "false"),
                        new XElement(ns + "RestartOnIdle", "false")),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "Hidden", "false"),
                    new XElement(ns + "RunOnlyIfIdle", "false"),
                    new XElement(ns + "WakeToRun", "false"),
                    new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(ns + "Priority", "7"),
                    new XElement(ns + "RestartOnFailure",
                        new XElement(ns + "Interval", "PT1M"),
                        new XElement(ns + "Count", "3"))),
                new XElement(ns + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", AppInfo.StartupCommand),
                        new XElement(ns + "Arguments", AppInfo.StartupArguments)))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static ProcessResult RunProcess(string fileName, string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info);
        if (process is null)
        {
            return new ProcessResult(-1, "", "Process.Start returned null.");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(15000);
        return new ProcessResult(process.HasExited ? process.ExitCode : -1, output.Trim(), error.Trim());
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temp file only.
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
