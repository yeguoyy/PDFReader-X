namespace PDFReaderX.LLM;

/// <summary>目录识别结果。</summary>
public sealed class TocResult
{
    /// <summary>已提供的文本中是否识别到目录。</summary>
    public bool HasToc { get; init; }

    /// <summary>目录是否已完整读完（不会再有后续目录页）。</summary>
    public bool TocComplete { get; init; }

    /// <summary>目录书签树（PageIndex 为归一化后的 0 基书本页码）。</summary>
    public List<LlmBookmark> Bookmarks { get; init; } = new();
}
