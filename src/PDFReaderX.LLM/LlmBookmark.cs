namespace PDFReaderX.LLM;

/// <summary>
/// LLM 生成的书签节点。
/// PageIndex 为 0 基页索引；从 LLM 返回的 1 基页码转换而来。
/// </summary>
public sealed class LlmBookmark
{
    public LlmBookmark(string title, int pageIndex)
    {
        Title = title;
        PageIndex = pageIndex;
    }

    public string Title { get; set; }

    public int PageIndex { get; set; }

    public List<LlmBookmark> Children { get; } = new();
}
