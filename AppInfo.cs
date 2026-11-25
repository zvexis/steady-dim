using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace SteadyDim;

internal static class AppInfo
{
    public const string Name = "SteadyDim";
    public const string Version = "3.0.1";
    public const string MutexName = @"Local\SteadyDim.SingleInstance";
    public const string ShowEventName = @"Local\SteadyDim.Show";
    public const string TaskName = "SteadyDim";

    private static string? _dataDir;

    public static string DataDir => _dataDir ??= ResolveDataDir();

    public static bool UsingPortableDataDir { get; private set; }

    public static string ConfigPath => Path.Combine(DataDir, "config.json");
    public static string LogPath => Path.Combine(DataDir, "steadydim.log");
    public static string DiagnosePath => Path.Combine(DataDir, "diagnose.txt");

    public static string ExePath
    {
        get
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                return processPath;
            }

            return Application.ExecutablePath;
        }
    }

    public static bool IsDotnetHosted =>
        string.Equals(
            Path.GetFileNameWithoutExtension(Environment.ProcessPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);

    public static string EntryAssemblyPath =>
        Assembly.GetEntryAssembly()?.Location ?? "";

    public static string StartupCommand =>
        IsDotnetHosted && !string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? Environment.ProcessPath
            : ExePath;

    public static string StartupArguments =>
        IsDotnetHosted
            ? $"\"{EntryAssemblyPath}\" --startup"
            : "--startup";

    private static string ResolveDataDir()
    {
        // Prefer a portable data folder next to the executable. This keeps the
        // normal exe and the dotnet-hosted launcher on the same settings, and it
        // still works when security software denies writes to the user profile.
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        if (CanWrite(portable))
        {
            UsingPortableDataDir = true;
            return portable;
        }

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Name);

        UsingPortableDataDir = false;
        return local;
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}




