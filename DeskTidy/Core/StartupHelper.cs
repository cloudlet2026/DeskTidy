using System;
using Microsoft.Win32;

namespace DeskTidy.Core;

/// <summary>开机自启：写入 HKCU\...\CurrentVersion\Run</summary>
public static class StartupHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "DeskTidy";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(AppName) != null;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey)!;
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? "";
            key.SetValue(AppName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }
}
