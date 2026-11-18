using System;
using System.IO;

namespace SteadyDim;

internal static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", message + Environment.NewLine + ex);

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);

            lock (Gate)
            {
                var file = new FileInfo(AppInfo.LogPath);
                if (file.Exists && file.Length > 2 * 1024 * 1024)
                {
                    var old = AppInfo.LogPath + ".old";
                    try
                    {
                        if (File.Exists(old))
                        {
                            File.Delete(old);
                        }

                        File.Move(AppInfo.LogPath, old);
                    }
                    catch
                    {
                        // Logging must never take the app down.
                    }
                }

                File.AppendAllText(
                    AppInfo.LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
