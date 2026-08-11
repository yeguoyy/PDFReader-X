using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PDFReaderX.App.Models;
using PDFReaderX.App.Services;
using PDFReaderX.App.ViewModels;

namespace PDFReaderX.App;

public partial class MainWindow : Window
{
    private bool _thumbnailListHover;
    private bool _bookmarksDirty;

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowBounds();
        _saveToastTimer.Tick += (_, _) =>
        {
            _saveToastTimer.Stop();
            SaveToast.Visibility = Visibility.Collapsed;
        };
        DataContextChanged += OnDataContextChanged;
        Canvas.UndoStateChanged += (_, _) => UpdateUndoButtons();
        UpdateUndoButtons();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainWindowViewModel oldViewModel)
        {
            oldViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            oldViewModel.PdfrxReady -= OnPdfrxReady;
            oldViewModel.DocumentOpened -= OnDocumentOpened;
            oldViewModel.Bookmarks.CollectionChanged -= OnBookmarksChanged;
        }
        if (e.NewValue is MainWindowViewModel newViewModel)
        {
            newViewModel.PropertyChanged += OnViewModelPropertyChanged;
            newViewModel.PdfrxReady += OnPdfrxReady;
            newViewModel.DocumentOpened += OnDocumentOpened;
            newViewModel.Bookmarks.CollectionChanged += OnBookmarksChanged;
        }
    }

    /// <summary>当前页变化时，让缩略图列表跟随滚动（鼠标悬停在列表上时不打扰用户）。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.CurrentPageIndex)
            || sender is not MainWindowViewModel viewModel
            || _thumbnailListHover)
        {
            return;
        }

        var current = viewModel.Thumbnails.FirstOrDefault(t => t.PageIndex == viewModel.CurrentPageIndex);
        if (current is not null)
        {
            ThumbnailList.ScrollIntoView(current);
        }
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

    /// <summary>保存当前窗口大小与最大化状态（最大化时记录还原尺寸）。</summary>
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

    private void OnPenColorSwatchClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel
            && sender is FrameworkElement element
            && element.DataContext is Color color)
        {
            viewModel.PenColor = color;
        }
    }

    private void OnPenWidthClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel
            && sender is FrameworkElement element
            && element.DataContext is double width)
        {
            viewModel.PenWidth = width;
        }
    }

    private void OnHighlightColorSwatchClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel
            && sender is FrameworkElement element
            && element.DataContext is Color color)
        {
            viewModel.HighlightColor = color;
        }
    }

    private void OnHighlightWidthClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel
            && sender is FrameworkElement element
            && element.DataContext is double width)
        {
            viewModel.HighlightWidth = width;
        }
    }

    private void OnEraserWidthClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel
            && sender is FrameworkElement element
            && element.DataContext is double width)
        {
            viewModel.EraserWidth = width;
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
