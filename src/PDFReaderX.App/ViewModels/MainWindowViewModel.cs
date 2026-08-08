using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PDFReaderX.App.Controls;
using PDFReaderX.App.Helpers;
using PDFReaderX.Core.Services;

namespace PDFReaderX.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private const double DisplayDpi = 96; // 页面基准尺寸按 96 DIP（zoom=1 时 1:1）
    private int _thumbnailEpoch; // 缩略图生成批次号，换文档时失效
    private int _lastCurrentThumb = -1;

    [ObservableProperty]
    private PdfRenderService? _document;

    public ObservableCollection<PageViewModel> Pages { get; } = new();

    /// <summary>侧边栏页面缩略图（后台线程生成）。</summary>
    public ObservableCollection<ThumbnailViewModel> Thumbnails { get; } = new();

    /// <summary>侧边栏书签目录（来自 PDF Outline）。</summary>
    public ObservableCollection<BookmarkViewModel> Bookmarks { get; } = new();

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

    /// <summary>画布上页面之间的间距（DIP）。</summary>
    [ObservableProperty]
    private double _pageGap = 24.0;

    /// <summary>当前所在页（0 基），由画布视口中心推算，供侧边栏高亮与跟随。</summary>
    [ObservableProperty]
    private int _currentPageIndex;

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

    partial void OnCurrentPageIndexChanged(int value)
    {
        if (_lastCurrentThumb >= 0 && _lastCurrentThumb < Thumbnails.Count)
        {
            Thumbnails[_lastCurrentThumb].IsCurrent = false;
        }
        if (value >= 0 && value < Thumbnails.Count)
        {
            Thumbnails[value].IsCurrent = true;
        }
        _lastCurrentThumb = value;
    }

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

            // 书签目录：后台读取，避免大 PDF 卡住 UI
            try
            {
                var outline = await Task.Run(() => document.GetOutline());
                foreach (var node in outline)
                {
                    Bookmarks.Add(BookmarkViewModel.FromCore(node));
                }
            }
            catch
            {
                // 个别 PDF 大纲读取失败不影响打开
            }

            StartThumbnailGeneration(document);

            StatusText = $"已加载 {document.PageCount} 页 · {Path.GetFileName(filePath)}";
        }
        finally
        {
            IsBusy = false;
            OpenCommand.NotifyCanExecuteChanged();
            CloseCommand.NotifyCanExecuteChanged();
        }
    }

    private void StartThumbnailGeneration(PdfRenderService document)
    {
        _thumbnailEpoch++;
        var epoch = _thumbnailEpoch;

        for (var i = 0; i < document.PageCount; i++)
        {
            var size = document.GetPageSize(i);
            var width = Math.Ceiling(size.Width * DisplayDpi / 72.0);
            var height = Math.Ceiling(size.Height * DisplayDpi / 72.0);
            Thumbnails.Add(new ThumbnailViewModel(i, width, height));
        }

        _ = GenerateThumbnailsAsync(document, epoch);
    }

    private async Task GenerateThumbnailsAsync(PdfRenderService document, int epoch)
    {
        const int targetWidthPx = 160;
        const int yieldInterval = 6;

        for (var i = 0; i < document.PageCount; i++)
        {
            if (epoch != _thumbnailEpoch)
            {
                return; // 文档已关闭或更换
            }

            BitmapSource? source = null;
            try
            {
                using var bitmap = document.RenderThumbnail(i, targetWidthPx);
                source = bitmap.ToBitmapSource();
            }
            catch
            {
                // 单页缩略图失败：跳过，不阻塞后续页
            }

            var index = i;
            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (epoch != _thumbnailEpoch || index >= Thumbnails.Count)
                    {
                        return;
                    }
                    Thumbnails[index].Thumbnail = source;
                });
            }
            catch
            {
                return; // 窗口已关闭
            }

            if (i % yieldInterval == yieldInterval - 1)
            {
                await Task.Delay(1); // 让出 CPU，保持 UI 流畅
            }
        }
    }

    private void Close()
    {
        CloseDocument();
        StatusText = "就绪";
    }

    private void CloseDocument()
    {
        _thumbnailEpoch++; // 取消进行中的缩略图生成
        _lastCurrentThumb = -1;
        Document?.Dispose();
        Document = null;
        HasDocument = false;
        FileName = "未打开文件";
        Pages.Clear();
        Thumbnails.Clear();
        Bookmarks.Clear();
        CurrentPageIndex = 0;
    }
}
