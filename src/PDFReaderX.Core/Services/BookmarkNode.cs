namespace PDFReaderX.Core.Services;

/// <summary>
/// PDF 大纲（书签目录）中的一个节点。
/// </summary>
public sealed class BookmarkNode
{
    public BookmarkNode(string title, int pageIndex)
    {
        Title = title;
        PageIndex = pageIndex;
    }

    /// <summary>书签标题。</summary>
    public string Title { get; }

    /// <summary>目标页索引（0 基）；无有效目标时为 -1。</summary>
    public int PageIndex { get; }

    /// <summary>子书签。</summary>
    public List<BookmarkNode> Children { get; } = new();
}