using System.Windows;
using System.Windows.Controls;

namespace PDFReaderX.App.Controls;

/// <summary>快捷笔图标模板选择器：按 Kind 选择笔种图标模板，便于扩展新笔种。</summary>
public sealed class QuickPenIconSelector : DataTemplateSelector
{
    public DataTemplate? PenTemplate { get; set; }

    public DataTemplate? HighlighterTemplate { get; set; }

    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
        return item is QuickPenStyle { Kind: "highlighter" }
            ? HighlighterTemplate!
            : PenTemplate!;
    }
}