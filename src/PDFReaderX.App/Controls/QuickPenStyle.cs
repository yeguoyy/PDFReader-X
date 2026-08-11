using System.Windows.Media;

namespace PDFReaderX.App.Controls;

/// <summary>工具栏快捷笔样式：点击即切换工具、颜色与粗细（仿 OneNote 笔列表）。</summary>
public sealed class QuickPenStyle
{
    /// <summary>唯一标识（用于选中态判断）。</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>对应的工具（钢笔或荧光笔）。</summary>
    public InkTool Tool { get; init; }

    /// <summary>笔种标识（"pen"/"highlighter"，决定图标外观，后续可扩展新笔种）。</summary>
    public string Kind { get; init; } = "pen";

    /// <summary>颜色。</summary>
    public Color Color { get; init; }

    /// <summary>粗细。</summary>
    public double Width { get; init; }

    /// <summary>显示名（ToolTip）。</summary>
    public string Name { get; init; } = string.Empty;
}