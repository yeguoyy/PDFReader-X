using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PDFReaderX.App.Controls;
using PDFReaderX.Core.Services;

namespace PDFReaderX.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private const double DisplayDpi = 96; // 页面基准尺寸按 96 DIP（zoom=1 时 1:1）

    [ObservableProperty]
    private PdfRenderService? _document;

    public ObservableCollection<PageViewModel> Pages { get; } = new();

    [ObservableProperty]
    private bool _hasDocument;

    [ObservableProperty]
    private string _fileName = "未打开文件";

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private bool _isBusy;

    // ---------- 画布状态 ----------

    [ObservableProperty]
    private double _zoom = 1.0;

    [ObservableProperty]
    private InkTool _activeTool = InkTool.Pen;

    [ObservableProperty]
    private Color _penColor = Colors.Black;

    [ObservableProperty]
    private double _penWidth = 3.0;

    [ObservableProperty]
    private Color _highlightColor = Colors.Yellow;

    [ObservableProperty]
    private double _highlightWidth = 24.0;

    [ObservableProperty]
    private double _eraserWidth = 16.0;

    public IReadOnlyList<Color> PenColors { get; } = new[]
    {
        Colors.Black, Colors.Red, Colors.Blue, Colors.Green, Colors.Orange, Colors.Purple,
    };

    public IReadOnlyList<Color> HighlightColors { get; } = new[]
    {
        Colors.Yellow, Colors.Lime, Colors.Cyan, Colors.Magenta, Colors.Orange,
    };

    public IReadOnlyList<double> PenWidths { get; } = new[] { 1.5, 3.0, 5.0 };
    public IReadOnlyList<double> HighlightWidths { get; } = new[] { 16.0, 24.0, 32.0 };
    public IReadOnlyList<double> EraserWidths { get; } = new[] { 8.0, 16.0, 32.0 };

    public RelayCommand OpenCommand { get; }

    public RelayCommand CloseCommand { get; }

    public MainWindowViewModel()
    {
        OpenCommand = new RelayCommand(Open, () => !IsBusy);
        CloseCommand = new RelayCommand(Close, () => HasDocument);
    }

    private async void Open()
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开 PDF 文件",
            Filter = "PDF 文件 (*.pdf)|*.pdf|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await OpenFileAsync(dialog.FileName);
    }

    public async Task OpenFileAsync(string filePath)
    {
        try
        {
            await LoadAsync(filePath);
        }
        catch (Exception ex)
        {
            StatusText = $"打开失败：{ex.Message}";
            MessageBox.Show(
                $"无法打开文件：\n{ex.Message}",
                "PDFReader X",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task LoadAsync(string filePath)
    {
        IsBusy = true;
        try
        {
            StatusText = "正在加载 PDF…";
            var document = await Task.Run(() => PdfRenderService.Load(filePath));
            CloseDocument();
            Document = document;

            HasDocument = true;
            FileName = Path.GetFileName(filePath);
            Zoom = 1.0;
            StatusText = "正在创建页面…";

            const int batchSize = 32;
            for (var i = 0; i < document.PageCount; i++)
            {
                var size = document.GetPageSize(i);
                var width = Math.Ceiling(size.Width * DisplayDpi / 72.0);
                var height = Math.Ceiling(size.Height * DisplayDpi / 72.0);
                Pages.Add(new PageViewModel(i, width, height));

                if ((i + 1) % batchSize == 0)
                {
                    StatusText = $"正在创建页面 {i + 1}/{document.PageCount}…";
                    await Task.Yield(); // 让 UI 有机会刷新状态栏
                }
            }

            StatusText = $"已加载 {document.PageCount} 页 · {Path.GetFileName(filePath)}";
        }
        finally
        {
            IsBusy = false;
            OpenCommand.NotifyCanExecuteChanged();
            CloseCommand.NotifyCanExecuteChanged();
        }
    }

    private void Close()
    {
        CloseDocument();
        StatusText = "就绪";
    }

    private void CloseDocument()
    {
        Document?.Dispose();
        Document = null;
        HasDocument = false;
        FileName = "未打开文件";
        Pages.Clear();
    }
}
