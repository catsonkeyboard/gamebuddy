using System.IO;

namespace GameBuddy.Services.Diagnostics;

/// <summary>极简文件日志，便于排查扫描/抓取失败。</summary>
public static class AppLog
{
    private static readonly object Sync = new();

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            Services.Storage.AppPaths.EnsureCreated();
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level} {message}";
            if (ex is not null) line += Environment.NewLine + ex;
            lock (Sync)
            {
                File.AppendAllText(Services.Storage.AppPaths.LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志失败不能影响主流程
        }
    }
}
