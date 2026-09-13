using System;
using System.Collections.Generic;

namespace DeskTidy.Core;

/// <summary>格子中的一个条目（指向真实文件/文件夹）</summary>
public sealed class BoxItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsFolder { get; set; }
}

/// <summary>一个格子（收纳盒）的持久化数据</summary>
public sealed class BoxData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新建格子";
    /// <summary>格子对应的物理文件夹，格子内容即该文件夹内容</summary>
    public string FolderPath { get; set; } = "";
    /// <summary>是否为映射模式（指向用户任意文件夹，删除格子不移动文件）</summary>
    public bool IsMapped { get; set; }
    public double X { get; set; } = 100;
    public double Y { get; set; } = 100;
    public double Width { get; set; } = 240;
    public double Height { get; set; } = 320;
}

/// <summary>一个被原位收纳的桌面图标快照（用于还原）</summary>
public sealed class HiddenIconSnapshot
{
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>应用整体设置</summary>
public sealed class AppSettings
{
    public List<BoxData> Boxes { get; set; } = new();
    public bool AutoStart { get; set; } = false;
    /// <summary>整理时是否隐藏系统桌面图标（由本工具接管桌面）</summary>
    public bool HideDesktopIcons { get; set; } = false;
    /// <summary>深色模式（真 Mica 深色 + 深色配色）</summary>
    public bool DarkMode { get; set; } = false;
    /// <summary>格子使用圆角还是直角</summary>
    public bool RoundedCorners { get; set; } = true;
    /// <summary>去掉快捷方式图标的叠加小箭头（写注册表 Shell Icons\29）</summary>
    public bool HideShortcutArrow { get; set; } = false;
    public double WindowOpacity { get; set; } = 0.92;
    /// <summary>原位收纳时被移到屏幕外的桌面图标快照</summary>
    public List<HiddenIconSnapshot> HiddenIcons { get; set; } = new();
}
