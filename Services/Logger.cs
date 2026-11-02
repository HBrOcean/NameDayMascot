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
                // 滚动：超过 512KB 时只丢掉最旧的部分，保留最后 256KB 以便仍有上下文
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 512 * 1024)
                    TrimTail(LogPath, 256 * 1024);

                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch { /* 日志失败不影响主流程 */ }
    }

    private static void TrimTail(string path, int keepBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length <= keepBytes) return;

        var buf = new byte[keepBytes];
        fs.Seek(-keepBytes, SeekOrigin.End);
        int n = fs.Read(buf, 0, buf.Length);
        fs.Dispose();

        // -keepBytes 会落在任意字节位置，可能把一个汉字（UTF-8 三字节）劈成两半，
        // 日志开头就出现 "?" 或乱码。跳到第一个换行之后，从下一行完整开始。
        int start = 0;
        for (int i = 0; i < n; i++)
        {
            if (buf[i] == (byte)'\n') { start = i + 1; break; }
        }
        if (n - start <= 0) return;   // 整段都没有换行（极端情况）就别动

        File.WriteAllBytes(path, buf.AsSpan(start, n - start).ToArray());
    }

    /// <summary>记录异常，包含类型、消息和调用栈（只记 Message 会丢失定位信息）。</summary>
    public static void Error(string message, Exception? ex = null)
        => Log("ERROR " + message + (ex != null ? Environment.NewLine + "  " + ex : ""));
}
