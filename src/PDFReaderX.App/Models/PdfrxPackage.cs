namespace PDFReaderX.App.Models;

/// <summary>从 .pdfrx 包读取出的完整画布状态，用于打开后恢复。</summary>
public sealed class PdfrxPackage
{
    public required byte[] PdfBytes { get; set; }

    /// <summary>manifest 中记录的源 PDF 路径（未内嵌 PDF 的会话包用于定位源文件）。</summary>
    public string? PdfSource { get; set; }
    public double Zoom { get; set; } = 1.0;
    public double PanX { get; set; }
    public double PanY { get; set; }
    public List<CanvasElementData> Elements { get; } = new();
    public Dictionary<int, byte[]> PageInks { get; } = new();
    public byte[]? FreeInk { get; set; }
    public List<BookmarkData> Bookmarks { get; } = new();
    public Dictionary<string, byte[]> Images { get; } = new();
}
