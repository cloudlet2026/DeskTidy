using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DeskTidy.Core;

/// <summary>Shell 相关注册表微调（快捷方式小箭头等）。</summary>
public static class ShellTweaks
{
    private const string ShellIconsSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";
    private const string ArrowValueName = "29";

    /// <summary>去掉箭头用的"空白图标"资源（imageres.dll 内索引 197 是透明图标）。</summary>
    private static string BlankIcon =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "imageres.dll") + ",197";

    /// <summary>是否已去掉快捷方式小箭头（HKLM Shell Icons\29 存在非空值即表示去掉）。</summary>
    public static bool IsShortcutArrowHidden()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ShellIconsSubKey, false);
            return key?.GetValue(ArrowValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>设置快捷方式小箭头显示/隐藏：写 HKLM（提权），随后重启资源管理器 + 清图标缓存使其生效。</summary>
    public static void SetShortcutArrowHidden(bool hidden)
    {
        string regKey = @"HKLM\" + ShellIconsSubKey;
        string args = hidden
            ? $"add \"{regKey}\" /v {ArrowValueName} /t REG_SZ /d \"{BlankIcon}\" /f"
            : $"delete \"{regKey}\" /v {ArrowValueName} /f";

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = args,
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            p?.WaitForExit();
        }
        catch
        {
            // 用户取消 UAC，或提权失败：不继续重启资源管理器
            return;
        }

        RestartExplorerAndClearIconCache();
    }

    /// <summary>重启资源管理器并清空图标缓存，使 Shell 图标叠加层改动立即生效。</summary>
    private static void RestartExplorerAndClearIconCache()
    {
        try
        {
            using (var kill = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = "/f /im explorer.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                kill?.WaitForExit();
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            TryDelete(Path.Combine(localAppData, "IconCache.db"));
            var cacheDir = Path.Combine(localAppData, "Microsoft", "Windows", "Explorer");
            if (Directory.Exists(cacheDir))
            {
                foreach (var f in Directory.GetFiles(cacheDir, "iconcache*"))
                    TryDelete(f);
            }
        }
        catch { /* 清理失败不致命 */ }
        finally
        {
            try { Process.Start(new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true }); }
            catch { }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
