using System;
using System.IO;
using System.Runtime.InteropServices;

namespace NameDayMascot.Services;

/// <summary>开机自启：Windows 注册表 Run 键 / macOS LaunchAgent / Linux autostart desktop。</summary>
public static class AutoStartService
{
    private const string AppName = "NameDayMascot";

    private static string ExecutablePath =>
        Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
        ?? AppContext.BaseDirectory;

    public static bool IsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return Microsoft.Win32.Registry.CurrentUser
                    .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")?
                    .GetValue(AppName) != null;

            if (OperatingSystem.IsMacOS())
                return File.Exists(LaunchAgentPath);

            return File.Exists(LinuxAutostartPath);
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var key = Microsoft.Win32.Registry.CurrentUser
                    .CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (key == null) return;
                if (enabled) key.SetValue(AppName, "\"" + ExecutablePath + "\"");
                else key.DeleteValue(AppName, false);
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                if (enabled)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LaunchAgentPath)!);
                    File.WriteAllText(LaunchAgentPath,
                        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" " +
                        "\"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                        "<plist version=\"1.0\"><dict>" +
                        "<key>Label</key><string>com.oceaniat.namedaymascot</string>" +
                        "<key>ProgramArguments</key><array><string>" + ExecutablePath + "</string></array>" +
                        "<key>RunAtLoad</key><true/></dict></plist>\n");
                }
                else if (File.Exists(LaunchAgentPath)) File.Delete(LaunchAgentPath);
                return;
            }

            // Linux
            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LinuxAutostartPath)!);
                File.WriteAllText(LinuxAutostartPath,
                    "[Desktop Entry]\nType=Application\nName=" + AppName + "\n" +
                    "Exec=" + ExecutablePath + "\nX-GNOME-Autostart-enabled=true\n");
            }
            else if (File.Exists(LinuxAutostartPath)) File.Delete(LinuxAutostartPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[AutoStart] failed: " + ex.Message);
        }
    }

    private static string LaunchAgentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", "com.oceaniat.namedaymascot.plist");

    private static string LinuxAutostartPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "autostart", AppName + ".desktop");
}
