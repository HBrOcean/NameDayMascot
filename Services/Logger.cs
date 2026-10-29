using System;
using System.IO;
using NameDayMascot.Config;

namespace NameDayMascot.Services;

/// <summary>
/// 文件日志：WinExe 没有控制台，出错时把信息写到用户配置目录，便于排查。
/// 路径：&lt;AppData&gt;/NameDayMascot/log.txt
/// </summary>
public static class Logger
{
    private static readonly object Gate = new();

    public static string LogPath => Path.Combine(ConfigStore.ConfigDir, "log.txt");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(ConfigStore.ConfigDir);
                // 简单滚动：超过 512KB 清空重来，避免无限增长
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 512 * 1024)
                    File.WriteAllText(LogPath, "");
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch { /* 日志失败不影响主流程 */ }
    }

    public static void Error(string message, Exception? ex = null)
        => Log("ERROR " + message + (ex != null ? " | " + ex.GetType().Name + ": " + ex.Message : ""));
}
