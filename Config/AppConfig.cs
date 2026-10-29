using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NameDayMascot.Services;

namespace NameDayMascot.Config;

/// <summary>单条按键映射：一个按键 → 一个（或多个，随机挑一个）动作。</summary>
public sealed class KeyBinding
{
    /// <summary>组合键，如 "Ctrl+Vc1"、"VcF1"、"Ctrl+Shift+VcA"。修饰键支持 Ctrl/Alt/Shift。</summary>
    public string Key { get; set; } = "";

    /// <summary>触发的动作名（Spine 动画名）。多个时随机播一个。</summary>
    public List<string> Actions { get; set; } = new();

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>桌宠整体配置（JSON 持久化）。</summary>
public sealed class AppConfig
{
    // —— 窗口/桌宠外观 ——
    public double WindowX { get; set; } = -1;   // -1 = 自动定位
    public double WindowY { get; set; } = -1;
    public double Scale { get; set; } = 0.15;    // 桌宠缩放（首次启动默认 15%）
    public bool Topmost { get; set; } = true;
    public bool ShowWhiteCard { get; set; } = false; // 白卡片背景（保底/调试）
    public bool Clickable { get; set; } = true;
    public bool LockOnAction { get; set; } = true;    // 动作锁定：播完才回待机

    // —— 皮肤（角色 assetId）——
    public string Skin { get; set; } = "311801";

    /// <summary>可用的三套皮肤（命名日系列）。</summary>
    public static readonly string[] AvailableSkins = { "311801", "311802", "311803" };

    /// <summary>皮肤显示名。</summary>
    public static string SkinDisplayName(string id) => id switch
    {
        "311801" => "命名日 · 一",
        "311802" => "命名日 · 二",
        "311803" => "命名日 · 三",
        _ => id,
    };

    // —— 动作 ——
    public string IdleAnimation { get; set; } = "idle";   // 待机（默认 idle，不存在时回退第一个）
    public string ClickAnimation { get; set; } = "";  // 点击动作（空=自动挑一个）

    // —— 行为 ——
    public bool SnapToEdge { get; set; } = true;  // 屏幕边缘吸附
    public bool AutoStart { get; set; } = false;  // 开机自启

    // —— 按键映射 ——
    public List<KeyBinding> KeyBindings { get; set; } = new();

    /// <summary>是否已写入过默认快捷键（旧配置升级时补一次，之后不再覆盖用户自定义）。</summary>
    public bool DefaultKeysApplied { get; set; } = false;

    /// <summary>
    /// 默认快捷键：统一 Ctrl + 有意义的字母/数字，互相不冲突。
    /// Ctrl+1/2 → skill1/skill2；Ctrl+D → die；Ctrl+Q → unique；其余按动作首字母。
    /// （Ctrl+Y 打开设置、Ctrl+N 关闭设置由程序固定处理，不在此表内。）
    /// </summary>
    public static List<KeyBinding> CreateDefaultBindings()
    {
        static KeyBinding B(string key, string action) =>
            new() { Key = key, Actions = new List<string> { action } };

        return new List<KeyBinding>
        {
            B("Ctrl+Vc1", "skill1"),
            B("Ctrl+Vc2", "skill2"),
            B("Ctrl+VcD", "die"),
            B("Ctrl+VcQ", "unique"),
            B("Ctrl+VcB", "born"),
            B("Ctrl+VcG", "giddy"),
            B("Ctrl+VcH", "hit"),
            B("Ctrl+VcI", "idle"),
            B("Ctrl+VcP", "posture"),
            B("Ctrl+VcS", "sleep"),
        };
    }

    /// <summary>是否已有记忆的窗口位置。</summary>
    [JsonIgnore]
    public bool HasSavedPosition => WindowX >= 0 && WindowY >= 0;
}

/// <summary>配置读写：存到用户配置目录下的 config.json。</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ConfigDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NameDayMascot");

    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                if (cfg != null)
                {
                    // 只要配置里没有任何一条「键名有效」的绑定，就补一份默认。
                    // 用"是否有有效绑定"而不是 Count>0 判断：旧版本可能留下
                    // Key 为空的空壳条目，导致快捷键整表失效。
                    bool hasValid = cfg.KeyBindings.Exists(kb => !string.IsNullOrWhiteSpace(kb.Key));
                    if (!hasValid)
                    {
                        cfg.KeyBindings = AppConfig.CreateDefaultBindings();
                        Logger.Log("[Config] 未发现有效按键绑定，已写入默认快捷键");
                    }
                    cfg.DefaultKeysApplied = true;
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[ConfigStore] load failed: " + ex.Message);
        }
        // 首次运行：直接带默认快捷键
        return new AppConfig
        {
            KeyBindings = AppConfig.CreateDefaultBindings(),
            DefaultKeysApplied = true,
        };
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[ConfigStore] save failed: " + ex.Message);
        }
    }
}
