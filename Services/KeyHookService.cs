using System;
using Avalonia.Threading;
using SharpHook;
using SharpHook.Data;

namespace NameDayMascot.Services;

/// <summary>一次全局按键事件（含修饰键状态）。Key 为 SharpHook 的 KeyCode 枚举名，如 Vc1 / VcY。</summary>
public readonly record struct KeyEvent(string Key, bool Ctrl, bool Alt, bool Shift);

/// <summary>
/// 全局键盘监听（SharpHook / libuiohook），跨 Windows/macOS/Linux。
/// 事件在钩子线程触发，这里切回 UI 线程再派发。
/// </summary>
public sealed class KeyHookService : IDisposable
{
    private TaskPoolGlobalHook? _hook;
    private bool _startAttempted;

    /// <summary>钩子是否成功运行。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>按键按下（带修饰键）。</summary>
    public event Action<KeyEvent>? KeyPressed;

    public bool Start()
    {
        if (_startAttempted) return IsRunning;
        _startAttempted = true;
        try
        {
            _hook = new TaskPoolGlobalHook();
            _hook.KeyPressed += OnKeyPressed;
            // fire-and-forget 的异常必须显式接住，否则 WinExe 下完全静默
            _hook.RunAsync(GlobalHookType.Keyboard).ContinueWith(
                t => Logger.Error("[KeyHook] 钩子线程异常退出", t.Exception),
                TaskContinuationOptions.OnlyOnFaulted);
            IsRunning = true;
            Logger.Log("[KeyHook] 已启动（SharpHook / uiohook）");
        }
        catch (Exception ex)
        {
            Logger.Error("[KeyHook] 启动失败", ex);
            IsRunning = false;
        }
        return IsRunning;
    }

    /// <summary>
    /// 是否丢弃「无任何修饰键」的按键。默认关闭：先保证功能可用，
    /// 过滤只是省一点点 UI 线程空转，不值得拿"快捷键失灵"去换。
    /// </summary>
    public volatile bool FilterPlainKeys = false;

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        var mask = e.RawEvent.Mask;
        bool ctrl = (mask & EventMask.Ctrl) != 0;
        bool alt = (mask & EventMask.Alt) != 0;
        bool shift = (mask & EventMask.Shift) != 0;

        if (FilterPlainKeys && !ctrl && !alt && !shift) return;

        // 值先取出，避免在 lambda 里跨线程访问事件对象
        string key = e.Data.KeyCode.ToString();
        var ke = new KeyEvent(key, ctrl, alt, shift);

        Dispatcher.UIThread.Post(() =>
        {
            // 只记录带修饰键的按键，避免正常打字把日志刷爆
            if (ctrl || alt || shift) Logger.Log($"[Key] 收到 {key} ctrl={ctrl} alt={alt} shift={shift}");
            KeyPressed?.Invoke(ke);
        });
    }

    public void Dispose()
    {
        if (_hook != null)
        {
            _hook.KeyPressed -= OnKeyPressed;
            try { _hook.Dispose(); } catch { /* ignore */ }
            _hook = null;
        }
        IsRunning = false;
    }
}

/// <summary>键名显示工具：把枚举名（VcF1）转成人可读（F1）。</summary>
public static class KeyNames
{
    public static string ToDisplay(string keyEnumName)
    {
        if (string.IsNullOrEmpty(keyEnumName)) return "";
        string s = keyEnumName.StartsWith("Vc") ? keyEnumName.Substring(2) : keyEnumName;
        return s switch
        {
            "Space" => "Space",
            "Enter" or "Return" => "Enter",
            "Escape" => "Esc",
            "BackSpace" or "Backspace" => "Backspace",
            "LeftControl" => "L-Ctrl",
            "RightControl" => "R-Ctrl",
            "LeftShift" => "L-Shift",
            "RightShift" => "R-Shift",
            "LeftAlt" => "L-Alt",
            "RightAlt" => "R-Alt",
            "Up" or "UpArrow" => "↑",
            "Down" or "DownArrow" => "↓",
            "Left" or "LeftArrow" => "←",
            "Right" or "RightArrow" => "→",
            _ => s,
        };
    }
}
