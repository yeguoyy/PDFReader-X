using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PDFReaderX.App.Helpers;
using PDFReaderX.Core.Services;

namespace PDFReaderX.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private const int DisplayDpi = 96; // 1 WPF DIP = 1/96 英寸，按 1:1 显示

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

        try
        {
            await LoadAsync(dialog.FileName);
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
            var document = await Task.Run(() => PdfRenderService.Load(filePath));
            CloseDocument();
            _document = document;

            HasDocument = true;
            FileName = Path.GetFileName(filePath);
            StatusText = $"正在渲染 {document.PageCount} 页…";

            for (var i = 0; i < document.PageCount; i++)
            {
                var index = i;
                var page = await Task.Run(() =>
                {
                    using var bitmap = document.RenderPage(index, DisplayDpi);
                    var size = document.GetPageSize(index);
                    var width = Math.Ceiling(size.Width * DisplayDpi / 72.0);
                    var height = Math.Ceiling(size.Height * DisplayDpi / 72.0);
                    return new PageViewModel(index, bitmap.ToBitmapSource(), width, height);
                });
                Pages.Add(page);
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
        _document?.Dispose();
        _document = null;
        HasDocument = false;
        FileName = "未打开文件";
        Pages.Clear();
    }
}
