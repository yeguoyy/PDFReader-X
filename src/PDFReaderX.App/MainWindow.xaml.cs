using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PDFReaderX.App.ViewModels;

namespace PDFReaderX.App;

public partial class MainWindow : Window
{
    private bool _thumbnailListHover;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
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

    // 拖拽 PDF 到窗口任意位置打开
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
            && File.Exists(files[0])
            && string.Equals(Path.GetExtension(files[0]), ".pdf", System.StringComparison.OrdinalIgnoreCase))
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                _ = viewModel.OpenFileAsync(files[0]);
            }
        }
        e.Handled = true;
    }
}
