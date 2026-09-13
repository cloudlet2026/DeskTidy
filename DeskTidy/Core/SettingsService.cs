using System;
using System.IO;
using System.Text.Json;

namespace DeskTidy.Core;

/// <summary>负责设置文件的加载与保存（%APPDATA%\DeskTidy\settings.json）</summary>
public static class SettingsService
{
    private static readonly string RootDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskTidy");

    private static readonly string BoxesRoot = Path.Combine(RootDir, "Boxes");
    private static readonly string SettingsFile = Path.Combine(RootDir, "settings.json");

    public static string BoxesDirectory => BoxesRoot;

    /// <summary>系统桌面文件夹路径（可能被重定向到其他盘，如 E:\Users\...\Desktop）。</summary>
    public static string DesktopPath =>
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    /// <summary>判断某路径是否就是桌面文件夹本身。</summary>
    public static bool IsDesktopPath(string path)
    {
        try
        {
            return !string.IsNullOrEmpty(path) &&
                   string.Equals(Path.GetFullPath(path), Path.GetFullPath(DesktopPath),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(BoxesRoot);
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null) return s;
            }
        }
        catch
        {
            // 设置损坏时回退到默认
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        EnsureDirectories();
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, JsonOpts));
    }

    /// <summary>为某个分类创建（如不存在）一个格子文件夹</summary>
    public static string GetOrCreateBoxFolder(string boxName)
    {
        EnsureDirectories();
        // 文件名安全化
        var safe = string.Concat(boxName.Split(Path.GetInvalidFileNameChars()));
        var dir = Path.Combine(BoxesRoot, safe);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
