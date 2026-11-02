using Avalonia;
using System;
using System.Threading;
using NameDayMascot.Services;

namespace NameDayMascot;

internal static class Program
{
    // 单实例 Mutex 名（每用户唯一）
    private const string SingleInstanceName = "NameDayMascot_SingleInstance_7F3A";

    /// <summary>
    /// 命名事件：第二个实例用它唤醒第一个实例（把设置面板亮出来）。
    /// 否则用户双击第二次时完全没反应——既没新窗口也没提示，会以为程序坏了。
    /// </summary>
    internal const string ActivateEventName = "NameDayMascot_Activate_7F3A";

    [STAThread]
    public static void Main(string[] args)
    {
        // 开发诊断：--dump 渲染一帧到系统临时目录，不开窗口
        if (args.Length > 0 && args[0] == "--dump")
        {
            Diagnostics.Dump();
            return;
        }

        // 单实例保护：已有实例在运行则通知它现身，不要静默退出
        using var mutex = new Mutex(true, SingleInstanceName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ActivateEventName);
                ev.Set();
                Logger.Log("检测到已有实例，已通知它显示窗口。");
            }
            catch (Exception ex)
            {
                // 命名事件打不开（权限/旧版本实例）就退回静默退出，不影响正确性
                Logger.Log("检测到已有实例在运行，本次启动退出（唤醒通知失败：" + ex.Message + "）");
            }
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
