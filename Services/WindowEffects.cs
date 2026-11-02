using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace NameDayMascot.Services;

/// <summary>
/// 窗口原生效果。目前只实现 Windows 的鼠标穿透：
/// WS_EX_TRANSPARENT 让命中测试穿透到下层窗口。
/// Avalonia 的透明窗口本身已带 WS_EX_LAYERED，所以只补 TRANSPARENT 这一位即可。
/// </summary>
public static class WindowEffects
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>开启/关闭鼠标穿透。非 Windows 平台忽略并记日志。</summary>
    public static void SetMouseThrough(Window window, bool through)
    {
        if (!OperatingSystem.IsWindows())
        {
            Logger.Log($"[Window] 鼠标穿透仅 Windows 生效，当前平台忽略（through={through}）");
            return;
        }

        try
        {
            var handle = window.TryGetPlatformHandle();
            if (handle == null || handle.Handle == IntPtr.Zero)
            {
                Logger.Log("[Window] 取不到原生句柄，穿透设置跳过");
                return;
            }

            IntPtr hwnd = handle.Handle;
            long style = GetExStyle(hwnd);
            long newStyle = through
                ? (style | WS_EX_TRANSPARENT)
                : (style & ~WS_EX_TRANSPARENT);

            if (newStyle != style) SetExStyle(hwnd, newStyle);
            Logger.Log($"[Window] 鼠标穿透 = {through}");
        }
        catch (Exception ex)
        {
            Logger.Error("[Window] 设置鼠标穿透失败", ex);
        }
    }

    private static long GetExStyle(IntPtr hwnd) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64()
            : GetWindowLong32(hwnd, GWL_EXSTYLE);

    private static void SetExStyle(IntPtr hwnd, long style)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(style));
        else SetWindowLong32(hwnd, GWL_EXSTYLE, (int)style);
    }

    // —— 置底 ——
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>把窗口压到所有其他窗口之下（仅 Windows）。</summary>
    public static void SetBottom(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            Logger.Log("[Window] 置底仅 Windows 生效，当前平台忽略");
            return;
        }

        try
        {
            var handle = window.TryGetPlatformHandle();
            if (handle == null || handle.Handle == IntPtr.Zero) return;

            SetWindowPos(handle.Handle, HWND_BOTTOM, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            Logger.Log("[Window] 已置底");
        }
        catch (Exception ex)
        {
            Logger.Error("[Window] 置底失败", ex);
        }
    }
}
