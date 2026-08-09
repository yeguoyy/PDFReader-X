namespace PDFReaderX.App.Models;

/// <summary>画布元素的可序列化数据（.pdfrx 包内 canvas.json 使用）。</summary>
public sealed class CanvasElementData
{
    /// <summary>元素类型："image" | "text"。</summary>
    public string Type { get; set; } = "text";

    /// <summary>图片元素：包内相对路径（images/img_xxx.png）。</summary>
    public string? ImageFile { get; set; }

    /// <summary>文本元素：文本内容。</summary>
    public string? Text { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public double FontSize { get; set; } = 14;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public string Color { get; set; } = "#FF000000";
}
