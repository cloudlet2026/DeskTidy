using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskTidy.Interop;

/// <summary>枚举到的一个桌面图标</summary>
public sealed class DesktopIcon
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>
/// 跨进程读取桌面 SysListView32，实现"原位收纳"：
/// 不移动文件，只把桌面图标移到屏幕外隐藏，记住原位置以便还原。
/// </summary>
public static class DesktopIconManager
{
    // LVM 消息
    private const int LVM_FIRST            = 0x1000;
    private const int LVM_GETITEMCOUNT     = LVM_FIRST + 4;
    private const int LVM_GETITEMPOSITION  = LVM_FIRST + 165;
    private const int LVM_SETITEMPOSITION  = LVM_FIRST + 15;
    private const int LVM_GETITEMTEXTW    = LVM_FIRST + 115;
    private const int LVM_ARRANGE         = LVM_FIRST + 21;
    private const int LVA_SNAPTOGRID     = 0x0005;

    private const uint LVIF_TEXT = 0x0001;

    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_READ      = 0x0010;
    private const uint PROCESS_VM_WRITE     = 0x0020;
    private const uint MEM_COMMIT  = 0x1000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LVITEMW
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
        public int iIndent;
        public int iGroupId;
        public uint cColumns;
        public IntPtr puColumns;
        public IntPtr piColFmt;
        public int iGroup;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? cls, string? win);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? win);
    [DllImport("user32.dll")]
    private static extern uint SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, uint size, uint type, uint protect);
    [DllImport("kernel32.dll")]
    private static extern bool VirtualFreeEx(IntPtr proc, IntPtr addr, uint size, uint type);
    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(IntPtr proc, IntPtr addr, [Out] byte[] buf, uint size, out uint read);
    [DllImport("kernel32.dll")]
    private static extern bool WriteProcessMemory(IntPtr proc, IntPtr addr, [In] byte[] buf, uint size, out uint written);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>找到桌面 SysListView32 句柄</summary>
    public static IntPtr GetDesktopListView()
    {
        var progman = FindWindow("Progman", null);
        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 2, 1000, out _);
            var worker = IntPtr.Zero;
            while (true)
            {
                worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
                if (worker == IntPtr.Zero) break;
                defView = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero) break;
            }
        }
        if (defView == IntPtr.Zero) return IntPtr.Zero;
        return FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    /// <summary>枚举桌面所有图标（只读）</summary>
    public static List<DesktopIcon> EnumIcons()
    {
        var result = new List<DesktopIcon>();
        var hList = GetDesktopListView();
        if (hList == IntPtr.Zero) return result;

        GetWindowThreadProcessId(hList, out uint pid);
        var proc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (proc == IntPtr.Zero) return result;

        try
        {
            int count = (int)SendMessage(hList, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
            int itemSize = Marshal.SizeOf<LVITEMW>();
            int textBytes = 520; // 260 chars * 2

            for (int i = 0; i < count; i++)
            {
                try
                {
                    var remoteItem = VirtualAllocEx(proc, IntPtr.Zero, (uint)itemSize, MEM_COMMIT, PAGE_READWRITE);
                    var remoteText = VirtualAllocEx(proc, IntPtr.Zero, (uint)textBytes, MEM_COMMIT, PAGE_READWRITE);
                    var remotePoint = VirtualAllocEx(proc, IntPtr.Zero, 8, MEM_COMMIT, PAGE_READWRITE);

                    var lvi = new LVITEMW
                    {
                        mask = LVIF_TEXT,
                        iItem = i,
                        iSubItem = 0,
                        pszText = remoteText,
                        cchTextMax = 260
                    };
                    var itemBytes = StructToBytes(lvi);
                    WriteProcessMemory(proc, remoteItem, itemBytes, (uint)itemSize, out _);

                    SendMessage(hList, LVM_GETITEMTEXTW, (IntPtr)i, remoteItem);

                    var textBuf = new byte[textBytes];
                    ReadProcessMemory(proc, remoteText, textBuf, (uint)textBytes, out _);
                    string name = Encoding.Unicode.GetString(textBuf).TrimEnd('\0');

                    SendMessage(hList, LVM_GETITEMPOSITION, (IntPtr)i, remotePoint);
                    var ptBuf = new byte[8];
                    ReadProcessMemory(proc, remotePoint, ptBuf, 8, out _);
                    var pt = BytesToPoint(ptBuf);

                    if (!string.IsNullOrEmpty(name))
                        result.Add(new DesktopIcon { Index = i, Name = name, X = pt.X, Y = pt.Y });

                    VirtualFreeEx(proc, remoteItem, 0, MEM_RELEASE);
                    VirtualFreeEx(proc, remoteText, 0, MEM_RELEASE);
                    VirtualFreeEx(proc, remotePoint, 0, MEM_RELEASE);
                }
                catch { /* 单个失败跳过 */ }
            }
        }
        finally
        {
            CloseHandle(proc);
        }
        return result;
    }

    /// <summary>把第 i 个图标移到屏幕外（隐藏）</summary>
    public static void HideIcon(IntPtr hList, int index)
    {
        // 坐标 -32000 的 ushort 位模式 = 33536 (0x8300)
        const ushort offscreen = 33536;
        int lparam = offscreen | (offscreen << 16);
        SendMessage(hList, LVM_SETITEMPOSITION, (IntPtr)index, (IntPtr)lparam);
    }

    /// <summary>把第 index 个图标恢复到 (x,y)</summary>
    public static void RestoreIcon(IntPtr hList, int index, int x, int y)
    {
        int lparam = (ushort)(short)x | ((ushort)(short)y << 16);
        SendMessage(hList, LVM_SETITEMPOSITION, (IntPtr)index, (IntPtr)lparam);
    }

    /// <summary>让系统把所有图标吸附到网格（还原后调用，弥补读不到精确坐标的问题）</summary>
    public static void ArrangeIcons(IntPtr hList)
    {
        SendMessage(hList, LVM_ARRANGE, (IntPtr)LVA_SNAPTOGRID, IntPtr.Zero);
    }

    private static byte[] StructToBytes<T>(T s) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buf = new byte[size];
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(s, ptr, false);
            Marshal.Copy(ptr, buf, 0, size);
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return buf;
    }

    private static POINT BytesToPoint(byte[] b)
    {
        return new POINT { X = BitConverter.ToInt32(b, 0), Y = BitConverter.ToInt32(b, 4) };
    }
}
