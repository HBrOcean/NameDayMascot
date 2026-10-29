using System;
using System.IO;
using Avalonia.Controls;

namespace NameDayMascot.Services;

/// <summary>统一从输出目录的 Assets/icons 读取窗口/托盘图标。</summary>
public static class AppIcons
{
    public static string IconDir => Path.Combine(AppContext.BaseDirectory, "Assets", "icons");

    /// <summary>加载指定图标文件（png/ico），失败返回 null 而不是抛异常。</summary>
    public static WindowIcon? Load(string fileName)
    {
        try
        {
            var path = Path.Combine(IconDir, fileName);
            if (File.Exists(path)) return new WindowIcon(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Icon] {fileName} 加载失败: {ex.Message}");
        }
        return null;
    }
}
