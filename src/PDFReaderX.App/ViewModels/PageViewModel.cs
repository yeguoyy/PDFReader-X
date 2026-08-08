using System.Windows.Ink;

namespace PDFReaderX.App.ViewModels;

/// <summary>
/// 画布上的一页 PDF：基础尺寸（zoom=1 时）+ 该页墨迹集合。
/// 位图不保存在这里，由 InfiniteCanvas 按视口按需渲染并缓存。
/// </summary>
public sealed class PageViewModel : ViewModelBase
{
    public PageViewModel(int pageIndex, double baseWidth, double baseHeight)
    {
        PageIndex = pageIndex;
        BaseWidth = baseWidth;
        BaseHeight = baseHeight;
    }

    public int PageIndex { get; }

    /// <summary>zoom = 1 时的页面宽度（DIP）。</summary>
    public double BaseWidth { get; }

    /// <summary>zoom = 1 时的页面高度（DIP）。</summary>
    public double BaseHeight { get; }

    /// <summary>该页的墨迹集合（与画布上的 InkCanvas.Strokes 共享同一实例）。</summary>
    public StrokeCollection Strokes { get; } = new();

    public string Title => $"第 {PageIndex + 1} 页";
}
