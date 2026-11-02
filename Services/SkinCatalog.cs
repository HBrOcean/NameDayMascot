using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NameDayMascot.Services;

/// <summary>一套皮肤的元信息。</summary>
public sealed class SkinEntry
{
    /// <summary>目录名，同时也是 .atlas / .skel 的文件名前缀。</summary>
    public string Id { get; init; } = "";

    /// <summary>界面上显示的名字（一定有值，不会为 null）。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>皮肤目录；资源缺失时为 null。</summary>
    public string? Dir { get; init; }

    /// <summary>是否为内置的三套（内置排在前面，且名字固定）。</summary>
    public bool IsBuiltin { get; init; }

    /// <summary>.atlas 与 .skel 齐全，真的能加载。</summary>
    public bool IsUsable { get; init; }
}

/// <summary>
/// 皮肤目录扫描。
///
/// 约定：Assets/characters/&lt;id&gt;/ 下放同名的 &lt;id&gt;.atlas、&lt;id&gt;.skel 和图集，
/// 该目录即为一套皮肤。放一个 display.txt（UTF-8，单行）可以自定义显示名。
///
/// 扫描失败（目录不存在、权限不够、枚举异常）时退化为内置三套的列表，
/// 保证界面永远不会因为"扫不到"而变空。
/// </summary>
public static class SkinCatalog
{
    // 内置三套：决定排序优先级和默认显示名
    private static readonly (string Id, string Name)[] Builtin =
    {
        ("311801", "命名日 · 一"),
        ("311802", "命名日 · 二"),
        ("311803", "命名日 · 三"),
    };

    private static List<SkinEntry>? _cache;

    /// <summary>当前可用的全部皮肤（内置在前，其余按目录名排序）。</summary>
    public static IReadOnlyList<SkinEntry> All => _cache ??= Scan();

    /// <summary>内置三套的 id（首次启动校验等场合用）。</summary>
    public static IEnumerable<string> BuiltinIds => Builtin.Select(b => b.Id);

    /// <summary>丢弃缓存，下次访问 All 时重新扫描。</summary>
    public static void Invalidate() => _cache = null;

    /// <summary>皮肤显示名：内置用内置名；自定义读 display.txt；都没有就用 id。</summary>
    public static string DisplayNameOf(string id)
    {
        var hit = All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        if (hit != null) return hit.DisplayName;

        // 不在缓存里（比如配置里存了已删掉的皮肤），现场算一个
        string? dir = DirOf(id);
        return dir != null ? DisplayNameFromDir(id, dir) : id;
    }

    /// <summary>某套皮肤的目录；找不到返回 null。</summary>
    public static string? DirOf(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (string root in RootCandidates())
        {
            string dir = Path.Combine(root, id);
            if (Directory.Exists(dir)) return dir;
        }
        return null;
    }

    /// <summary>characters 根目录的候选路径（程序目录优先，其次是开发时的源码目录）。</summary>
    public static IEnumerable<string> RootCandidates()
    {
        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "Assets", "characters");
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Assets", "characters"));
    }

    private static List<SkinEntry> Scan()
    {
        // id → 目录。同名时以先找到的为准（程序目录优先于源码目录）
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in RootCandidates())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string id = Path.GetFileName(dir);
                    if (!found.ContainsKey(id)) found[id] = dir;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Skin] 扫描目录失败 {root}", ex);
            }
        }

        var list = new List<SkinEntry>();

        // 1) 内置三套永远在列表里（哪怕资源缺失也显示，方便用户看出少了什么）
        foreach (var (id, name) in Builtin)
        {
            found.TryGetValue(id, out string? dir);
            list.Add(Make(id, name, dir, isBuiltin: true));
            found.Remove(id);
        }

        // 2) 其余自定义皮肤，按 id 排序
        foreach (var kv in found.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            list.Add(Make(kv.Key, DisplayNameFromDir(kv.Key, kv.Value), kv.Value, isBuiltin: false));

        Logger.Log($"[Skin] 扫描到 {list.Count} 套皮肤（其中可用 {list.Count(e => e.IsUsable)} 套）");
        return list;
    }

    private static SkinEntry Make(string id, string name, string? dir, bool isBuiltin)
    {
        bool usable = false;
        if (dir != null)
        {
            try
            {
                usable = File.Exists(Path.Combine(dir, id + ".atlas"))
                      && File.Exists(Path.Combine(dir, id + ".skel"));
            }
            catch (Exception ex) { Logger.Error($"[Skin] 检查 {id} 资源失败", ex); }
        }

        return new SkinEntry
        {
            Id = id,
            DisplayName = string.IsNullOrWhiteSpace(name) ? id : name,
            Dir = dir,
            IsBuiltin = isBuiltin,
            IsUsable = usable,
        };
    }

    /// <summary>
    /// 自定义皮肤的显示名兜底链：display.txt → id。
    /// 读不到或内容为空都不抛异常，一律退回 id（显示名必须有值）。
    /// </summary>
    private static string DisplayNameFromDir(string id, string dir)
    {
        try
        {
            string txt = Path.Combine(dir, "display.txt");
            if (File.Exists(txt))
            {
                string name = File.ReadAllText(txt).Trim();
                // 只取第一行，且限长，避免有人把整篇说明贴进去
                int nl = name.IndexOfAny(new[] { '\r', '\n' });
                if (nl > 0) name = name.Substring(0, nl).Trim();
                if (name.Length > 0) return name.Length > 24 ? name.Substring(0, 24) : name;
            }
        }
        catch (Exception ex) { Logger.Error($"[Skin] 读取 {id}/display.txt 失败", ex); }

        return id;
    }
}
