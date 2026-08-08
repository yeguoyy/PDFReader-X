using System.Drawing;
using PdfiumViewer;

namespace PDFReaderX.Core.Services;

/// <summary>
/// 封装 PDFium 的加载与渲染。
/// 注意：PdfDocument 不是线程安全的，调用方需保证同一时间只有一个线程访问同一实例。
/// </summary>
public sealed class PdfRenderService : IDisposable
{
    private readonly PdfDocument _document;
    private readonly object _renderLock = new();

    private PdfRenderService(PdfDocument document, string filePath)
    {
        _document = document;
        FilePath = filePath;
    }

    public string FilePath { get; }

    public int PageCount => _document.PageCount;

    public static PdfRenderService Load(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        return new PdfRenderService(PdfDocument.Load(filePath), filePath);
    }

    /// <summary>页面原始尺寸（磅，72dpi）。</summary>
    public SizeF GetPageSize(int pageIndex) => _document.PageSizes[pageIndex];

    /// <summary>
    /// 按目标宽度渲染页面缩略图。自动计算合适的 DPI（受上下限约束），
    /// 相比全尺寸渲染大幅降低内存占用，适合侧边栏批量生成。
    /// </summary>
    public Image RenderThumbnail(int pageIndex, int targetWidthPx)
    {
        var size = GetPageSize(pageIndex);
        var dpi = Math.Clamp(72.0 * targetWidthPx / size.Width, 16.0, 96.0);
        return RenderPage(pageIndex, (int)Math.Round(dpi), PdfRenderFlags.LcdText);
    }

    /// <summary>按指定 DPI 渲染页面到位图。</summary>
    public Image RenderPage(
        int pageIndex,
        int dpi,
        PdfRenderFlags flags = PdfRenderFlags.LcdText | PdfRenderFlags.Annotations)
    {
        var size = GetPageSize(pageIndex);
        var width = (int)Math.Ceiling(size.Width * dpi / 72.0);
        var height = (int)Math.Ceiling(size.Height * dpi / 72.0);
        lock (_renderLock)
        {
            return _document.Render(pageIndex, width, height, dpi, dpi, flags);
        }
    }

    /// <summary>提取页面文本（Phase 4 LLM 书签生成使用）。</summary>
    public string GetPageText(int pageIndex)
    {
        lock (_renderLock)
        {
            return _document.GetPdfText(pageIndex);
        }
    }

    /// <summary>读取 PDF 大纲（书签目录），无书签时返回空列表。</summary>
    public IReadOnlyList<BookmarkNode> GetOutline()
    {
        lock (_renderLock)
        {
            return ConvertBookmarks(_document.Bookmarks);
        }
    }

    private static List<BookmarkNode> ConvertBookmarks(PdfBookmarkCollection bookmarks)
    {
        var result = new List<BookmarkNode>(bookmarks.Count);
        foreach (var bookmark in bookmarks)
        {
            var node = new BookmarkNode(bookmark.Title, bookmark.PageIndex);
            if (bookmark.Children is { Count: > 0 })
            {
                node.Children.AddRange(ConvertBookmarks(bookmark.Children));
            }
            result.Add(node);
        }
        return result;
    }

    public void Dispose() => _document.Dispose();
}
