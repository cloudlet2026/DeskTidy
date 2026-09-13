using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskTidy.Core;
using DeskTidy.Interop;
using DeskTidy.Views;
using WinForms = System.Windows.Forms;

namespace DeskTidy;

public partial class App : Application
{
    public static App Instance => (App)Current;

    private static Mutex? _singleInstance;
    private WinForms.NotifyIcon? _notify;
    private AppSettings _settings = new();
    private readonly List<FolderBoxWindow> _boxWindows = new();
    private DispatcherTimer? _desktopStateTimer;
    private bool _desktopWasActive;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] className, int maxCount);

    /// <summary>给外部（格子窗口）异步保存设置用</summary>
    public static Task SaveSettingsAsync()
    {
        SettingsService.Save(Instance._settings);
        return Task.CompletedTask;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例
        _singleInstance = new Mutex(true, @"Local\DeskTidy_SingleInstance_8F3A2B1C-1A2B-4C3D-9E5F-1A2B3C4D5E6F", out bool created);
        if (!created)
        {
            MessageBox.Show("DeskTidy 已经在运行了（看系统托盘）。", "DeskTidy");
            Shutdown();
            return;
        }

        // 托盘常驻程序：所有格子窗口都关闭后也不退出，必须显式退出
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        SettingsService.EnsureDirectories();
        _settings = SettingsService.Load();

        // 开机自启以注册表为唯一事实来源，纠正设置与注册表不一致的情况
        _settings.AutoStart = StartupHelper.IsEnabled();
        SettingsService.Save(_settings);

        InitNotifyIcon();

        // 运行即自动整理：确保「桌面整理」映射格子存在，并把桌面文件标记为隐藏（收纳进格子）
        EnsureTidyBox();
        CollectDesktopFiles();
        SettingsService.Save(_settings);

        RestoreBoxes();

        // 应用持久化的深浅配色
        ApplyTheme(_settings.DarkMode);

        // 若用户此前勾选了"隐藏系统桌面图标"，恢复该状态
        if (_settings.HideDesktopIcons)
        {
            NativeMethods.SetDesktopIconsVisible(false);
        }

        StartDesktopStateMonitor();

        base.OnStartup(e);
    }

    // ---------------- 托盘 ----------------
    private void InitNotifyIcon()
    {
        _notify = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "DeskTidy 桌面整理"
        };

        var menu = new WinForms.ContextMenuStrip();
        var miTidy = new WinForms.ToolStripMenuItem("立即整理桌面");
        miTidy.Click += async (_, _) => await TidyDesktopAsync();
        var miNew = new WinForms.ToolStripMenuItem("新建空白格子");
        miNew.Click += async (_, _) => await NewBoxAsync();
        var miAuto = new WinForms.ToolStripMenuItem("开机自动启动") { Checked = _settings.AutoStart };
        miAuto.Click += (_, _) =>
        {
            miAuto.Checked = !miAuto.Checked;
            StartupHelper.SetEnabled(miAuto.Checked);
            _settings.AutoStart = miAuto.Checked;
            SettingsService.Save(_settings);
        };

        // 隐藏/显示系统桌面图标：点击切换，菜单文字随状态变化
        var miHideIcons = new WinForms.ToolStripMenuItem();
        miHideIcons.Click += (_, _) => ToggleDesktopIcons(miHideIcons);

        // 去掉快捷方式小箭头（写注册表，可能需要管理员权限）
        var miArrow = new WinForms.ToolStripMenuItem("去掉快捷方式小箭头")
        {
            Checked = ShellTweaks.IsShortcutArrowHidden()
        };
        miArrow.Click += (_, _) =>
        {
            miArrow.Checked = !miArrow.Checked;
            ShellTweaks.SetShortcutArrowHidden(miArrow.Checked);
            _settings.HideShortcutArrow = miArrow.Checked;
            SettingsService.Save(_settings);
        };

        var miDark = new WinForms.ToolStripMenuItem("深色模式") { Checked = _settings.DarkMode };
        miDark.Click += (_, _) =>
        {
            miDark.Checked = !miDark.Checked;
            _settings.DarkMode = miDark.Checked;
            SettingsService.Save(_settings);
            ApplyTheme(_settings.DarkMode);
        };
        var miRounded = new WinForms.ToolStripMenuItem("圆角") { Checked = _settings.RoundedCorners };
        miRounded.Click += (_, _) =>
        {
            miRounded.Checked = !miRounded.Checked;
            _settings.RoundedCorners = miRounded.Checked;
            SettingsService.Save(_settings);
            ApplyCornerRadius(_settings.RoundedCorners);
        };
        var miQuit = new WinForms.ToolStripMenuItem("退出 DeskTidy");
        miQuit.Click += (_, _) => Shutdown();

        menu.Items.AddRange(new WinForms.ToolStripItem[]
        {
            miTidy, miNew, new WinForms.ToolStripSeparator(),
            miAuto, miHideIcons, miArrow, miDark, miRounded, new WinForms.ToolStripSeparator(),
            miQuit
        });

        // 每次打开菜单时按实际状态刷新文字
        menu.Opening += (_, _) =>
        {
            miHideIcons.Text = NativeMethods.IsDesktopIconsVisible() ? "隐藏系统桌面图标" : "显示系统桌面图标";
            miArrow.Checked = ShellTweaks.IsShortcutArrowHidden();
        };
        UpdateIconMenuText(miHideIcons);

        _notify.ContextMenuStrip = menu;
        _notify.DoubleClick += (_, _) => ToggleAllBoxes();
    }

    private void UpdateIconMenuText(WinForms.ToolStripMenuItem item)
    {
        item.Text = NativeMethods.IsDesktopIconsVisible() ? "隐藏系统桌面图标" : "显示系统桌面图标";
    }

    private void ToggleDesktopIcons(WinForms.ToolStripMenuItem item)
    {
        bool visible = NativeMethods.IsDesktopIconsVisible();
        NativeMethods.SetDesktopIconsVisible(!visible);
        _settings.HideDesktopIcons = !visible;
        SettingsService.Save(_settings);
        UpdateIconMenuText(item);
    }

    public AppSettings Settings => _settings;

    private void ToggleAllBoxes()
    {
        bool anyVisible = _boxWindows.Any(w => w.IsVisible);
        foreach (var w in _boxWindows)
        {
            if (anyVisible)
            {
                w.SetUserHidden(true);
                w.Hide();
            }
            else
            {
                w.SetUserHidden(false);
                w.Show();
            }
        }
    }

    /// <summary>切换深浅配色：整体替换全局主题画刷（DynamicResource 自动刷新）。</summary>
    private void ApplyTheme(bool dark)
    {
        var res = Current.Resources;
        if (dark)
        {
            res["CardBrush"] = new SolidColorBrush(Color.FromArgb(0xE6, 0x21, 0x21, 0x21));
            res["TextBrush"] = new SolidColorBrush(Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2));
            res["SecondaryTextBrush"] = new SolidColorBrush(Color.FromArgb(0xFF, 0xBB, 0xBB, 0xBB));
            res["DividerBrush"] = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            res["ItemHoverBrush"] = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            res["ItemSelectedBrush"] = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            res["ButtonForegroundBrush"] = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
            res["ButtonHoverBrush"] = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            res["ButtonPressedBrush"] = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            res["CardBrush"] = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
            res["TextBrush"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1B));
            res["SecondaryTextBrush"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x44, 0x44, 0x44));
            res["DividerBrush"] = new SolidColorBrush(Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
            res["ItemHoverBrush"] = new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x00, 0x00));
            res["ItemSelectedBrush"] = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00));
            res["ButtonForegroundBrush"] = new SolidColorBrush(Color.FromArgb(0x99, 0x11, 0x11, 0x11));
            res["ButtonHoverBrush"] = new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x00, 0x00));
            res["ButtonPressedBrush"] = new SolidColorBrush(Color.FromArgb(0x26, 0x00, 0x00, 0x00));
        }
    }

    private void ApplyCornerRadius(bool rounded)
    {
        foreach (var w in _boxWindows) w.SetCornerRadius(rounded);
    }

    private void StartDesktopStateMonitor()
    {
        _desktopStateTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
            (_, _) => UpdateDesktopPresentation(), Dispatcher);
        _desktopStateTimer.Start();
    }

    private void UpdateDesktopPresentation()
    {
        var desktopActive = IsDesktopForeground();
        var visibleBoxes = _boxWindows.Where(w => !w.IsUserHidden).ToList();

        if (!desktopActive)
        {
            // 只要前台不是桌面，就持续强制 NOTOPMOST，避免残留置顶
            foreach (var box in visibleBoxes)
                box.SetDesktopTopmost(false);
            _desktopWasActive = false;
            return;
        }

        if (desktopActive && !_desktopWasActive)
        {
            // 刚进入桌面：先恢复显示，再提升到桌面层
            foreach (var box in visibleBoxes)
                box.ShowForDesktop();
        }
        // 桌面在前台时，持续保持 TOPMOST
        foreach (var box in visibleBoxes)
            box.SetDesktopTopmost(true);

        _desktopWasActive = true;
    }

    private static bool IsDesktopForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        var className = new char[128];
        var length = GetClassName(foreground, className, className.Length);
        var name = length > 0 ? new string(className, 0, length) : string.Empty;
        return name.Equals("Progman", StringComparison.Ordinal) ||
               name.Equals("WorkerW", StringComparison.Ordinal) ||
               name.Equals("SHELLDLL_DefView", StringComparison.Ordinal);
    }

    // ---------------- 格子管理 ----------------
    public System.Collections.Generic.IEnumerable<System.Windows.Rect> GetOtherBoxRects(FolderBoxWindow self)
    {
        foreach (var w in _boxWindows)
        {
            if (ReferenceEquals(w, self)) continue;
            yield return new System.Windows.Rect(w.Left, w.Top, w.Width, w.Height);
        }
    }

    private void RestoreBoxes()
    {
        // 清理旧版虚拟格子等孤儿数据（FolderPath 为空且非映射的格子没有意义）
        _settings.Boxes.RemoveAll(b => string.IsNullOrEmpty(b.FolderPath) && !b.IsMapped);
        foreach (var box in _settings.Boxes)
        {
            OpenBoxWindow(box);
        }
    }

    private FolderBoxWindow OpenBoxWindow(BoxData box)
    {
        var win = new FolderBoxWindow();
        win.LoadBox(box);
        win.Closed += (_, _) => _boxWindows.Remove(win);
        win.Show();
        _boxWindows.Add(win);
        return win;
    }

    public async Task RemoveBoxAsync(string boxId)
    {
        var box = _settings.Boxes.FirstOrDefault(b => b.Id == boxId);
        if (box != null) _settings.Boxes.Remove(box);
        await SaveSettingsAsync();
    }

    public async Task NewBoxAsync()
    {
        var name = InputDialog.Show("新建格子", "格子名称：", "新建格子");
        if (string.IsNullOrWhiteSpace(name)) return;

        var folder = SettingsService.GetOrCreateBoxFolder(name);
        var position = FindAvailablePosition(GuessNextBoxX(), 120, 240, 320,
            _boxWindows.Select(w => new Rect(w.Left, w.Top, w.Width, w.Height)));
        var box = new BoxData
        {
            Name = name,
            FolderPath = folder,
            X = position.X,
            Y = position.Y,
            Width = 240,
            Height = 320
        };
        _settings.Boxes.Add(box);
        OpenBoxWindow(box);
        await SaveSettingsAsync();
    }

    private double GuessNextBoxX()
    {
        var workArea = SystemParameters.WorkArea;
        // 默认排在屏幕右侧
        return workArea.Right - 260;
    }

    private static Point FindAvailablePosition(
        double preferredX,
        double preferredY,
        double width,
        double height,
        IEnumerable<Rect> occupied)
    {
        var work = SystemParameters.WorkArea;
        var start = new Point(
            Math.Clamp(preferredX, work.Left, work.Right - width),
            Math.Clamp(preferredY, work.Top, work.Bottom - height));
        var occupiedRects = occupied.ToArray();
        bool IsAvailable(double x, double y) =>
            !occupiedRects.Any(r => r.IntersectsWith(new Rect(x, y, width, height)));

        if (IsAvailable(start.X, start.Y)) return start;

        var xValues = Enumerable.Range(0, Math.Max(1, (int)Math.Ceiling((work.Width - width) / 16) + 1))
            .Select(i => Math.Clamp(Math.Ceiling(work.Left / 16) * 16 + i * 16,
                work.Left, work.Right - width));
        var yValues = Enumerable.Range(0, Math.Max(1, (int)Math.Ceiling((work.Height - height) / 16) + 1))
            .Select(i => Math.Clamp(Math.Ceiling(work.Top / 16) * 16 + i * 16,
                work.Top, work.Bottom - height));

        var candidate = from y in yValues
                        from x in xValues
                        let rect = new Rect(x, y, width, height)
                        where !occupiedRects.Any(r => r.IntersectsWith(rect))
                        orderby Math.Abs(x - start.X) + Math.Abs(y - start.Y)
                        select new Point(x, y);
        return candidate.FirstOrDefault(start);
    }

    // ---------------- 一键整理桌面（映射到格子，不移动/复制文件） ----------------
    /// <summary>确保「桌面整理」映射格子存在（不移动文件），返回是否新建了格子。</summary>
    private bool EnsureTidyBox()
    {
        string desktop = SettingsService.DesktopPath;
        if (!Directory.Exists(desktop)) return false;
        if (_settings.Boxes.Any(b => b.IsMapped && SettingsService.IsDesktopPath(b.FolderPath)))
            return false;

        var position = FindAvailablePosition(GuessNextBoxX(), 100, 320, 440,
            _settings.Boxes.Select(b => new Rect(b.X, b.Y, b.Width, b.Height)));
        _settings.Boxes.Add(new BoxData
        {
            Name = "桌面整理",
            IsMapped = true,
            FolderPath = desktop,
            X = position.X,
            Y = position.Y,
            Width = 320,
            Height = 440
        });
        return true;
    }

    /// <summary>把桌面上的文件/文件夹标记为"隐藏"（收纳进格子，不移动文件）。</summary>
    private void CollectDesktopFiles()
    {
        string desktop = SettingsService.DesktopPath;
        if (!Directory.Exists(desktop)) return;
        foreach (var path in Directory.GetFileSystemEntries(desktop))
        {
            try
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.System) != 0) continue; // 跳过 desktop.ini 等系统项
                if ((attrs & FileAttributes.Hidden) == 0)
                    File.SetAttributes(path, attrs | FileAttributes.Hidden);
            }
            catch { /* 被占用则跳过 */ }
        }
    }

    /// <summary>取消"隐藏"标记，把被收纳的文件恢复到桌面显示。</summary>
    public void RestoreDesktopFiles()
    {
        string desktop = SettingsService.DesktopPath;
        if (!Directory.Exists(desktop)) return;
        foreach (var path in Directory.GetFileSystemEntries(desktop))
        {
            try
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.System) != 0) continue;
                if ((attrs & FileAttributes.Hidden) != 0)
                    File.SetAttributes(path, attrs & ~FileAttributes.Hidden);
            }
            catch { }
        }
    }

    public async Task TidyDesktopAsync()
    {
        try
        {
            bool created = EnsureTidyBox();

            // 把桌面文件标记为隐藏（收纳进格子），不移动、不复制，也不隐藏整个桌面
            CollectDesktopFiles();

            await SaveSettingsAsync();

            // 新建格子时才重建窗口；已有的映射格子本就是实时视图
            if (created) RebuildBoxWindows();
            else
            {
                foreach (var w in _boxWindows) w.RefreshItems();
            }

            MessageBox.Show("桌面文件已收纳进「桌面整理」格子（未移动/复制文件），这些文件已从桌面隐藏（其他图标保留）。",
                "DeskTidy", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"整理失败：{ex.Message}", "DeskTidy",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RebuildBoxWindows()
    {
        foreach (var w in _boxWindows.ToList())
        {
            w.Close();
        }
        _boxWindows.Clear();

        foreach (var box in _settings.Boxes)
        {
            OpenBoxWindow(box);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 退出时把被收纳（隐藏）的桌面文件恢复显示，避免用户找不到文件
        try { RestoreDesktopFiles(); } catch { }

        // 若勾选了"隐藏系统桌面图标"，退出时恢复显示
        if (_settings.HideDesktopIcons)
        {
            try { NativeMethods.SetDesktopIconsVisible(true); } catch { }
        }
        SettingsService.Save(_settings);
        if (_notify != null) _notify.Visible = false;
        _notify?.Dispose();
        base.OnExit(e);
    }
}
