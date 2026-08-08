using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PDFReaderX.App.ViewModels;

namespace PDFReaderX.App;

public partial class MainWindow : Window
{
    private bool _thumbnailListHover;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Canvas.UndoStateChanged += (_, _) => UpdateUndoButtons();
        UpdateUndoButtons();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainWindowViewModel oldViewModel)
        {
            oldViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        if (e.NewValue is MainWindowViewModel newViewModel)
        {
            newViewModel.PropertyChanged += OnViewModelPropertyChanged;
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

    private void OnResetZoomClick(object sender, RoutedEventArgs e)
    {
        Canvas.ResetView();
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
            Canvas.DeleteSelectedElement();
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
