using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Media;
using NameDayMascot.Config;
using NameDayMascot.Rendering;
using NameDayMascot.Services;
using NameDayMascot.Views;

namespace NameDayMascot;

public class App : Application
{
    private AppConfig _config = new();
    private SkeletonModel? _model;
    private PetWindow? _pet;
    private KeyHookService? _keyHook;
    private TrayService? _tray;
    private SettingsWindow? _settings;
    private readonly Random _rng = new();

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _config = ConfigStore.Load();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 按配置的皮肤加载，失败回退到 311801
            string skin = string.IsNullOrWhiteSpace(_config.Skin) ? "311801" : _config.Skin;
            _model = TryCreateModel(skin) ?? TryCreateModel("311801");

            if (_model != null)
            {
                _pet = new PetWindow(_config, _model);
                desktop.MainWindow = _pet;
                desktop.Exit += (_, __) => Cleanup();

                _keyHook = new KeyHookService();
                _keyHook.KeyPressed += OnGlobalKey;
                RefreshKeyFilter();
                bool hookOk = _keyHook.Start();

                // 启动时把绑定表和钩子状态写进日志，快捷键不响应时可直接对照排查
                Logger.Log($"[Key] 钩子运行={hookOk}，绑定 {_config.KeyBindings.Count} 条: "
                    + string.Join(" | ", _config.KeyBindings.Select(
                        k => $"{(k.Enabled ? "" : "[禁]")}{k.Key}→{string.Join("/", k.Actions)}")));

                RebuildTray();
            }
            else
            {
                desktop.MainWindow = new Window
                {
                    Title = "NameDayMascot",
                    Width = 480,
                    Height = 180,
                    Content = new TextBlock
                    {
                        Text = "缺少模型资源。请确认 Assets/characters/ 下存在 311801 / 311802 / 311803 三套，" +
                               "每套含同名的 .atlas、.skel 和 .png 图集。",
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(16),
                    },
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnGlobalKey(KeyEvent e)
    {
        if (_pet == null) return;

        // 设置面板开关：固定快捷键（不占用动作绑定表）
        if (e.Ctrl && !e.Alt && !e.Shift)
        {
            if (string.Equals(e.Key, "VcY", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log("[Key] Ctrl+Y → 打开设置");
                ShowSettings();
                return;
            }
            if (string.Equals(e.Key, "VcN", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log("[Key] Ctrl+N → 关闭设置");
                CloseSettings();
                return;
            }
        }

        foreach (var kb in _config.KeyBindings)
        {
            if (!kb.Enabled) continue;
            if (!KeyMatches(kb.Key, e)) continue;

            if (kb.Actions.Count > 0)
            {
                string picked = kb.Actions[_rng.Next(kb.Actions.Count)];
                Logger.Log($"[Key] 匹配 {kb.Key} → {picked}");
                _pet.PlayAction(picked);
            }
            break;
        }
    }

    /// <summary>
    /// 把 "Ctrl+Alt+Vc1" 拆成「修饰键 + 主键」后与事件比较。
    /// 修饰键要求精确一致（例如配置 Ctrl+Vc1 时，再按 Shift 不会触发）。
    /// </summary>
    private static bool KeyMatches(string spec, KeyEvent e)
    {
        if (string.IsNullOrWhiteSpace(spec)) return false;

        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        string main = parts[^1];
        bool ctrl = false, alt = false, shift = false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
            }
        }

        return string.Equals(main, e.Key, StringComparison.OrdinalIgnoreCase)
               && ctrl == e.Ctrl && alt == e.Alt && shift == e.Shift;
    }

    /// <summary>切换皮肤（三套之间切换），重建渲染与托盘动作菜单。</summary>
    public void SwitchSkin(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (id == _config.Skin && _model != null) return;

        var newModel = TryCreateModel(id);
        if (newModel == null) return;

        var oldModel = _model;
        _config.Skin = id;
        ConfigStore.Save(_config);

        // 动画名不通用：当前选中的待机/点击动画若在新角色里不存在，重置为空（自动取第一个）
        if (!string.IsNullOrEmpty(_config.IdleAnimation) && !newModel.HasAnimation(_config.IdleAnimation))
            _config.IdleAnimation = "";
        if (!string.IsNullOrEmpty(_config.ClickAnimation) && !newModel.HasAnimation(_config.ClickAnimation))
            _config.ClickAnimation = "";

        _model = newModel;
        _pet?.SetModel(newModel);
        oldModel?.Dispose();

        RebuildTray();
        Logger.Log($"切换到皮肤 {id}");
    }

    private void RebuildTray()
    {
        _tray?.Dispose();
        _tray = null;
        if (_model == null) return;

        _tray = new TrayService(this,
            onOpenSettings: ShowSettings,
            onToggleWindow: TogglePet,
            onQuit: () => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(),
            actionNames: _model.AnimationNames,
            onPlayAction: name => _pet?.PlayAction(name));
    }

    private void ShowSettings()
    {
        if (_pet == null) return;
        if (_settings is { IsVisible: true })
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsWindow(_config, _pet, SwitchSkin);
        // 关闭设置面板时刷新按键过滤（用户可能新增了不带修饰键的绑定）
        _settings.Closed += (_, __) => { _settings = null; RefreshKeyFilter(); };
        _settings.Show();
        _settings.Activate();
    }

    /// <summary>
    /// 只有在「所有启用绑定都带修饰键」时才开启过滤。
    /// 一旦存在 F1 这类裸键绑定，就必须放行无修饰按键。
    /// </summary>
    private void RefreshKeyFilter()
    {
        if (_keyHook == null) return;
        bool hasPlain = _config.KeyBindings.Exists(kb => kb.Enabled && !kb.Key.Contains('+'));
        _keyHook.FilterPlainKeys = !hasPlain;
    }

    /// <summary>关闭设置面板（Ctrl+N）。</summary>
    private void CloseSettings()
    {
        if (_settings is { IsVisible: true })
        {
            _settings.Close();
            _settings = null;
        }
    }

    private void TogglePet()
    {
        if (_pet == null) return;
        if (_pet.IsVisible)
        {
            // 隐藏时停掉渲染循环，否则 30fps 空跑白耗 CPU
            _pet.SetRenderRunning(false);
            _pet.Hide();
        }
        else
        {
            _pet.SetRenderRunning(true);
            _pet.Show();
            _pet.Activate();
        }
    }

    private void Cleanup()
    {
        ConfigStore.Save(_config);
        _keyHook?.Dispose();
        _tray?.Dispose();
    }

    private static string? FindCharacterDir(string id)
    {
        string baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Assets", "characters", id),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Assets", "characters", id)),
        };
        foreach (var c in candidates)
            if (Directory.Exists(c)) return c;
        return null;
    }

    private static SkeletonModel? TryCreateModel(string id)
    {
        try
        {
            var dir = FindCharacterDir(id);
            if (dir == null) return null;
            string atlas = Path.Combine(dir, id + ".atlas");
            string skel = Path.Combine(dir, id + ".skel");
            if (!File.Exists(atlas) || !File.Exists(skel)) return null;
            return new SkeletonModel(atlas, skel);
        }
        catch (Exception ex)
        {
            Logger.Error($"加载皮肤 {id} 失败", ex);
            return null;
        }
    }
}
