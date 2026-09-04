﻿using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PDFReaderX.App.Controls;
using PDFReaderX.App.Models;
using PDFReaderX.App.Services;
using PDFReaderX.App.ViewModels;

namespace PDFReaderX.App;

public partial class MainWindow : Window
{
    private bool _thumbnailListHover;
    private bool _bookmarksDirty;
    private bool _isBookmarkSyncing;
    private const double SidebarMinWidth = 160;
    private const double SidebarMaxWidth = 450;
    private double _lastSidebarWidth = 220;
    private ScaleTransform _sidebarScaleTransform = new();

    // 快捷笔长按拖动排序
    private QuickPenStyle? _dragPenItem;
    private Point _dragPenStart;
    private bool _dragPenArmed;
    private bool _dragPenMoved;
    private DispatcherTimer? _penLongPressTimer;

    public MainWindow()
    {
        InitializeComponent();
        _sidebarScaleTransform = (ScaleTransform)ThumbnailTabContent.LayoutTransform;
        RestoreWindowBounds();
        _saveToastTimer.Tick += (_, _) =>
        {
            _saveToastTimer.Stop();
            SaveToast.Visibility = Visibility.Collapsed;
        };
        DataContextChanged += OnDataContextChanged;
        Canvas.UndoStateChanged += (_, _) => UpdateUndoButtons();
        Canvas.CurrentPageChanged += OnCanvasCurrentPageChanged;
        UpdateUndoButtons();
        RestoreSidebarWidth();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainWindowViewModel oldViewModel)
        {
            oldViewModel.PdfrxReady -= OnPdfrxReady;
            oldViewModel.DocumentOpened -= OnDocumentOpened;
            oldViewModel.Bookmarks.CollectionChanged -= OnBookmarksChanged;
        }
        if (e.NewValue is MainWindowViewModel newViewModel)
        {
            newViewModel.PdfrxReady += OnPdfrxReady;
            newViewModel.DocumentOpened += OnDocumentOpened;
            newViewModel.Bookmarks.CollectionChanged += OnBookmarksChanged;
        }
    }

    /// <summary>画布当前页变化：更新缩略图当前位置，并让书签定位/高亮到对应章节。</summary>
    private void OnCanvasCurrentPageChanged(object? sender, int pageIndex)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.CurrentPageIndex = pageIndex;
        if (!_thumbnailListHover)
        {
            var thumbnail = viewModel.Thumbnails.FirstOrDefault(t => t.PageIndex == pageIndex);
            if (thumbnail is not null)
            {
                ThumbnailList.ScrollIntoView(thumbnail);
            }
        }

        SyncBookmarkToPage(viewModel, pageIndex);
    }

    /// <summary>根据当前页选择最接近的书签；先展开父节点，再滚动到对应可视化项。</summary>
    private void SyncBookmarkToPage(MainWindowViewModel viewModel, int pageIndex)
    {
        if (viewModel.Bookmarks.Count == 0)
        {
            return;
        }

        var target = FindBookmarkForPage(viewModel.Bookmarks, pageIndex);
        if (target is null)
        {
            return;
        }

        _isBookmarkSyncing = true;
        try
        {
            BringBookmarkIntoView(BookmarkTree, target);
        }
        finally
        {
            _isBookmarkSyncing = false;
        }
    }

    private static BookmarkViewModel? FindBookmarkForPage(
        IEnumerable<BookmarkViewModel> bookmarks, int pageIndex)
    {
        BookmarkViewModel? best = null;
        void Visit(BookmarkViewModel item)
        {
            if (item.CanNavigate && item.PageIndex <= pageIndex)
            {
                if (best is null
                    || item.PageIndex > best.PageIndex
                    || (item.PageIndex == best.PageIndex && item.Children.Count > 0))
                {
                    best = item;
                }
            }

            foreach (var child in item.Children)
            {
                Visit(child);
            }
        }

        foreach (var bookmark in bookmarks)
        {
            Visit(bookmark);
        }

        return best;
    }

    private void BringBookmarkIntoView(TreeView tree, BookmarkViewModel bookmark)
    {
        // 先定位可视化项；虚拟化列表可能需要先让父节点显示出来。
        var container = FindTreeContainer(tree, bookmark);
        if (container is null)
        {
            container = RevealTreeContainer(tree, bookmark);
        }

        if (container is null)
        {
            return;
        }

        container.IsExpanded = true;
        container.BringIntoView();
        if (!container.IsSelected)
        {
            container.IsSelected = true;
        }
    }

    private static TreeViewItem? FindTreeContainer(ItemsControl parent, object item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem direct)
        {
            return direct;
        }

        foreach (object container in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(container) is TreeViewItem child
                && FindTreeContainer(child, item) is { } result)
            {
                return result;
            }
        }

        return null;
    }

    private TreeViewItem? RevealTreeContainer(TreeView tree, BookmarkViewModel bookmark)
    {
        var path = new List<BookmarkViewModel>();
        for (var current = bookmark; current is not null;)
        {
            path.Insert(0, current);
            current = FindParentBookmark(tree.Items.Cast<BookmarkViewModel>(), current);
        }

        ItemsControl parent = tree;
        foreach (var item in path)
        {
            var container = parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
            if (container is null)
            {
                // 父节点尚未生成子项时，先布局后再取一次。
                parent.UpdateLayout();
                container = parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
            }

            if (container is null
                || (!ReferenceEquals(item, bookmark) && !container.IsExpanded && !container.HasItems))
            {
                return null;
            }

            container.IsExpanded = true;
            if (!ReferenceEquals(item, bookmark))
            {
                container.BringIntoView();
                parent.UpdateLayout();
            }

            parent = container;
        }

        return parent as TreeViewItem;
    }

    private static BookmarkViewModel? FindParentBookmark(
        IEnumerable<BookmarkViewModel> candidates, BookmarkViewModel target)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Children.Contains(target))
            {
                return candidate;
            }

            if (FindParentBookmark(candidate.Children, target) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private bool _isExitingWithSave;

    /// <summary>恢复上次关闭时的窗口大小（限制在当前工作区内，避免小屏幕窗口过大），并恢复最大化状态。</summary>
    private void RestoreWindowBounds()
    {
        var settings = AppSettingsStore.Load();
        var work = SystemParameters.WorkArea;
        var width = Math.Max(640, Math.Min(settings.WindowWidth ?? 1280.0, work.Width));
        var height = Math.Max(480, Math.Min(settings.WindowHeight ?? 800.0, work.Height));
        Width = width;
        Height = height;
        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>恢复侧栏宽度（限制在允许范围内），并同步折叠按钮状态。</summary>
    private void RestoreSidebarWidth()
    {
        var settings = AppSettingsStore.Load();
        if (settings.SidebarWidth is not { } savedWidth)
        {
            return;
        }

        _lastSidebarWidth = Math.Clamp(savedWidth, SidebarMinWidth, SidebarMaxWidth);
        SidebarColumn.MinWidth = SidebarMinWidth;
        SidebarColumn.MaxWidth = SidebarMaxWidth;
        SidebarColumn.Width = new GridLength(_lastSidebarWidth);
        SidebarToggle.ToolTip = "折叠侧栏";
    }

    /// <summary>把侧栏宽度写入应用配置，折叠时仍保留上次展开宽度。</summary>
    private void SaveSidebarWidth()
    {
        var settings = AppSettingsStore.Load();
        settings.SidebarWidth = SidebarColumn.ActualWidth > 1
            ? SidebarColumn.ActualWidth
            : _lastSidebarWidth;
        AppSettingsStore.Save(settings);
    }

    private void SaveWindowBounds()
    {
        var settings = AppSettingsStore.Load();
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            settings.WindowWidth = bounds.Width;
            settings.WindowHeight = bounds.Height;
        }
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        AppSettingsStore.Save(settings);
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        SaveWindowBounds(); // 记住关闭时的窗口大小/最大化状态，下次启动恢复
        SaveSidebarWidth();

        if (DataContext is not MainWindowViewModel viewModel || viewModel.Document is null)
        {
            return;
        }

        if (_isExitingWithSave)
        {
            return; // 保存已完成，允许直接关闭
        }

        if (!Canvas.IsModified && !_bookmarksDirty)
        {
            return;
        }

        var confirm = new ExitConfirmWindow { Owner = this };
        var choice = confirm.ShowDialog();
        if (choice is null)
        {
            e.Cancel = true; // 取消关闭
            return;
        }
        if (choice == false)
        {
            return; // 不保存，直接退出
        }

        // 保存并退出：先取消本次关闭，异步保存并显示进度，完成后真正退出
        e.Cancel = true;
        SaveOnExitAsync(viewModel);
    }

    private async void SaveOnExitAsync(MainWindowViewModel viewModel)
    {
        if (viewModel.Document is not { } document)
        {
            return; // 文档已关闭，直接退出
        }

        var progressWindow = new ExitSaveWindow { Owner = this };
        progressWindow.Show();
        try
        {
            var progress = new Progress<string>(text => progressWindow.SetStatus(text));
            if (!string.IsNullOrEmpty(viewModel.CurrentPdfrxPath))
            {
                // 退出保存不重新内嵌 PDF（保留源文件引用），避免写入上百 MB
                // SaveAsync 需在 UI 线程读取画布元素，写文件部分内部已在后台执行
                await PdfrxStore.SaveAsync(
                    viewModel.CurrentPdfrxPath, Canvas, document, viewModel.Bookmarks,
                    progress, embedPdf: false);
            }
            else
            {
                SessionStore.Save(Canvas, document, viewModel.Bookmarks);
            }
            progressWindow.SetCompleted();
            await Task.Delay(250);
            progressWindow.Close();
            _isExitingWithSave = true;
            Close();
        }
        catch (Exception ex)
        {
            LogExitSaveError(ex);
            var stillExit = progressWindow.AskExitAfterFailure($"{ex.GetType().Name}: {ex.Message}");
            progressWindow.Close();
            if (stillExit)
            {
                _isExitingWithSave = true;
                Close();
            }
        }
    }

    private readonly DispatcherTimer _saveToastTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

    private void ShowSaveToast(string message, bool autoHide = false)
    {
        SaveToastText.Text = message;
        SaveToast.Visibility = Visibility.Visible;
        _saveToastTimer.Stop();
        if (autoHide)
        {
            _saveToastTimer.Start();
        }
    }

    private static void LogExitSaveError(Exception ex)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PDFReaderX", "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "exit-save-error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // 日志写入失败不影响退出
        }
    }

        private void OnResetZoomClick(object sender, RoutedEventArgs e)
    {
        Canvas.ResetView();
    }

    private void OnDocumentOpened()
    {
        Canvas.ResetModified();
        _bookmarksDirty = false;
    }

    /// <summary>点击 "+"：打开新建笔对话框。</summary>
    private void OnAddQuickPenClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        var dialog = new NewPenDialog(
            viewModel.PenColors.ToList(),
            viewModel.PenWidths.ToList(),
            viewModel.HighlightWidths.ToList()) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } style)
        {
            viewModel.AddQuickPen(style);
        }
    }

    /// <summary>右键快捷笔 → 删除这支笔。</summary>
    private void OnDeleteQuickPenClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item
            && item.Parent is ContextMenu menu
            && menu.PlacementTarget is Button { Tag: QuickPenStyle style }
            && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.RemoveQuickPen(style);
        }
    }

    /// <summary>长按快捷笔：按住约 0.25 秒后进入拖动排序模式（未移动松手仍视为点击）。</summary>
    private void OnQuickPenPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: QuickPenStyle style })
        {
            return;
        }
        _dragPenItem = style;
        _dragPenStart = e.GetPosition(QuickPensPanel);
        _dragPenArmed = false;
        _dragPenMoved = false;
        _penLongPressTimer?.Stop();
        _penLongPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _penLongPressTimer.Tick += (_, _) =>
        {
            _penLongPressTimer.Stop();
            if (_dragPenItem is not null && Mouse.LeftButton == MouseButtonState.Pressed && sender is Button button)
            {
                _dragPenArmed = true;
                button.CaptureMouse();
                button.Opacity = 0.55;
            }
        };
        _penLongPressTimer.Start();
    }

    private void OnQuickPenPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragPenItem is null || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (_dragPenArmed)
        {
            var position = e.GetPosition(QuickPensPanel);
            var currentIndex = viewModel.QuickPens.IndexOf(_dragPenItem);
            var targetIndex = GetQuickPenTargetIndex(position);
            if (targetIndex >= 0 && targetIndex != currentIndex)
            {
                viewModel.QuickPens.Move(currentIndex, targetIndex);
                _dragPenMoved = true;
            }
            return;
        }
        // 长按计时结束前移动超过阈值 → 放弃长按（鼠标小抖动不影响）
        if (_penLongPressTimer?.IsEnabled == true
            && (e.GetPosition(QuickPensPanel) - _dragPenStart).Length > 8)
        {
            _penLongPressTimer.Stop();
        }
    }

    private void OnQuickPenPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _penLongPressTimer?.Stop();
        var button = sender as Button;
        button?.ReleaseMouseCapture();
        if (_dragPenItem is not null && DataContext is MainWindowViewModel viewModel)
        {
            if (_dragPenArmed && _dragPenMoved)
            {
                // 长按并真的拖动了 → 只排序，不切换
                viewModel.SaveQuickPens();
                e.Handled = true;
            }
            else
            {
                // 普通点击（短按，或长按后未移动）→ 切换笔
                var style = _dragPenItem;
                viewModel.ActiveTool = style.Tool;
                if (style.Tool == InkTool.Pen)
                {
                    viewModel.PenColor = style.Color;
                    viewModel.PenWidth = style.Width;
                }
                else
                {
                    viewModel.HighlightColor = style.Color;
                    viewModel.HighlightWidth = style.Width;
                }
            }
        }
        _dragPenArmed = false;
        _dragPenMoved = false;
        _dragPenItem = null;
        if (button is not null)
        {
            button.Opacity = 1.0;
        }
    }

    /// <summary>根据鼠标 X 位置计算应插入的索引（按各容器中心点二分）。</summary>
    private int GetQuickPenTargetIndex(Point position)
    {
        var count = ((MainWindowViewModel)DataContext!).QuickPens.Count;
        for (var i = 0; i < count; i++)
        {
            if (QuickPensPanel.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container)
            {
                var bounds = container.TransformToAncestor(QuickPensPanel)
                    .TransformBounds(new Rect(container.RenderSize));
                if (position.X < bounds.Left + bounds.Width / 2)
                {
                    return i;
                }
            }
        }
        return count - 1;
    }
    /// <summary>点工具按钮右上角箭头：已展开则收起，未展开则展开（ToggleButton 自带切换）。</summary>
    private void OnToolArrowClick(object sender, RoutedEventArgs e)
    {
        // 点击箭头同时切换工具，保证视觉一致
        if (DataContext is MainWindowViewModel viewModel && sender is ToggleButton { Tag: string tool })
        {
            if (Enum.TryParse<InkTool>(tool, out var inkTool))
            {
                viewModel.ActiveTool = inkTool;
            }
        }
    }

    private void OnPenColorPicked(object? sender, ColorPickedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.PenColor = e.Color;
            viewModel.AddRecentColor(e.Color);
        }
    }

    private void OnPenWidthPicked(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && sender is InkToolOptionsPanel panel)
        {
            viewModel.PenWidth = panel.CurrentWidth;
        }
    }

    private void OnHighlightColorPicked(object? sender, ColorPickedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.HighlightColor = e.Color;
            viewModel.AddRecentColor(e.Color);
        }
    }

    private void OnHighlightWidthPicked(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && sender is InkToolOptionsPanel panel)
        {
            viewModel.HighlightWidth = panel.CurrentWidth;
        }
    }

    private void OnEraserWidthPicked(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && sender is InkToolOptionsPanel panel)
        {
            viewModel.EraserWidth = panel.CurrentWidth;
        }
    }

    private void OnPenMoreColorsClicked(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        var dialog = new ColorPickerDialog(viewModel.PenColor) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            viewModel.PenColor = dialog.SelectedColor;
            viewModel.AddRecentColor(dialog.SelectedColor);
        }
    }

    private void OnHighlightMoreColorsClicked(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        var dialog = new ColorPickerDialog(viewModel.HighlightColor) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            viewModel.HighlightColor = dialog.SelectedColor;
            viewModel.AddRecentColor(dialog.SelectedColor);
        }
    }

    private void OnPenEyedropperClicked(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            PickWithEyedropper(viewModel.PenColor, color => viewModel.PenColor = color);
        }
    }

    private void OnHighlightEyedropperClicked(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            PickWithEyedropper(viewModel.HighlightColor, color => viewModel.HighlightColor = color);
        }
    }

    private void PickWithEyedropper(Color currentColor, Action<Color> apply)
    {
        var picker = new EyedropperWindow { Owner = this };
        if (picker.ShowDialog() == true && picker.PickedColor is { } color)
        {
            apply(color);
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.AddRecentColor(color);
            }
        }
    }

    private void OnBookmarksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        _bookmarksDirty = true;
    }

    private void OnPdfrxReady(PdfrxPackage package)
    {
        Canvas.RestoreFromPackage(package);
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.StatusText = "批注文档已恢复";
        }
    }

    private async void OnSavePdfrxClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || viewModel.Document is not { } document)
        {
            MessageBox.Show("请先打开一个 PDF 文档。", "PDFReader X", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targetPath = viewModel.CurrentPdfrxPath;
        if (string.IsNullOrEmpty(targetPath))
        {
            var dialog = new SaveFileDialog
            {
                Title = "保存批注文档",
                Filter = "PDFReader X 批注文档 (*.pdfrx)|*.pdfrx",
                FileName = Path.GetFileNameWithoutExtension(viewModel.FileName) + ".pdfrx",
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }
            targetPath = dialog.FileName;
        }
        else if (!Canvas.IsModified && !_bookmarksDirty)
        {
            ShowSaveToast("没有需要保存的更改", autoHide: true);
            return;
        }

        try
        {
            var progress = new Progress<string>(text => ShowSaveToast(text));
            await PdfrxStore.SaveAsync(targetPath, Canvas, document, viewModel.Bookmarks, progress);
            viewModel.CurrentPdfrxPath = targetPath;
            Canvas.ResetModified();
            _bookmarksDirty = false;
            var fileName = Path.GetFileName(targetPath);
            ShowSaveToast($"已保存 {fileName}", autoHide: true);
            viewModel.StatusText = $"已保存 {fileName}";
        }
        catch (Exception ex)
        {
            ShowSaveToast("保存失败", autoHide: true);
            MessageBox.Show(
                $"保存失败：\n{ex.Message}\n\n如果文件正被其他程序（如杀毒软件）占用，请稍后重试。",
                "PDFReader X",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void OnExportPdfClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || viewModel.Document is not { } document)
        {
            MessageBox.Show("请先打开一个 PDF 文档。", "PDFReader X", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出 PDF",
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            FileName = Path.GetFileNameWithoutExtension(viewModel.FileName) + "-批注.pdf",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var progress = new Progress<string>(text =>
            {
                viewModel.StatusText = text;
                ShowSaveToast(text);
            });
            await PdfExportService.ExportAsync(dialog.FileName, Canvas, document, progress);
            viewModel.StatusText = $"已导出 {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"导出失败：\n{ex.Message}",
                "PDFReader X",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnInsertImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "插入图片",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true && !Canvas.InsertImageFromFile(dialog.FileName))
        {
            MessageBox.Show("无法加载该图片文件。", "PDFReader X", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        Canvas.Undo();
    }

    /// <summary>折叠/展开左侧缩略图与书签栏。</summary>

    private void OnSidebarToggleClick(object sender, RoutedEventArgs e)
    {
        var collapsed = SidebarColumn.Width.IsAbsolute && SidebarColumn.Width.Value < 1;
        if (collapsed)
        {
            var width = Math.Clamp(_lastSidebarWidth, SidebarMinWidth, SidebarMaxWidth);
            SidebarColumn.MinWidth = SidebarMinWidth;
            SidebarColumn.MaxWidth = SidebarMaxWidth;
            SidebarColumn.Width = new GridLength(width);
        }
        else
        {
            if (SidebarColumn.ActualWidth > 1)
            {
                _lastSidebarWidth = SidebarColumn.ActualWidth;
            }

            SidebarColumn.MinWidth = 0;
            SidebarColumn.MaxWidth = 0;
            SidebarColumn.Width = new GridLength(0);
        }

        SidebarToggleIcon.Data = collapsed
            ? Geometry.Parse("M 5 3 L 10 10 L 5 17")
            : Geometry.Parse("M 8 3 L 3 10 L 8 17");
        SidebarToggle.ToolTip = collapsed ? "折叠侧栏" : "展开侧栏";
    }

    private void OnRedoClick(object sender, RoutedEventArgs e)
    {
        Canvas.Redo();
    }

    /// <summary>字号输入框回车：解析自定义字号并应用。</summary>
    private void OnFontSizePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyFontSizeInput((ComboBox)sender);
            e.Handled = true;
        }
    }

    /// <summary>字号输入框失焦：解析自定义字号并应用。</summary>
    private void OnFontSizeLostFocus(object sender, RoutedEventArgs e)
    {
        ApplyFontSizeInput((ComboBox)sender);
    }

    /// <summary>解析字号输入（6~144），更新 ViewModel 并回显当前值。</summary>
    private void ApplyFontSizeInput(ComboBox combo)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (double.TryParse(combo.Text, out var size))
        {
            size = Math.Clamp(size, 6, 144);
            if (Math.Abs(size - viewModel.TextFontSize) > 0.01)
            {
                viewModel.TextFontSize = size;
            }
        }
        combo.Text = viewModel.TextFontSize.ToString("0.##");
    }

    private void OnBoldClick(object sender, RoutedEventArgs e)
    {
        Canvas.ToggleEditBold();
    }

    private void OnItalicClick(object sender, RoutedEventArgs e)
    {
        Canvas.ToggleEditItalic();
    }

    private void OnUnderlineClick(object sender, RoutedEventArgs e)
    {
        Canvas.ToggleEditUnderline();
    }

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = Canvas.CanUndo;
        RedoButton.IsEnabled = Canvas.CanRedo;
    }

    /// <summary>缩略图列表滚轮：一次滚动半页视口高度，避免速度过快。</summary>
    /// <summary>Ctrl+滚轮缩放侧栏内容，普通滚轮仍由内部列表滚动。</summary>
    private void OnSidebarPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            || SidebarColumn.ActualWidth <= 1)
        {
            return;
        }

        var scale = Math.Clamp(_sidebarScaleTransform.ScaleX + Math.Sign(e.Delta) * 0.1, 0.7, 1.8);
        _sidebarScaleTransform.ScaleX = scale;
        _sidebarScaleTransform.ScaleY = scale;
        e.Handled = true;
    }

    /// <summary>拖动侧栏分隔条调整宽度。</summary>
    private void OnSidebarSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (SidebarColumn.ActualWidth <= 1)
        {
            return;
        }

        var width = Math.Clamp(SidebarColumn.ActualWidth + e.HorizontalChange, SidebarMinWidth, SidebarMaxWidth);
        SidebarColumn.MinWidth = SidebarMinWidth;
        SidebarColumn.MaxWidth = SidebarMaxWidth;
        SidebarColumn.Width = new GridLength(width);
        _lastSidebarWidth = width;
    }

    private void OnThumbnailListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindVisualChild<ScrollViewer>(ThumbnailList) is not ScrollViewer scrollViewer)
        {
            return;
        }
        var step = Math.Max(1, scrollViewer.ViewportHeight / 2.0);
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - Math.Sign(e.Delta) * step);
        e.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }
            if (FindVisualChild<T>(child) is T found)
            {
                return found;
            }
        }
        return null;
    }

    private void OnThumbnailSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThumbnailList.SelectedItem is ThumbnailViewModel thumbnail)
        {
            Canvas.GoToPage(thumbnail.PageIndex);
        }
    }

    private void OnBookmarkSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_isBookmarkSyncing)
        {
            return;
        }

        if (e.NewValue is BookmarkViewModel bookmark && bookmark.CanNavigate)
        {
            Canvas.GoToPage(bookmark.PageIndex);
        }
    }

    private void OnThumbnailListMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _thumbnailListHover = true;
    }

    private void OnThumbnailListMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _thumbnailListHover = false;
    }

    // 快捷键：撤销/重做/删除/粘贴图片（文本框编辑中不拦截）
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase)
        {
            return;
        }
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.Z)
        {
            Canvas.Undo();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Y)
        {
            Canvas.Redo();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            if (Canvas.HasSelectedInk)
            {
                Canvas.DeleteSelectedInk();
            }
            else
            {
                Canvas.DeleteSelectedElement();
            }
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.V && Clipboard.ContainsImage())
        {
            Canvas.InsertImage(Clipboard.GetImage());
            e.Handled = true;
        }
    }

    // 拖拽 PDF / 图片到窗口任意位置打开或插入
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0
            && File.Exists(files[0]))
        {
            var first = files[0];
            var extension = Path.GetExtension(first).ToLowerInvariant();
            if (extension == ".pdf")
            {
                if (DataContext is MainWindowViewModel viewModel)
                {
                    _ = viewModel.OpenFileAsync(first);
                }
            }
            else if (extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
            {
                var position = e.GetPosition(Canvas);
                if (!Canvas.InsertImageFromFile(first, position, selectAfterInsert: false)) // 拖入保持当前工具，不打断书写
                {
                    MessageBox.Show("无法加载该图片文件。", "PDFReader X", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        e.Handled = true;
    }
}
