using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PDFReaderX.App.ViewModels;

/// <summary>
/// 侧边栏的一页缩略图：位图由后台线程生成，生成完成后更新。
/// </summary>
public sealed partial class ThumbnailViewModel : ViewModelBase
{
    public ThumbnailViewModel(int pageIndex, double baseWidth, double baseHeight)
    {
        PageIndex = pageIndex;
        BaseWidth = baseWidth;
        BaseHeight = baseHeight;
    }

    public int PageIndex { get; }

    /// <summary>zoom = 1 时的页面宽度（DIP），用于保持缩略图宽高比。</summary>
    public double BaseWidth { get; }

    /// <summary>zoom = 1 时的页面高度（DIP）。</summary>
    public double BaseHeight { get; }

    public string Title => $"第 {PageIndex + 1} 页";

    [ObservableProperty]
    private BitmapSource? _thumbnail;

    [ObservableProperty]
    private bool _isCurrent;
}