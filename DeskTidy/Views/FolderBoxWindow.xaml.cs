using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskTidy.Core;
using VBIO = Microsoft.VisualBasic.FileIO;

namespace DeskTidy.Views;

/// <summary>列表项视图模型</summary>
public sealed class ItemVm
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsFolder { get; set; }
    public string Category { get; set; } = "";
    public BitmapSource? Icon { get; set; }
}

public partial class FolderBoxWindow : Window
{
    public BoxData Box { get; private set; } = new();
    private readonly ObservableCollection<ItemVm> _items = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    private const double GridSize = 16;
    private const double Gap = 16;
    private const double SnapDist = 10;
    private const double MinW = 210, MinH = 160;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_WINDOWEDGE = 0x00000100;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int n, int dw);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint GW_OWNER = 4;

    public FolderBoxWindow()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _items;
        SizeChanged += (_, __) => { Box.Width = Width; Box.Height = Height; };
        StateChanged += (_, __) => { if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; };
        ApplyIconView();
    }

    private bool _isListView;
    private string _currentCategory = "";

    private void ApplyIconView()
    {
        ItemsList.ItemsPanel = (ItemsPanelTemplate)FindResource("IconPanel");
        ItemsList.ItemTemplate = (DataTemplate)FindResource("IconTemplate");
    }
    private void ApplyListView()
    {
        ItemsList.ItemsPanel = (ItemsPanelTemplate)FindResource("ListPanel");
        ItemsList.ItemTemplate = (DataTemplate)FindResource("ListTemplate");
    }
    private void ToggleView_Click(object sender, RoutedEventArgs e)
    {
        _isListView = !_isListView;
        if (_isListView) ApplyListView(); else ApplyIconView();
    }

    private void CatTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string cat)
        {
            _currentCategory = cat;
            RefreshItems();
        }
    }

    /// <summary>右上角 ✕：隐藏该面板（托盘双击可再显示全部格子）</summary>
    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        SetUserHidden(true);
        Hide();
    }

    private bool _allowUserHide;
    private double _dpi = 1.0;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var src = HwndSource.FromHwnd(hwnd);
        _dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        src?.AddHook(WndProc);
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex &= ~WS_EX_WINDOWEDGE;
        ex |= WS_EX_TOOLWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    /// <summary>切换圆角/直角（圆角半径 12 / 0）。</summary>
    public void SetCornerRadius(bool rounded)
    {
        Card.CornerRadius = new CornerRadius(rounded ? 12 : 0);
    }

    public bool IsUserHidden => _allowUserHide;

    public void ShowForDesktop()
    {
        if (_allowUserHide) return;
        RefreshItems();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        ShowWindow(new WindowInteropHelper(this).Handle, SW_SHOWNOACTIVATE);
    }

    public void SetDesktopTopmost(bool topmost)
    {
        if (_allowUserHide) return;
        Topmost = topmost;
        var hwnd = new WindowInteropHelper(this).Handle;
        var zOrder = topmost ? HWND_TOPMOST : HWND_NOTOPMOST;
        var owner = GetWindow(hwnd, GW_OWNER);
        if (owner != IntPtr.Zero)
            SetWindowPos(owner, zOrder, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SetWindowPos(hwnd, zOrder, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_SHOWWINDOW = 0x0018;
    private const int WM_SIZE = 0x0005;
    private const int SIZE_MINIMIZED = 1;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    private const int SW_RESTORE = 9;
    private const int SW_SHOWNOACTIVATE = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    public void SetUserHidden(bool hidden) => _allowUserHide = hidden;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == SC_MINIMIZE)
        {
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == WM_SIZE && wParam.ToInt32() == SIZE_MINIMIZED)
        {
            ShowWindow(hwnd, SW_RESTORE);
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == WM_SHOWWINDOW && wParam == IntPtr.Zero && !_allowUserHide)
        {
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == WM_WINDOWPOSCHANGING && lParam != IntPtr.Zero && !_allowUserHide)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_HIDEWINDOW) != 0)
            {
                pos.flags &= ~SWP_HIDEWINDOW;
                pos.flags |= SWP_SHOWWINDOW;
                Marshal.StructureToPtr(pos, lParam, false);
                handled = true;
                return IntPtr.Zero;
            }
        }
        return IntPtr.Zero;
    }

    public void LoadBox(BoxData box)
    {
        Box = box;
        TitleText.Text = box.Name;
        Width = box.Width;
        Height = box.Height;
        var work = SystemParameters.WorkArea;
        Left = Math.Clamp(box.X, work.Left + Gap, work.Right - Width - Gap);
        Top = Math.Clamp(box.Y, work.Top + Gap, work.Bottom - Height - Gap);

        // 应用圆角/直角
        SetCornerRadius(App.Instance.Settings.RoundedCorners);

        // 「桌面整理」格子显示分类标签栏，且默认用列表视图
        if (IsTidyBox)
        {
            TabBarHost.Visibility = Visibility.Visible;
            BuildTabs();
            _isListView = true;
            ApplyListView();
        }
        else
        {
            TabBarHost.Visibility = Visibility.Collapsed;
        }

        RefreshItems();
        StartWatch();
    }

    private bool IsTidyBox => Box.IsMapped && SettingsService.IsDesktopPath(Box.FolderPath);

    /// <summary>生成分类标签（文档/图片/视频/音乐/压缩包/程序/其他，不含"全部"）。</summary>
    private void BuildTabs()
    {
        TabBar.Children.Clear();
        _currentCategory = FileClassifier.AllBoxNames.Length > 0 ? FileClassifier.AllBoxNames[0] : "";
        bool first = true;
        foreach (var cat in FileClassifier.AllBoxNames)
        {
            var rb = new RadioButton
            {
                Content = cat,
                Tag = cat,
                GroupName = "Cat",
                Style = (Style)FindResource("TabStyle")
            };
            rb.Checked += CatTab_Checked;
            if (first)
            {
                rb.IsChecked = true;
                first = false;
            }
            TabBar.Children.Add(rb);
        }
    }

    private void StartWatch()
    {
        try
        {
            Directory.CreateDirectory(Box.FolderPath);
            _watcher = new FileSystemWatcher(Box.FolderPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Attributes,
                EnableRaisingEvents = true,
                IncludeSubdirectories = false
            };
            _watcher.Changed += OnFsChanged;
            _watcher.Created += OnFsChanged;
            _watcher.Deleted += OnFsChanged;
            _watcher.Renamed += (_, _) => OnFsChanged();

            _debounce = new Timer(_ => Dispatcher.Invoke(RefreshItems),
                null, Timeout.Infinite, Timeout.Infinite);
        }
        catch { }
    }

    private void OnFsChanged(object? sender = null, EventArgs? e = null)
    {
        _debounce?.Change(200, Timeout.Infinite);
    }

    public void RefreshItems()
    {
        _items.Clear();
        if (IsTidyBox)
        {
            RefreshTidyItems();
            return;
        }
        try
        {
            if (!Directory.Exists(Box.FolderPath)) return;
            foreach (var dir in Directory.EnumerateDirectories(Box.FolderPath)
                                         .Where(d => !IsHiddenSystem(d))
                                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                _items.Add(new ItemVm
                {
                    Name = Path.GetFileName(dir),
                    Path = dir,
                    IsFolder = true,
                    Icon = IconHelper.GetFileIcon(dir, true)
                });
            }
            foreach (var file in Directory.EnumerateFiles(Box.FolderPath)
                                          .Where(f => !IsHiddenSystem(f))
                                          .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                _items.Add(new ItemVm
                {
                    Name = DisplayNameFor(file),
                    Path = file,
                    IsFolder = false,
                    Icon = IconHelper.GetFileIcon(file, false)
                });
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    /// <summary>「桌面整理」格子：只显示被收纳（Hidden）的文件/文件夹，并按当前分类标签过滤。</summary>
    private void RefreshTidyItems()
    {
        try
        {
            if (!Directory.Exists(Box.FolderPath)) return;
            var entries = Directory.GetFileSystemEntries(Box.FolderPath)
                .Where(IsTidiedItem)
                .Where(p => string.IsNullOrEmpty(_currentCategory) ||
                            string.Equals(FileClassifier.Classify(p), _currentCategory, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
            foreach (var path in entries)
            {
                bool isFolder = Directory.Exists(path);
                _items.Add(new ItemVm
                {
                    Name = DisplayNameFor(path),
                    Path = path,
                    IsFolder = isFolder,
                    Category = FileClassifier.Classify(path),
                    Icon = IconHelper.GetFileIcon(path, isFolder)
                });
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    /// <summary>是否是被收纳进格子的条目（Hidden 且非 System）。</summary>
    private static bool IsTidiedItem(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.Hidden) != 0 && (attrs & FileAttributes.System) == 0;
        }
        catch { return false; }
    }

    /// <summary>跳过隐藏/系统文件（如 desktop.ini），避免映射桌面时冒出杂项。</summary>
    private static bool IsHiddenSystem(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch { return false; }
    }

    private static string DisplayNameFor(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return name[..^4];
        return name;
    }

    // ============ 拖动窗口 ============
    private bool _dragging;
    private Point _dragScreenStart;
    private double _winStartX, _winStartY;

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _dragging = true;
        _dragScreenStart = PointToScreen(e.GetPosition(this));
        _winStartX = Left;
        _winStartY = Top;
        HeaderBar.CaptureMouse();
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var now = PointToScreen(e.GetPosition(this));
        double dx = (now.X - _dragScreenStart.X) / _dpi;
        double dy = (now.Y - _dragScreenStart.Y) / _dpi;
        var work = SystemParameters.WorkArea;
        double x = Math.Clamp(_winStartX + dx, work.Left + Gap, work.Right - Width - Gap);
        double y = Math.Clamp(_winStartY + dy, work.Top + Gap, work.Bottom - Height - Gap);
        ApplyMagnet(ref x, ref y, Width, Height);
        Left = x; Top = y;
    }

    private void Header_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        HeaderBar.ReleaseMouseCapture();
        SnapToGrid();
    }

    /// <summary>磁吸到其他格子边界：对齐边（左/右/上/下）或相邻（固定间隙 Gap）。</summary>
    private void ApplyMagnet(ref double x, ref double y, double w, double h)
    {
        double bestDx = 0, bestDy = 0;
        double bestDistX = SnapDist, bestDistY = SnapDist;
        foreach (var r in App.Instance.GetOtherBoxRects(this))
        {
            // 本格左边缘 x 的对齐目标：左对齐 / 右对齐 / 贴到右侧(间隙) / 贴到左侧(间隙)
            double[] xCandidates = { r.Left, r.Right - w, r.Right + Gap, r.Left - w - Gap };
            foreach (var cx in xCandidates)
            {
                double d = Math.Abs(x - cx);
                if (d < bestDistX) { bestDistX = d; bestDx = cx - x; }
            }
            // 本格上边缘 y 的对齐目标：上对齐 / 下对齐 / 贴到下方(间隙) / 贴到上方(间隙)
            double[] yCandidates = { r.Top, r.Bottom - h, r.Bottom + Gap, r.Top - h - Gap };
            foreach (var cy in yCandidates)
            {
                double d = Math.Abs(y - cy);
                if (d < bestDistY) { bestDistY = d; bestDy = cy - y; }
            }
        }
        x += bestDx; y += bestDy;
    }

    private void SnapToGrid()
    {
        var work = SystemParameters.WorkArea;
        double x = Math.Round(Left / GridSize) * GridSize;
        double y = Math.Round(Top / GridSize) * GridSize;
        x = Math.Clamp(x, work.Left + Gap, work.Right - Width - Gap);
        y = Math.Clamp(y, work.Top + Gap, work.Bottom - Height - Gap);
        Left = x; Top = y;
        Box.X = Left; Box.Y = Top;
        Box.Width = Width; Box.Height = Height;
        _ = App.SaveSettingsAsync();
    }

    // ============ 四边/四角调整大小 ============
    private void ClampWithinWork()
    {
        var work = SystemParameters.WorkArea;
        if (Width < MinW) Width = MinW;
        if (Height < MinH) Height = MinH;
        if (Left < work.Left + Gap) Left = work.Left + Gap;
        if (Top < work.Top + Gap) Top = work.Top + Gap;
        if (Left + Width > work.Right - Gap) Left = work.Right - Gap - Width;
        if (Top + Height > work.Bottom - Gap) Top = work.Bottom - Gap - Height;
    }

    private void Resize_Completed(object sender, DragCompletedEventArgs e)
    {
        Box.X = Left;
        Box.Y = Top;
        Box.Width = Width;
        Box.Height = Height;
        _ = App.SaveSettingsAsync();
    }

    private void R_Right_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinW, Width + e.HorizontalChange);
        ClampWithinWork();
    }
    private void R_Bottom_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Height = Math.Max(MinH, Height + e.VerticalChange);
        ClampWithinWork();
    }
    private void R_Left_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double newW = Math.Max(MinW, Width - e.HorizontalChange);
        Left += Width - newW;
        Width = newW;
        ClampWithinWork();
    }
    private void R_Top_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double newH = Math.Max(MinH, Height - e.VerticalChange);
        Top += Height - newH;
        Height = newH;
        ClampWithinWork();
    }
    private void R_BR_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinW, Width + e.HorizontalChange);
        Height = Math.Max(MinH, Height + e.VerticalChange);
        ClampWithinWork();
    }
    private void R_TR_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinW, Width + e.HorizontalChange);
        double newH = Math.Max(MinH, Height - e.VerticalChange);
        Top += Height - newH;
        Height = newH;
        ClampWithinWork();
    }
    private void R_BL_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double newW = Math.Max(MinW, Width - e.HorizontalChange);
        Left += Width - newW;
        Width = newW;
        Height = Math.Max(MinH, Height + e.VerticalChange);
        ClampWithinWork();
    }
    private void R_TL_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double newW = Math.Max(MinW, Width - e.HorizontalChange);
        Left += Width - newW;
        Width = newW;
        double newH = Math.Max(MinH, Height - e.VerticalChange);
        Top += Height - newH;
        Height = newH;
        ClampWithinWork();
    }

    // ============ 拖出文件到桌面 / 其他格子 ============
    private Point _itemDragStart;
    private ItemVm? _itemDragCandidate;
    private bool _itemDragStarted;

    private void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _itemDragStart = e.GetPosition(ItemsList);
        _itemDragCandidate = ItemAtPoint(ItemsList, _itemDragStart);
        _itemDragStarted = false;
    }

    private void ItemsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_itemDragCandidate == null || _itemDragStarted || e.LeftButton != MouseButtonState.Pressed)
            return;
        var pos = e.GetPosition(ItemsList);
        if (Math.Abs(pos.X - _itemDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _itemDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _itemDragStarted = true;
        var item = _itemDragCandidate;
        var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
        data.SetData(DataFormats.UnicodeText, item.Path);
        try
        {
            // FileDrop 让资源管理器/桌面原生完成移动或复制；其他格子走 Window_Drop。
            DragDrop.DoDragDrop(ItemsList, data,
                DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link);
        }
        finally
        {
            _itemDragCandidate = null;
            _itemDragStarted = false;
        }
    }

    private static ItemVm? ItemAtPoint(ListBox list, Point pos)
    {
        var el = list.InputHitTest(pos) as DependencyObject;
        while (el != null && el is not ListBoxItem)
            el = VisualTreeHelper.GetParent(el);
        return (el as ListBoxItem)?.DataContext as ItemVm;
    }

    // ============ 双击打开 ============
    private void Item_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsList.SelectedItem is ItemVm vm) OpenPath(vm.Path);
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true, Verb = "open" });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开：{ex.Message}", "DeskTidy",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private ItemVm? SelectedVm => ItemsList.SelectedItem as ItemVm;

    /// <summary>右键先选中条目再弹菜单，保证条目右键菜单能拿到正确对象。</summary>
    private void ItemsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemAtPoint(ItemsList, e.GetPosition(ItemsList));
        if (item != null) ItemsList.SelectedItem = item;
    }

    private void OpenItem_Click(object sender, RoutedEventArgs e)
    {
        var vm = SelectedVm;
        if (vm != null) OpenPath(vm.Path);
    }

    private void LocateItem_Click(object sender, RoutedEventArgs e)
    {
        var vm = SelectedVm;
        if (vm == null) return;
        try { Process.Start("explorer.exe", $"/select,\"{vm.Path}\""); }
        catch { }
    }

    private void RenameItem_Click(object sender, RoutedEventArgs e)
    {
        var vm = SelectedVm;
        if (vm == null) return;
        var newName = InputDialog.Show("重命名", "新名称：", vm.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == vm.Name) return;
        try
        {
            var dir = Path.GetDirectoryName(vm.Path)!;
            string finalName;
            if (vm.IsFolder) finalName = newName;
            else
            {
                var ext = Path.GetExtension(vm.Path);
                finalName = ext.Length > 0 && !newName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                    ? newName + ext : newName;
            }
            var dest = Path.Combine(dir, finalName);
            if (vm.IsFolder) Directory.Move(vm.Path, dest);
            else File.Move(vm.Path, dest);
            RefreshItems();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"重命名失败：{ex.Message}", "DeskTidy",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        var vm = SelectedVm;
        if (vm == null) return;
        var d = MessageBox.Show($"删除「{vm.Name}」？将移入回收站。",
            "DeskTidy", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (d != MessageBoxResult.Yes) return;
        try
        {
            if (vm.IsFolder)
                VBIO.FileSystem.DeleteDirectory(vm.Path,
                    VBIO.UIOption.OnlyErrorDialogs, VBIO.RecycleOption.SendToRecycleBin);
            else
                VBIO.FileSystem.DeleteFile(vm.Path,
                    VBIO.UIOption.OnlyErrorDialogs, VBIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败：{ex.Message}", "DeskTidy",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ============ 拖放文件 ============
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        int skipped = MoveFilesToBox(files);
        RefreshItems();
        if (skipped > 0)
            MessageBox.Show($"已有 {skipped} 个同名文件在格子中，已跳过。",
                "DeskTidy", MessageBoxButton.OK, MessageBoxImage.Information);
        await App.SaveSettingsAsync();
    }

    private int MoveFilesToBox(string[] files)
    {
        Directory.CreateDirectory(Box.FolderPath);
        int skipped = 0;
        foreach (var src in files)
        {
            try
            {
                var dest = Path.Combine(Box.FolderPath, Path.GetFileName(src));
                if (string.Equals(src, dest, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(dest) || Directory.Exists(dest)) { skipped++; continue; }
                MoveEntry(src, dest);
            }
            catch { }
        }
        return skipped;
    }

    /// <summary>移动文件/文件夹；跨磁盘卷时 File.Move 会抛 IOException，改为复制后删除。</summary>
    private static void MoveEntry(string src, string dest)
    {
        if (Directory.Exists(src))
        {
            Directory.Move(src, dest);
            return;
        }
        try
        {
            File.Move(src, dest);
        }
        catch (IOException)
        {
            File.Copy(src, dest);
            File.Delete(src);
        }
    }

    // ============ 格子右键菜单 ============
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = Box.FolderPath, UseShellExecute = true }); }
        catch { }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var input = InputDialog.Show("重命名格子", "格子名称：", Box.Name);
        if (!string.IsNullOrWhiteSpace(input))
        {
            Box.Name = input.Trim();
            TitleText.Text = Box.Name;
            _ = App.SaveSettingsAsync();
        }
    }

    private void MapFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择要映射到格子的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var path = dlg.SelectedPath;
            _watcher?.Dispose();
            Box.FolderPath = path;
            Box.IsMapped = true;
            Box.Name = new DirectoryInfo(path).Name;
            TitleText.Text = Box.Name;
            RefreshItems();
            StartWatch();
            _ = App.SaveSettingsAsync();
        }
    }

    private async void DeleteBox_Click(object sender, RoutedEventArgs e)
    {
        // 映射到桌面的「桌面整理」格子：只是桌面的视图，删除即移除格子并恢复桌面文件显示
        if (Box.IsMapped && SettingsService.IsDesktopPath(Box.FolderPath))
        {
            var r = MessageBox.Show($"删除格子「{Box.Name}」？\n桌面文件不会被删除或移动，并会恢复显示在桌面。",
                "DeskTidy", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            await App.Instance.RemoveBoxAsync(Box.Id);
            App.Instance.RestoreDesktopFiles();
            Close();
            return;
        }

        if (Box.IsMapped)
        {
            var r = MessageBox.Show(
                $"删除映射「{Box.Name}」？\n格子将变为空格子，映射的文件夹不会被修改。",
                "DeskTidy", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            _watcher?.Dispose();
            Box.IsMapped = false;
            Box.FolderPath = SettingsService.GetOrCreateBoxFolder(Box.Name);
            TitleText.Text = Box.Name;
            RefreshItems();
            StartWatch();
            await App.SaveSettingsAsync();
            return;
        }

        var yes = MessageBox.Show($"删除格子「{Box.Name}」？\n格子内的文件将移动回桌面。",
            "DeskTidy", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (yes != MessageBoxResult.Yes) return;
        RestoreFilesToDesktop();
        await App.Instance.RemoveBoxAsync(Box.Id);
        Close();
    }

    private static string ResolveDest(string dest)
    {
        if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;
        var dir = Path.GetDirectoryName(dest)!;
        var name = Path.GetFileNameWithoutExtension(dest);
        var ext = Path.GetExtension(dest);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private void RestoreFilesToDesktop()
    {
        try
        {
            if (!Directory.Exists(Box.FolderPath)) return;
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            foreach (var item in Directory.GetFileSystemEntries(Box.FolderPath))
            {
                var dest = ResolveDest(Path.Combine(desktop, Path.GetFileName(item)));
                try { MoveEntry(item, dest); }
                catch { }
            }
            try { Directory.Delete(Box.FolderPath, recursive: false); } catch { }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"还原文件到桌面时出错：{ex.Message}", "DeskTidy",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        Box.X = Left; Box.Y = Top;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
