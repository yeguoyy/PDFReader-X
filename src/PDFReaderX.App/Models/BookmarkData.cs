namespace PDFReaderX.App.Models;

/// <summary>书签树节点的可序列化数据（.pdfrx 包内 bookmarks.json 使用）。</summary>
public sealed class BookmarkData
{
    public string Title { get; set; } = "";
    public int PageIndex { get; set; }
    public List<BookmarkData> Children { get; set; } = new();
}
