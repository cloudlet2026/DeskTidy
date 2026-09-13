using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DeskTidy.Core;

/// <summary>提取文件关联图标并转为 WPF 可用的 BitmapSource（优先 256×256 大图，避免拉伸模糊）</summary>
public static class IconHelper
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [ComImport, Guid("46EB5926-582E-4017-9FDF-E899DAA0019F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr hIcon);
        [PreserveSig] int GetImageInfo(int i, out int pImageInfo);
        [PreserveSig] int ImageList_Draw(int i, int hidc, int x, int y, int fStyle);
    }

    private static readonly Guid IID_IImageList = new(IIDStr);
    private const string IIDStr = "46EB5926-582E-4017-9FDF-E899DAA0019F";
    private const int SHIL_JUMBO = 4; // 256×256
    private const int ILD_TRANSPARENT = 0x00000001;

    public static BitmapSource? GetFileIcon(string path, bool isFolder)
    {
        try
        {
            uint attr = isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            var shfi = new SHFILEINFO();
            IntPtr res = SHGetFileInfo(path, attr, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES);
            if (res == IntPtr.Zero) return null;

            // 优先尝试 256×256 大图
            var g = IID_IImageList;
            if (SHGetImageList(SHIL_JUMBO, ref g, out var iml) == 0)
            {
                IntPtr hIcon = IntPtr.Zero;
                int hr = iml.GetIcon(shfi.iIcon, ILD_TRANSPARENT, out hIcon);
                if (hr == 0 && hIcon != IntPtr.Zero)
                {
                    try
                    {
                        return Imaging.CreateBitmapSourceFromHIcon(
                            hIcon, Int32Rect.Empty,
                            BitmapSizeOptions.FromWidthAndHeight(256, 256));
                    }
                    finally { DestroyIcon(hIcon); }
                }
            }

            // 降级：用 SHGFI_ICON 拿 32×32 大图标
            var shfi2 = new SHFILEINFO();
            SHGetFileInfo(path, attr, ref shfi2, (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
            if (shfi2.hIcon != IntPtr.Zero)
            {
                try
                {
                    return Imaging.CreateBitmapSourceFromHIcon(
                        shfi2.hIcon, Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(48, 48));
                }
                finally { DestroyIcon(shfi2.hIcon); }
            }
        }
        catch { }
        return null;
    }
}
