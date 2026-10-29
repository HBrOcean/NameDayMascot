using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace NameDayMascot.Services;

/// <summary>托盘图标 + 原生右键菜单（设置 / 显示隐藏 / 快速动作 / 退出）。</summary>
public sealed class TrayService : IDisposable
{
    private readonly TrayIcon _tray;

    public TrayService(
        Application app,
        Action onOpenSettings,
        Action onToggleWindow,
        Action onQuit,
        IReadOnlyList<string> actionNames,
        Action<string> onPlayAction)
    {
        _tray = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "NameDayMascot",
        };

        var menu = new NativeMenu();

        var miShow = new NativeMenuItem("显示 / 隐藏桌宠");
        miShow.Command = new RelayCommand(onToggleWindow);
        menu.Add(miShow);

        // 快速动作子菜单
        if (actionNames.Count > 0)
        {
            var miActions = new NativeMenuItem("播放动作");
            var sub = new NativeMenu();
            foreach (var name in actionNames)
            {
                var it = new NativeMenuItem(name);
                string captured = name;
                it.Command = new RelayCommand(() => onPlayAction(captured));
                sub.Add(it);
            }
            miActions.Menu = sub;
            menu.Add(miActions);
        }

        menu.Add(new NativeMenuItemSeparator());

        var miSettings = new NativeMenuItem("设置…");
        miSettings.Command = new RelayCommand(onOpenSettings);
        menu.Add(miSettings);

        var miQuit = new NativeMenuItem("退出");
        miQuit.Command = new RelayCommand(onQuit);
        menu.Add(miQuit);

        _tray.Menu = menu;
        _tray.Clicked += (_, __) => onToggleWindow();

        // Avalonia 11：托盘图标集合是静态附加属性，不是 Application.TrayIcons 实例属性
        TrayIcon.SetIcons(app, new TrayIcons { _tray });
    }

    private static WindowIcon? LoadIcon()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "icons", "tray.png");
            if (File.Exists(path))
                return new WindowIcon(File.OpenRead(path));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[Tray] icon load failed: " + ex.Message);
        }
        return null;
    }

    public void Dispose()
    {
        try { _tray.Dispose(); } catch { /* ignore */ }
    }
}

/// <summary>极简 ICommand 封装。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
