using System;
using System.Runtime.InteropServices;

namespace DeskTidy.Interop;

/// <summary>Win32 / Shell 相关的 P/Invoke 声明</summary>
internal static class NativeMethods
{
    [DllImport("shell32.dll")]
    internal static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    public const int SHCNE_ASSOCCHANGED = 0x08000000;
    public const int SHCNF_IDLIST = 0x0000;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 切换/设置桌面图标显隐。
    /// 通过向 SHELLDLL_DefView 发送 WM_COMMAND(0x7402) 实现，
    /// 这是系统自带的"查看 -> 显示桌面图标"命令。
    /// </summary>
    public static void SetDesktopIconsVisible(bool visible)
    {
        var progman = FindWindow("Progman", null);
        IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);

        // Win10/11 下图标可能挂在 WorkerW 上，做一次探测
        if (defView == IntPtr.Zero)
        {
            // 触发 WorkerW 链
            SendMessageTimeout(progman, 0x052C, new IntPtr(0), IntPtr.Zero, 2, 1000, out _);
            var workerW = IntPtr.Zero;
            while (true)
            {
                workerW = FindWindowEx(IntPtr.Zero, workerW, "WorkerW", null);
                if (workerW == IntPtr.Zero) break;
                defView = FindWindowEx(workerW, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero) break;
            }
        }

        if (defView != IntPtr.Zero)
        {
            // 0x7402 = 切换"显示桌面图标"。这里只能切换，
            // 所以我们先读取当前状态，再按目标决定是否切换。
            bool currentlyVisible = IsDesktopIconsVisible();
            if (currentlyVisible != visible)
            {
                PostMessage(defView, 0x0111, (IntPtr)0x7402, IntPtr.Zero);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    /// <summary>读取当前桌面图标是否显示（注册表 HideIcons）。</summary>
    public static bool IsDesktopIconsVisible()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            var v = key?.GetValue("HideIcons");
            if (v is int i) return i == 0;
        }
        catch { /* 忽略 */ }
        return true;
    }
}
