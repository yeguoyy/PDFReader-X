using System.IO;
using System.Windows;
using PDFReaderX.App.ViewModels;

namespace PDFReaderX.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnResetZoomClick(object sender, RoutedEventArgs e)
    {
        Canvas.ResetView();
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
