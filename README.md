# DeskTidy —— 轻量桌面整理工具

基于 **.NET 8 + C# / WPF** 的 Windows 桌面整理工具，思路类似酷呆桌面 / 腾讯桌面整理：在桌面上放置若干半透明「格子」面板，把散落的桌面文件拖进格子收纳，让桌面保持整洁。格子是常驻桌面的组件，不是普通应用窗口。

## 功能特性

- **亚克力毛玻璃格子**：通过 `SetWindowCompositionAttribute` 启用系统亚克力模糊，浅色半透明，背后壁纸隐约可见。
- **手动收纳**：不自动整理。把文件 / 文件夹从桌面或资源管理器**拖进格子**即物理移动到格子目录；同名文件自动跳过。
- **格子拖动与磁吸**：拖动标题栏移动格子，靠近屏幕边缘或其他格子时自动吸附，统一保留 16px 间隙；松手后对齐 16px 桌面网格。
- **八向调整大小**：四边 / 四角拖拽改变格子尺寸，最小 210×160，位置和大小持久化。
- **两种视图**：大图标网格视图 / 紧凑列表视图，右键一键切换。
- **映射文件夹**：把任意本地文件夹映射进格子实时显示其内容；删除映射格子时还原为空收纳格，不改动外部文件夹。
- **双击打开**：格子内图标双击即用系统默认程序打开，右键可定位、重命名、删除（进回收站）。
- **删除格子还原**：删除普通收纳格时，其中文件自动移回桌面。
- **常驻桌面**：Win+D / 显示桌面后格子自动回到桌面层；系统托盘管理，支持开机自启、单实例运行。
- **快捷方式**：`.lnk` 文件在格子中不显示后缀，与 Windows 桌面一致。

## 技术栈

| 项 | 选择 |
|---|---|
| 框架 | .NET 8 (`net8.0-windows`) |
| UI | WPF（无边框分层窗口、亚克力毛玻璃） |
| 托盘 | WinForms `NotifyIcon` |
| 互操作 | P/Invoke：亚克力、窗口层级、桌面状态检测、文件关联图标提取 |
| 配置 | `System.Text.Json`，存于 `%APPDATA%\DeskTidy\settings.json` |
| 数据 | 普通格子目录 `%APPDATA%\DeskTidy\Boxes\<格子名>\`，映射格子指向外部文件夹 |

## 目录结构

```
DeskTidy/
├── DeskTidy.sln
├── docs/                        # README 效果图
├── installer/
│   └── DeskTidy.wxs             # WiX 安装包定义（MSI）
└── DeskTidy/
    ├── DeskTidy.csproj
    ├── app.manifest
    ├── App.xaml / App.xaml.cs   # 单实例 + 托盘 + 桌面状态监测
    ├── Core/
    │   ├── Models.cs            # BoxData / AppSettings
    │   ├── SettingsService.cs   # JSON 配置读写
    │   ├── FileClassifier.cs    # 扩展名分类
    │   ├── ShellTweaks.cs       # Shell 微调
    │   ├── StartupHelper.cs     # 开机自启注册表
    │   └── IconHelper.cs        # 文件关联图标提取
    ├── Interop/
    │   ├── NativeMethods.cs
    │   └── DesktopIconManager.cs
    └── Views/
        ├── FolderBoxWindow.xaml(.cs)  # 格子面板（拖动/缩放/磁吸/视图/拖放）
        └── InputDialog.xaml(.cs)      # 轻量重命名输入框
```

## 编译与运行

前提：安装 .NET 8 SDK。

```powershell
# 编译
dotnet build DeskTidy.sln -c Release

# 发布单文件 exe（框架依赖，目标机需 .NET 8 Desktop Runtime）
dotnet publish DeskTidy\DeskTidy.csproj -c Release -r win-x64 `
    --self-contained false -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o publish
```

产物为 `publish\DeskTidy.exe`，双击运行，图标驻留系统托盘。

## 使用说明

1. 右键托盘图标 →「新建格子」创建一个收纳格。
2. 把桌面文件直接拖进格子即完成收纳（物理移动文件）。
3. 拖动标题栏移动格子，靠近其他格子 / 屏幕边缘会磁吸对齐。
4. 拖格子四边 / 四角调整大小；右键格子空白处切换大图标 / 列表视图。
5. 右键格子 →「映射到文件夹」可让格子实时显示某个文件夹内容。
6. 右键格子 →「删除格子」会把其中文件移回桌面后移除面板。
7. 双击格子内图标打开文件；右键图标可定位、重命名、删除。

## 后续可扩展方向

- 屏幕边缘停靠自动收起 / 展开
- 多显示器独立布局
- 格子内搜索、排序、按类型筛选
- 数字签名与自包含安装包
