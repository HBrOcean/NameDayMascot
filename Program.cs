using Avalonia;
using System;
using System.Threading;
using NameDayMascot.Services;

namespace NameDayMascot;

internal static class Program
{
    // 单实例 Mutex 名（每用户唯一）
    private const string SingleInstanceName = "NameDayMascot_SingleInstance_7F3A";

    [STAThread]
    public static void Main(string[] args)
    {
        // 开发诊断：--dump 渲染一帧到 /tmp/dump.png，不开窗口
        if (args.Length > 0 && args[0] == "--dump")
        {
            Diagnostics.Dump();
            return;
        }

        // 单实例保护：已有实例在运行则直接退出，避免起两个桌宠
        using var mutex = new Mutex(true, SingleInstanceName, out bool createdNew);
        if (!createdNew)
        {
            Logger.Log("检测到已有实例在运行，本次启动退出。");
            return;
        }

        try
        {
            Logger.Log($"启动 NameDayMascot (pid={Environment.ProcessId})");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Logger.Error("未处理异常导致退出", ex);
            throw;
        }
        finally
        {
            GC.KeepAlive(mutex);
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
