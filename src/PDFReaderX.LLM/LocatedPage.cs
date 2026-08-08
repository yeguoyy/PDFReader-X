namespace PDFReaderX.LLM;

/// <summary>视觉定位到的章节标题及其在整个 PDF 中的绝对页码（1 基）。</summary>
public sealed record LocatedPage(string Title, int PageNumber);
