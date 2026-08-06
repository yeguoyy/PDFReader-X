using System.Windows.Media.Imaging;

namespace PDFReaderX.App.ViewModels;

/// <summary>画布上的一页 PDF，承载渲染后的位图。</summary>
public sealed class PageViewModel : ViewModelBase
{
    public PageViewModel(int pageIndex, BitmapSource image, double width, double height)
    {
        PageIndex = pageIndex;
        Image = image;
        Width = width;
        Height = height;
    }

    public int PageIndex { get; }

    public BitmapSource Image { get; }

    public double Width { get; }

    public double Height { get; }

    public string Title => $"第 {PageIndex + 1} 页";
}
