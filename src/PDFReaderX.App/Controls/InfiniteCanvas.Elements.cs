using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using PDFReaderX.App.Models;
using PDFReaderX.Core.Utilities;

namespace PDFReaderX.App.Controls;

/// <summary>无限画布的元素层：图片与文本框（世界坐标），支持选择、拖动、缩放与删除。</summary>
public partial class InfiniteCanvas
{
    private const double DefaultTextFontSize = 14;

    private sealed class CanvasElement
    {
        public required Grid Root { get; set; }
        public required FrameworkElement Content { get; set; }
        public bool IsText;
        public bool SizeAdjusted;
        public string Text = string.Empty;
        public double FontSize = DefaultTextFontSize;
        public FontWeight Weight = FontWeights.Normal;
        public FontStyle Style = FontStyles.Normal;
        public TextDecorationCollection? Decorations;
        public BitmapSource? ImageSource;
        public Color Color = Colors.Black; // 文本颜色（随元素保存，.pdfrx 持久化用）
        public List<TextRunInfo>? TextRuns; // 分段富文本（局部格式），null 表示整段统一格式
        public double WorldX;
        public double WorldY;
        public double WorldWidth;
        public double WorldHeight;
    }

    /// <summary>富文本分段（局部格式），Text 内 "\n" 表示换行。</summary>
    private sealed class TextRunInfo
    {
        public string Text = string.Empty;
        public double FontSize = DefaultTextFontSize;
        public FontWeight Weight = FontWeights.Normal;
        public FontStyle Style = FontStyles.Normal;
        public TextDecorationCollection? Decorations;
        public Color Color = Colors.Black;
    }

    private readonly List<CanvasElement> _elements = new();
    private CanvasElement? _selectedElement;
    private Grid? _selectionAdorner;
    private CanvasElement? _dragElement;
    private Point _dragStartScreen;
    private double _dragStartWorldX;
    private double _dragStartWorldY;
    private CanvasElement? _editingTextElement;
    private bool _editingTextIsNew;
    private bool _textCreateActive;
    private bool _textEditSelectAllOnFocus;
    private Point _textCreateStart;
    private bool _resizeActive;
    private ResizeDirection _resizeDirection;
    private Point _resizeStart;
    private double _resizeX;
    private double _resizeY;
    private double _resizeW;
    private double _resizeH;

    private Point ScreenToWorld(Point screen)
    {
        var (x, y) = CanvasTransform.ScreenToWorld(screen.X, screen.Y, _pan.X, _pan.Y, Zoom);
        return new Point(x, y);
    }

    /// <summary>视口坐标是否落在元素当前的视觉矩形内。</summary>
    private bool IsPointInElement(Point viewportPoint, CanvasElement element)
    {
        var x = element.WorldX * Zoom + _pan.X + _docOffsetX;
        var y = element.WorldY * Zoom + _pan.Y;
        return viewportPoint.X >= x && viewportPoint.X <= x + element.WorldWidth * Zoom
            && viewportPoint.Y >= y && viewportPoint.Y <= y + element.WorldHeight * Zoom;
    }

    /// <summary>视口坐标命中的文本元素（按 Z 序取最上层）。</summary>
    private CanvasElement? FindTextElementAt(Point viewportPoint)
    {
        for (var i = _elements.Count - 1; i >= 0; i--)
        {
            if (_elements[i].IsText && IsPointInElement(viewportPoint, _elements[i]))
            {
                return _elements[i];
            }
        }
        return null;
    }

    /// <summary>从文件插入图片到画布（可选指定屏幕位置，默认视口中心）。失败返回 false。</summary>
    public bool InsertImageFromFile(string path, Point? screenPosition = null, bool selectAfterInsert = true)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            InsertImage(bitmap, screenPosition, selectAfterInsert);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>插入图片元素到画布（屏幕位置转世界坐标，默认视口中心）。默认插入后切到选择工具并自动选中，拖入场景可保持当前工具。</summary>
    public void InsertImage(BitmapSource source, Point? screenPosition = null, bool selectAfterInsert = true)
    {
        var world = screenPosition is Point point ? ScreenToWorld(point)
            : ScreenToWorld(new Point(ActualWidth / 2, ActualHeight / 2));

        // 默认大小按像素数判断、占当前视口比例计算，与缩放和图片 DPI 无关：
        // 小图保持原始像素大小（相对小），大图等比缩放到视口宽度 60% 以内（高度不超过视口 80%）。
        // 世界尺寸 = 目标屏幕尺寸 / Zoom，渲染后屏幕尺寸在任何缩放级别下一致。
        double width = Math.Max(1, source.PixelWidth);
        double height = Math.Max(1, source.PixelHeight);
        var fit = Math.Min(1.0, Math.Min((Math.Max(1, ActualWidth) * 0.6) / width, (Math.Max(1, ActualHeight) * 0.8) / height));
        width = width * fit / Math.Max(0.01, Zoom);
        height = height * fit / Math.Max(0.01, Zoom);

        var image = new Image { Source = source, Stretch = Stretch.Uniform };
        var element = new CanvasElement
        {
            Root = CreateElementRoot(width, height),
            Content = image,
            ImageSource = source,
            WorldX = world.X - width / 2,
            WorldY = world.Y - height / 2,
            WorldWidth = width,
            WorldHeight = height,
        };
        element.Root.Children.Add(image);

        AddElementInternal(element);
        RecordUndo(
            undo: () => RemoveElementInternal(element),
            redo: () => AddElementInternal(element));
        if (selectAfterInsert)
        {
            ActiveTool = InkTool.Select;
            SelectElement(element); // 插入后直接选中，方便立即调整大小与位置
        }
    }

    /// <summary>在屏幕位置开始输入一个新文本框。</summary>
    public void StartTextElementAt(Point screenPosition)
    {
        if (_editingTextElement is not null)
        {
            return;
        }
        var world = ScreenToWorld(screenPosition);
        var element = new CanvasElement
        {
            Root = CreateElementRoot(240, 44),
            Content = CreateTextEditBox(),
            IsText = true,
            WorldX = world.X,
            WorldY = world.Y,
            WorldWidth = 240,
            WorldHeight = 44,
            Color = PenColor,
        };
        element.Root.Children.Add(element.Content); // 把输入框加入容器（缺失会导致输入框不显示、无法聚焦）
        ApplyTextEditBorder(element.Root);
        ViewportCanvas.Children.Add(element.Root);
        Panel.SetZIndex(element.Root, 10);
        UpdateElementLayout(element);
        element.Root.UpdateLayout(); // 强制同步布局，确保 Loaded 立即触发、Focus 可用
        _editingTextElement = element;
        _editingTextIsNew = true;
        AttachTextEditing(element);
        AddResizeThumbs(element, element.Root);
        FocusTextEditBox((RichTextBox)element.Content);
    }

    /// <summary>创建文本时按住拖动：以按下点为左上角调整初始框大小（最小 40×30）。</summary>
    private void ResizeTextCreate(Point current)
    {
        if (_editingTextElement is not { } element)
        {
            return;
        }
        var start = ScreenToWorld(_textCreateStart);
        var now = ScreenToWorld(current);
        var x = Math.Min(start.X, now.X);
        var y = Math.Min(start.Y, now.Y);
        var width = Math.Max(40, Math.Abs(now.X - start.X));
        var height = Math.Max(30, Math.Abs(now.Y - start.Y));
        element.WorldX = x;
        element.WorldY = y;
        element.WorldWidth = width;
        element.WorldHeight = height;
        element.Root.Width = width;
        element.Root.Height = height;
        element.SizeAdjusted = true;
        UpdateElementLayout(element);
    }

    /// <summary>输入框右下角缩放手柄：编辑中直接调整框大小，影响换行。</summary>
    [Flags]
    private enum ResizeDirection { None = 0, N = 1, S = 2, E = 4, W = 8 }

    private static readonly (ResizeDirection Dir, HorizontalAlignment Ha, VerticalAlignment Va, Cursor Cursor, double W, double H)[] ResizeThumbSpecs = new[]
    {
        (ResizeDirection.N, HorizontalAlignment.Stretch, VerticalAlignment.Top, Cursors.SizeNS, double.NaN, 10.0),
        (ResizeDirection.S, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, Cursors.SizeNS, double.NaN, 10.0),
        (ResizeDirection.E, HorizontalAlignment.Right, VerticalAlignment.Stretch, Cursors.SizeWE, 10.0, double.NaN),
        (ResizeDirection.W, HorizontalAlignment.Left, VerticalAlignment.Stretch, Cursors.SizeWE, 10.0, double.NaN),
        (ResizeDirection.N | ResizeDirection.W, HorizontalAlignment.Left, VerticalAlignment.Top, Cursors.SizeNWSE, 10.0, 10.0),
        (ResizeDirection.N | ResizeDirection.E, HorizontalAlignment.Right, VerticalAlignment.Top, Cursors.SizeNESW, 10.0, 10.0),
        (ResizeDirection.S | ResizeDirection.E, HorizontalAlignment.Right, VerticalAlignment.Bottom, Cursors.SizeNWSE, 10.0, 10.0),
        (ResizeDirection.S | ResizeDirection.W, HorizontalAlignment.Left, VerticalAlignment.Bottom, Cursors.SizeNESW, 10.0, 10.0),
    };

    /// <summary>给容器加 8 个透明缩放热区（四角四边），拖动时调整位置与大小，无图标干扰。</summary>
    private void AddResizeThumbs(CanvasElement element, Panel container)
    {
        foreach (var (dir, ha, va, cursor, width, height) in ResizeThumbSpecs)
        {
            var thumb = new Border
            {
                Background = Brushes.Transparent,
                Cursor = cursor,
                HorizontalAlignment = ha,
                VerticalAlignment = va,
            };
            if (!double.IsNaN(width))
            {
                thumb.Width = width;
            }
            if (!double.IsNaN(height))
            {
                thumb.Height = height;
            }
            thumb.MouseLeftButtonDown += (_, e) => OnResizeThumbDown(element, dir, thumb, e);
            thumb.MouseMove += (_, e) => OnResizeThumbMove(element, e);
            thumb.MouseLeftButtonUp += (_, e) => OnResizeThumbUp(element, thumb, e);
            container.Children.Add(thumb);
        }
    }

    private void OnResizeThumbDown(CanvasElement element, ResizeDirection dir, Border thumb, MouseButtonEventArgs e)
    {
        _resizeActive = true;
        _resizeDirection = dir;
        _resizeStart = e.GetPosition(RootGrid);
        _resizeX = element.WorldX;
        _resizeY = element.WorldY;
        _resizeW = element.WorldWidth;
        _resizeH = element.WorldHeight;
        thumb.CaptureMouse();
        e.Handled = true;
    }

    private void OnResizeThumbMove(CanvasElement element, MouseEventArgs e)
    {
        if (!_resizeActive)
        {
            return;
        }
        var position = e.GetPosition(RootGrid);
        var dx = (position.X - _resizeStart.X) / Zoom;
        var dy = (position.Y - _resizeStart.Y) / Zoom;
        var x = _resizeX;
        var y = _resizeY;
        var width = _resizeW;
        var height = _resizeH;
        if (_resizeDirection.HasFlag(ResizeDirection.W))
        {
            x += dx;
            width -= dx;
        }
        if (_resizeDirection.HasFlag(ResizeDirection.E))
        {
            width += dx;
        }
        if (_resizeDirection.HasFlag(ResizeDirection.N))
        {
            y += dy;
            height -= dy;
        }
        if (_resizeDirection.HasFlag(ResizeDirection.S))
        {
            height += dy;
        }
        if (width < 40)
        {
            if (_resizeDirection.HasFlag(ResizeDirection.W))
            {
                x -= 40 - width;
            }
            width = 40;
        }
        if (height < 30)
        {
            if (_resizeDirection.HasFlag(ResizeDirection.N))
            {
                y -= 30 - height;
            }
            height = 30;
        }
        element.WorldX = x;
        element.WorldY = y;
        element.WorldWidth = width;
        element.WorldHeight = height;
        element.Root.Width = width;
        element.Root.Height = height;
        element.SizeAdjusted = true;
        UpdateElementLayout(element);
        UpdateSelectionAdorner();
        if (_editingTextElement is not null)
        {
            AutoSizeTextEditBox();
        }
    }

    private void OnResizeThumbUp(CanvasElement element, Border thumb, MouseButtonEventArgs e)
    {
        if (!_resizeActive)
        {
            return;
        }
        _resizeActive = false;
        thumb.ReleaseMouseCapture();
        if (!ReferenceEquals(_editingTextElement, element))
        {
            var fromX = _resizeX;
            var fromY = _resizeY;
            var fromW = _resizeW;
            var fromH = _resizeH;
            var toX = element.WorldX;
            var toY = element.WorldY;
            var toW = element.WorldWidth;
            var toH = element.WorldHeight;
            if (Math.Abs(fromX - toX) > 0.001 || Math.Abs(fromY - toY) > 0.001
                || Math.Abs(fromW - toW) > 0.001 || Math.Abs(fromH - toH) > 0.001)
            {
                RecordUndo(
                    undo: () => ApplyElementBounds(element, fromX, fromY, fromW, fromH),
                    redo: () => ApplyElementBounds(element, toX, toY, toW, toH));
            }
        }
        e.Handled = true;
    }

    private void ApplyElementBounds(CanvasElement element, double x, double y, double width, double height)
    {
        element.WorldX = x;
        element.WorldY = y;
        element.WorldWidth = width;
        element.WorldHeight = height;
        element.Root.Width = width;
        element.Root.Height = height;
        UpdateElementLayout(element);
        UpdateSelectionAdorner();
    }


    /// <summary>删除当前选中的元素（记录撤销）。</summary>
    public void DeleteSelectedElement()
    {
        var element = _selectedElement;
        if (element is null || _editingTextElement is not null)
        {
            return;
        }
        Deselect();
        RemoveElementInternal(element);
        RecordUndo(
            undo: () => AddElementInternal(element),
            redo: () => RemoveElementInternal(element));
    }

    private static Grid CreateElementRoot(double width, double height) => new()
    {
        Background = Brushes.Transparent,
        Width = width,
        Height = height,
    };

    private void AttachElementEvents(CanvasElement element)
    {
        element.Root.MouseLeftButtonDown += OnElementMouseDown;
        element.Root.MouseMove += OnElementMouseMove;
        element.Root.MouseLeftButtonUp += OnElementMouseUp;
    }

    /// <summary>把元素加入正式列表并挂交互事件（元素 Root 需已存在于画布）。</summary>
    private void RegisterElement(CanvasElement element)
    {
        var firstTime = element.Root.Tag is null;
        element.Root.Tag = element;
        if (firstTime)
        {
            AttachElementEvents(element);
        }
        _elements.Add(element);
    }

    private void AddElementInternal(CanvasElement element)
    {
        RegisterElement(element);
        ViewportCanvas.Children.Add(element.Root);
        Panel.SetZIndex(element.Root, 10);
        UpdateElementLayout(element);
    }

    private void RemoveElementInternal(CanvasElement element)
    {
        if (_selectedElement == element)
        {
            Deselect();
        }
        _elements.Remove(element);
        ViewportCanvas.Children.Remove(element.Root);
    }

    /// <summary>按世界坐标与当前缩放刷新元素位置。</summary>
    private void UpdateElementLayout(CanvasElement element)
    {
        Canvas.SetLeft(element.Root, element.WorldX * Zoom);
        Canvas.SetTop(element.Root, element.WorldY * Zoom);
        element.Root.RenderTransform = new ScaleTransform(Zoom, Zoom);
    }

    private void UpdateElementsLayout()
    {
        foreach (var element in _elements)
        {
            UpdateElementLayout(element);
        }
        if (_editingTextElement is not null)
        {
            UpdateElementLayout(_editingTextElement);
        }
        UpdateSelectionAdorner();
    }

    private void ClearElements()
    {
        Deselect();
        _editingTextElement = null;
        foreach (var element in _elements)
        {
            ViewportCanvas.Children.Remove(element.Root);
        }
        _elements.Clear();
        _dragElement = null;
    }

    // ---------- 选择与拖动 ----------

    private void OnElementMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_editingTextElement is not null)
        {
            return;
        }
        if (((FrameworkElement)sender).Tag is not CanvasElement element)
        {
            return;
        }

        if (ActiveTool == InkTool.Select && e.ClickCount == 2 && element.IsText)
        {
            StartTextEdit(element);
            e.Handled = true;
            return;
        }

        if (ActiveTool != InkTool.Select)
        {
            return;
        }

        SelectElement(element);
        _dragElement = element;
        _dragStartScreen = e.GetPosition(RootGrid);
        _dragStartWorldX = element.WorldX;
        _dragStartWorldY = element.WorldY;
        element.Root.CaptureMouse();
        e.Handled = true;
    }

    private void OnElementMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragElement is null)
        {
            return;
        }
        var position = e.GetPosition(RootGrid);
        _dragElement.WorldX = _dragStartWorldX + (position.X - _dragStartScreen.X) / Zoom;
        _dragElement.WorldY = _dragStartWorldY + (position.Y - _dragStartScreen.Y) / Zoom;
        UpdateElementLayout(_dragElement);
        UpdateSelectionAdorner();
    }

    private void OnElementMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragElement is null)
        {
            return;
        }
        var element = _dragElement;
        _dragElement = null;
        element.Root.ReleaseMouseCapture();

        var fromX = _dragStartWorldX;
        var fromY = _dragStartWorldY;
        var toX = element.WorldX;
        var toY = element.WorldY;
        if (Math.Abs(fromX - toX) > 0.001 || Math.Abs(fromY - toY) > 0.001)
        {
            RecordUndo(
                undo: () =>
                {
                    element.WorldX = fromX;
                    element.WorldY = fromY;
                    UpdateElementLayout(element);
                    UpdateSelectionAdorner();
                },
                redo: () =>
                {
                    element.WorldX = toX;
                    element.WorldY = toY;
                    UpdateElementLayout(element);
                    UpdateSelectionAdorner();
                });
        }
        e.Handled = true;
    }

    private void SelectElement(CanvasElement element)
    {
        Deselect();
        _selectedElement = element;

        var dashed = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF)),
            StrokeThickness = 1.2,
            IsHitTestVisible = false,
        };
        // 文本与图片元素的选择边框统一跟随“边框设置”
        var (brush, thickness, dashes, visible) = GetTextBorderVisual(TextBorderStyle);
        dashed.Stroke = brush;
        dashed.StrokeThickness = thickness;
        if (dashes is not null)
        {
            dashed.StrokeDashArray = dashes;
        }
        if (!visible)
        {
            dashed.Visibility = Visibility.Collapsed;
        }

        var adorner = new Grid();
        adorner.Children.Add(dashed);
        AddResizeThumbs(element, adorner);
        ViewportCanvas.Children.Add(adorner);
        Panel.SetZIndex(adorner, 20);
        _selectionAdorner = adorner;
        UpdateSelectionAdorner();
    }

    private void Deselect()
    {
        _selectedElement = null;
        if (_selectionAdorner is not null)
        {
            ViewportCanvas.Children.Remove(_selectionAdorner);
            _selectionAdorner = null;
        }
    }

    private void UpdateSelectionAdorner()
    {
        if (_selectionAdorner is null || _selectedElement is null)
        {
            return;
        }
        var element = _selectedElement;
        var x = element.WorldX * Zoom;
        var y = element.WorldY * Zoom;
        var width = Math.Max(4, element.WorldWidth * Zoom);
        var height = Math.Max(4, element.WorldHeight * Zoom);
        Canvas.SetLeft(_selectionAdorner, x - 4);
        Canvas.SetTop(_selectionAdorner, y - 4);
        _selectionAdorner.Width = width + 8;
        _selectionAdorner.Height = height + 8;
    }

    // ---------- 文本框编辑 ----------

    /// <summary>创建可见的富文本输入框（白底，边框由画布层装饰提供，文字颜色跟随当前画笔颜色）。</summary>
    private RichTextBox CreateTextEditBox(IReadOnlyList<TextRunInfo>? runs = null, CanvasElement? format = null)
    {
        var box = new RichTextBox
        {
            FontSize = format?.FontSize ?? TextFontSize,
            FontWeight = format?.Weight ?? FontWeights.Normal,
            FontStyle = format?.Style ?? FontStyles.Normal,
            Foreground = new SolidColorBrush(format?.Color ?? PenColor),
            Background = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            VerticalContentAlignment = VerticalAlignment.Top,
            AcceptsReturn = true,
            // 显式设置选区画刷：失焦（点工具栏字号/颜色）时也保留蓝色高亮，能看清哪些字符被选中
            SelectionBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF)),
            SelectionTextBrush = Brushes.White,
            IsInactiveSelectionHighlightEnabled = true,
        };
        FillRichTextBox(box, runs);
        return box;
    }

    /// <summary>把分段格式写入 RichTextBox（"\n" 转为软换行），编辑与提交保持同一结构。</summary>
    private static void FillRichTextBox(RichTextBox box, IReadOnlyList<TextRunInfo>? runs)
    {
        box.Document.Blocks.Clear();
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        box.Document.Blocks.Add(paragraph);
        if (runs is null)
        {
            return;
        }
        foreach (var run in runs)
        {
            var segments = run.Text.Split('\n');
            for (var i = 0; i < segments.Length; i++)
            {
                if (i > 0)
                {
                    paragraph.Inlines.Add(new LineBreak());
                }
                if (segments[i].Length == 0)
                {
                    continue;
                }
                paragraph.Inlines.Add(new Run(segments[i])
                {
                    FontSize = run.FontSize,
                    FontWeight = run.Weight,
                    FontStyle = run.Style,
                    TextDecorations = run.Decorations,
                    Foreground = new SolidColorBrush(run.Color),
                });
            }
        }
    }

    /// <summary>提取 RichTextBox 的全部文本与分段格式（段落/软换行统一转为 "\n"）。</summary>
    private static List<TextRunInfo> ExtractRuns(RichTextBox box)
    {
        var runs = new List<TextRunInfo>();
        TextRunInfo? last = null;

        void PushBreak()
        {
            runs.Add(new TextRunInfo
            {
                Text = "\n",
                FontSize = last?.FontSize ?? DefaultTextFontSize,
                Weight = last?.Weight ?? FontWeights.Normal,
                Style = last?.Style ?? FontStyles.Normal,
                Decorations = last?.Decorations,
                Color = last?.Color ?? Colors.Black,
            });
            last = null;
        }

        void Walk(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run run when run.Text.Length > 0:
                        runs.Add(new TextRunInfo
                        {
                            Text = run.Text,
                            FontSize = run.FontSize,
                            Weight = run.FontWeight,
                            Style = run.FontStyle,
                            Decorations = run.TextDecorations,
                            Color = (run.Foreground as SolidColorBrush)?.Color ?? Colors.Black,
                        });
                        last = runs[^1];
                        break;
                    case LineBreak:
                        PushBreak();
                        break;
                    case Span span:
                        Walk(span.Inlines);
                        break;
                }
            }
        }

        var firstBlock = true;
        foreach (var block in box.Document.Blocks)
        {
            if (block is not Paragraph paragraph)
            {
                continue;
            }
            if (!firstBlock)
            {
                PushBreak(); // 段落之间补换行
            }
            firstBlock = false;
            Walk(paragraph.Inlines);
        }
        while (runs.Count > 0 && runs[^1].Text == "\n")
        {
            runs.RemoveAt(runs.Count - 1); // 尾部段落符不保留
        }
        return runs;
    }

    /// <summary>拼接分段文本为纯文本（保留 "\n" 换行）。</summary>
    private static string BuildPlainText(IReadOnlyList<TextRunInfo> runs)
    {
        var sb = new StringBuilder();
        foreach (var run in runs)
        {
            sb.Append(run.Text);
        }
        return sb.ToString();
    }

    /// <summary>规范化换行符：\r\n 与 \r 统一为 \n（旧文件兼容）。</summary>
    private static string NormalizeNewlines(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>按分段格式创建渲染用 Run。</summary>
    private static Run CreateRun(TextRunInfo run) => new(run.Text)
    {
        FontSize = run.FontSize,
        FontWeight = run.Weight,
        FontStyle = run.Style,
        TextDecorations = run.Decorations,
        Foreground = new SolidColorBrush(run.Color),
    };

    /// <summary>文本框边框样式的视觉参数（编辑框与选择框共用，保持同步）。</summary>
    private static (Brush Brush, double Thickness, DoubleCollection? Dashes, bool Visible) GetTextBorderVisual(string style)
    {
        return style switch
        {
            "blue-solid" => (new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF)), 2.0, null, true),
            "black-solid" => (Brushes.Black, 1.5, null, true),
            "gray-thin" => (new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)), 1.0, null, true),
            "none" => (Brushes.Transparent, 0.0, null, false),
            _ => (Brushes.Black, 1.0, new DoubleCollection { 3, 2 }, true),
        };
    }

    /// <summary>按当前设置的款式给输入框容器叠加边框装饰（支持虚线，随画布缩放）。</summary>
    private void ApplyTextEditBorder(Grid root)
    {
        var (brush, thickness, dashes, visible) = GetTextBorderVisual(TextBorderStyle);
        var rect = new Rectangle
        {
            Stroke = brush,
            StrokeThickness = thickness,
            IsHitTestVisible = false,
            RadiusX = 2,
            RadiusY = 2,
        };
        if (dashes is not null)
        {
            rect.StrokeDashArray = dashes;
        }
        if (!visible)
        {
            rect.Visibility = Visibility.Collapsed;
        }
        root.Children.Add(rect); // 放在最上层，避免被 TextBox 白底盖住（IsHitTestVisible=false 不挡输入）
    }
    /// <summary>边框设置变化后立即刷新：正在编辑的文本框与选择框应用新款式。</summary>
    public void RefreshElementBorders()
    {
        if (_editingTextElement is not null)
        {
            ApplyTextEditBorder(_editingTextElement.Root);
        }
        if (_selectedElement is not null)
        {
            var element = _selectedElement;
            Deselect();
            SelectElement(element);
        }
    }

    /// <summary>立即尝试聚焦，并在随后 ~2s 内持续重试（覆盖窗口激活/布局延迟导致的首帧聚焦失败）。</summary>
    private void FocusTextEditBox(RichTextBox box, bool selectAll = false)
    {
        _textEditSelectAllOnFocus = selectAll;
        if (Window.GetWindow(this) is Window window && !window.IsActive)
        {
            window.Activate();
        }
        TryFocusTextEditBox(box, selectAll);
        // 元素首次布局完成（Loaded）后再补一次聚焦，覆盖“刚创建时未布局导致 Focus 失败”的情况
        box.Loaded += OnEditBoxLoaded;
        var retries = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            if (!IsCurrentEditBox(box))
            {
                timer.Stop(); // 输入框已被提交/移除，停止旧框的聚焦重试
                return;
            }
            if (Keyboard.FocusedElement == box)
            {
                timer.Stop();
                return;
            }
            TryFocusTextEditBox(box, selectAll);
            if (++retries >= 40)
            {
                timer.Stop();
            }
        };
        timer.Start();
        if (Window.GetWindow(this) is Window activatedWindow)
        {
            void OnWindowActivated(object? sender, EventArgs e)
            {
                activatedWindow.Activated -= OnWindowActivated;
                if (IsCurrentEditBox(box) && Keyboard.FocusedElement != box)
                {
                    TryFocusTextEditBox(box, selectAll);
                }
            }
            activatedWindow.Activated += OnWindowActivated;
        }
    }

    private void OnEditBoxLoaded(object sender, RoutedEventArgs e)
    {
        var box = (RichTextBox)sender;
        box.Loaded -= OnEditBoxLoaded;
        if (IsCurrentEditBox(box) && Keyboard.FocusedElement != box)
        {
            TryFocusTextEditBox(box, _textEditSelectAllOnFocus);
        }
    }

    private bool IsCurrentEditBox(RichTextBox box)
        => _editingTextElement is not null && ReferenceEquals(_editingTextElement.Content, box);

    private void TryFocusTextEditBox(RichTextBox box, bool selectAll)
    {
        var focused = box.Focus();
        if (!focused)
        {
            focused = Keyboard.Focus(box) == box;
        }
        if (Window.GetWindow(this) is Window window)
        {
            if (focused)
            {
                window.Title = "PDFReader X"; // 聚焦成功后恢复标题，清除诊断残留
            }
            else if (IsCurrentEditBox(box))
            {
                // 临时诊断：聚焦失败时把原因显示在窗口标题
                window.Title = $"Focus失败: Focusable={box.Focusable} Visible={box.IsVisible} Enabled={box.IsEnabled} Loaded={box.IsLoaded} WinActive={window.IsActive}";
                try
                {
                    var root = _editingTextElement!.Root;
                    var inCanvas = ViewportCanvas.Children.Contains(root);
                    File.AppendAllText(
                        AppContext.BaseDirectory + "focus-debug.log",
                        $"{DateTime.Now:HH:mm:ss.fff} Focus失败 rootInCanvas={inCanvas} viewportLoaded={ViewportCanvas.IsLoaded} viewportChildren={ViewportCanvas.Children.Count} zoom={Zoom} pan={_pan.X:F1},{_pan.Y:F1} boxParent={box.Parent?.GetType().Name ?? "null"}\n");
                }
                catch
                {
                    // 日志失败不影响主流程
                }
            }
        }
        if (focused && selectAll)
        {
            box.SelectAll();
        }
    }

    private void AttachTextEditing(CanvasElement element)
    {
        var box = (RichTextBox)element.Content;
        box.KeyDown += OnTextKeyDown;
        box.LostKeyboardFocus += OnTextLostFocus;
        box.TextChanged += (_, _) => AutoSizeTextEditBox(); // 内容变化时扩展框高，避免滚动条压缩宽度
        // 点击输入框时强制重新获得键盘焦点（兜底：某些情况下 WPF 默认点击聚焦会被上层事件吞掉）
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // 点击位置落在已有选区内：只聚焦不清除选区，避免“点字号后回来选区消失”
            if (!box.Selection.IsEmpty && e.GetPosition(box) is Point click)
            {
                var position = box.GetPositionFromPoint(click, true);
                if (position is not null
                    && box.Selection.Start.CompareTo(position) <= 0
                    && box.Selection.End.CompareTo(position) >= 0)
                {
                    Keyboard.Focus(box);
                    e.Handled = true;
                    return;
                }
            }
            Keyboard.Focus(box);
        };
    }

    private void OnTextKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Key == Key.B)
            {
                ToggleBold((RichTextBox)sender);
                AutoSizeTextEditBox();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.I)
            {
                ToggleItalic((RichTextBox)sender);
                AutoSizeTextEditBox();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.U)
            {
                ToggleUnderline((RichTextBox)sender);
                AutoSizeTextEditBox();
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true; // 阻止 RichTextBox 插入换行
            CommitTextElement();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelTextElement();
        }
        // Shift+Enter 放行给 RichTextBox 默认处理：插入软换行
    }

    /// <summary>根据输入框内坐标计算最近的光标插入位置（OneNote 式点击定位）。</summary>
    private static TextPointer GetTextPositionFromPoint(RichTextBox box, Point localPoint)
        => box.GetPositionFromPoint(localPoint, true);

    /// <summary>把格式应用到选区，并强制刷新选区高亮（失焦状态下修改字号后阴影可能不跟随重排）。</summary>
    private static void ApplySelectionFormat(RichTextBox box, DependencyProperty property, object? value)
    {
        var start = box.Selection.Start;
        var end = box.Selection.End;
        box.Selection.ApplyPropertyValue(property, value);
        if (start.CompareTo(end) != 0)
        {
            box.Selection.Select(start, end);
        }
    }

    private static void ToggleBold(RichTextBox box)
    {
        var current = box.Selection.GetPropertyValue(TextElement.FontWeightProperty);
        var isBold = current is FontWeight weight && weight == FontWeights.Bold;
        ApplySelectionFormat(box, TextElement.FontWeightProperty, isBold ? FontWeights.Normal : FontWeights.Bold);
    }

    private static void ToggleItalic(RichTextBox box)
    {
        var current = box.Selection.GetPropertyValue(TextElement.FontStyleProperty);
        var isItalic = current is FontStyle style && style == FontStyles.Italic;
        ApplySelectionFormat(box, TextElement.FontStyleProperty, isItalic ? FontStyles.Normal : FontStyles.Italic);
    }

    private static void ToggleUnderline(RichTextBox box)
    {
        var current = box.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
        var isUnderline = current is TextDecorationCollection { Count: > 0 };
        ApplySelectionFormat(box, Inline.TextDecorationsProperty, isUnderline ? null : TextDecorations.Underline);
    }

    /// <summary>工具栏加粗按钮：作用于正在编辑的文本框。</summary>
    public void ToggleEditBold()
    {
        if (_editingTextElement?.Content is RichTextBox box)
        {
            ToggleBold(box);
            AutoSizeTextEditBox();
        }
    }

    /// <summary>工具栏斜体按钮：作用于正在编辑的文本框。</summary>
    public void ToggleEditItalic()
    {
        if (_editingTextElement?.Content is RichTextBox box)
        {
            ToggleItalic(box);
            AutoSizeTextEditBox();
        }
    }

    /// <summary>工具栏下划线按钮：作用于正在编辑的文本框。</summary>
    public void ToggleEditUnderline()
    {
        if (_editingTextElement?.Content is RichTextBox box)
        {
            ToggleUnderline(box);
            AutoSizeTextEditBox();
        }
    }

    /// <summary>编辑框高度跟随内容自动扩展（只增不减），与提交后 TextBlock 高度一致，避免位移。</summary>
    public void AutoSizeTextEditBox()
    {
        if (_editingTextElement is not { } element || element.Content is not RichTextBox box)
        {
            return;
        }
        var width = Math.Max(40, element.WorldWidth);
        var contentHeight = MeasureTextBlockHeight(ExtractRuns(box), width - 8);
        var height = Math.Max(contentHeight, element.WorldHeight);
        if (Math.Abs(element.Root.Height - height) > 0.5)
        {
            element.Root.Height = height;
            element.WorldHeight = height;
        }
    }

    /// <summary>用与提交后 TextBlock 相同的参数测量分段文本所需高度。</summary>
    private static double MeasureTextBlockHeight(IReadOnlyList<TextRunInfo> runs, double width)
    {
        var probe = new TextBlock { TextWrapping = TextWrapping.Wrap };
        foreach (var run in runs)
        {
            probe.Inlines.Add(CreateRun(run));
        }
        probe.Measure(new Size(Math.Max(1, width), double.PositiveInfinity));
        return Math.Max(20, probe.DesiredSize.Height + 8);
    }

    /// <summary>新焦点是否落在窗口工具栏内（格式控件），用于编辑中切换格式不中断。</summary>
    private static bool IsInToolbar(DependencyObject node)
    {
        while (node is not null)
        {
            if (node is ToolBar || node is FrameworkElement { Name: "FileToolbar" or "DrawingToolbar" })
            {
                return true;
            }
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OnTextLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var box = (RichTextBox)sender;
        // 焦点移到工具栏（颜色/字号/B/I/U 等格式控件）：保持编辑状态，等待格式生效
        if (IsCurrentEditBox(box) && e.NewFocus is DependencyObject newFocus && IsInToolbar(newFocus))
        {
            return;
        }
        // 空输入框焦点意外丢失到画布/窗口（非控件）：延迟补回键盘焦点，避免“输不进去”
        var isEmpty = string.IsNullOrWhiteSpace(new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text);
        if (IsCurrentEditBox(box) && isEmpty && e.NewFocus is not Control)
        {
            var retryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            retryTimer.Tick += (_, _) =>
            {
                retryTimer.Stop();
                if (_editingTextElement is not null && Keyboard.FocusedElement != box)
                {
                    TryFocusTextEditBox(box, selectAll: true);
                }
            };
            retryTimer.Start();
            return;
        }
        if (!isEmpty)
        {
            CommitTextElement();
        }
    }

    private void CommitTextElement()
    {
        var element = _editingTextElement;
        if (element is null)
        {
            return;
        }
        var wasNew = _editingTextIsNew;
        _editingTextElement = null;
        var textBox = (RichTextBox)element.Content;
        var newRuns = ExtractRuns(textBox);
        var newText = BuildPlainText(newRuns);
        // 整段默认字段取第一个分段，兼容旧版读取
        element.FontSize = newRuns.Count > 0 ? newRuns[0].FontSize : element.FontSize;
        element.Weight = newRuns.Count > 0 ? newRuns[0].Weight : element.Weight;
        element.Style = newRuns.Count > 0 ? newRuns[0].Style : element.Style;
        element.Decorations = newRuns.Count > 0 ? newRuns[0].Decorations : element.Decorations;
        element.Color = newRuns.Count > 0 ? newRuns[0].Color : element.Color;

        if (string.IsNullOrWhiteSpace(newText))
        {
            if (wasNew)
            {
                ViewportCanvas.Children.Remove(element.Root);
            }
            else
            {
                _elements.Add(element);
                SetTextContent(element, element.TextRuns ?? new List<TextRunInfo>());
            }
            return;
        }

        if (wasNew)
        {
            SetTextContent(element, newRuns);
            RegisterElement(element);
            RecordUndo(
                undo: () => RemoveElementInternal(element),
                redo: () => AddElementInternal(element));
        }
        else
        {
            var oldRuns = element.TextRuns ?? new List<TextRunInfo>();
            SetTextContent(element, newRuns);
            _elements.Add(element);
            RecordUndo(
                undo: () => SetTextContent(element, oldRuns),
                redo: () => SetTextContent(element, newRuns));
        }
    }

    private void CancelTextElement()
    {
        var element = _editingTextElement;
        if (element is null)
        {
            return;
        }
        var wasNew = _editingTextIsNew;
        _editingTextElement = null;
        if (wasNew)
        {
            ViewportCanvas.Children.Remove(element.Root);
        }
        else
        {
            _elements.Add(element);
            SetTextContent(element, element.TextRuns ?? new List<TextRunInfo>());
        }
    }

    private void StartTextEdit(CanvasElement element, Point? viewportPoint = null)
    {
        if (_editingTextElement is not null)
        {
            return;
        }
        Deselect();
        _elements.Remove(element); // 编辑期间暂时移出正式列表，提交/取消时再恢复
        element.TextRuns ??= new List<TextRunInfo>
        {
            new() { Text = element.Text, FontSize = element.FontSize, Weight = element.Weight, Style = element.Style, Decorations = element.Decorations, Color = element.Color },
        }; // 旧版元素进入编辑前先转为分段格式，撤销时能恢复原状
        var box = CreateTextEditBox(element.TextRuns, element);
        element.Root.Width = Math.Max(40, element.WorldWidth); // 保持原框宽，避免编辑/提交后重新换行造成位移
        element.Root.Height = Math.Max(28, element.WorldHeight);
        element.Root.Children.Clear();
        element.Root.Children.Add(box);
        ApplyTextEditBorder(element.Root);
        element.Content = box;
        element.Root.UpdateLayout(); // 强制同步布局，确保 Loaded 立即触发、Focus 可用
        _editingTextElement = element;
        _editingTextIsNew = false;
        AttachTextEditing(element);
        AddResizeThumbs(element, element.Root);
        FocusTextEditBox(box, selectAll: viewportPoint is null);
        if (viewportPoint is Point point)
        {
            box.UpdateLayout();
            var local = RootGrid.TranslatePoint(point, box);
            box.CaretPosition = GetTextPositionFromPoint(box, local);
        }
    }

    /// <summary>按分段格式创建渲染用 TextBlock（与编辑框相同的边距与换行）。</summary>
    private static TextBlock CreateTextBlock(CanvasElement element, double width)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4), // 与编辑时一致，避免提交后文字偏移
        };
        var runs = element.TextRuns;
        if (runs is null || runs.Count == 0)
        {
            block.Text = element.Text;
            block.FontSize = element.FontSize;
            block.FontWeight = element.Weight;
            block.FontStyle = element.Style;
            block.TextDecorations = element.Decorations;
            block.Foreground = new SolidColorBrush(element.Color);
        }
        else
        {
            foreach (var run in runs)
            {
                block.Inlines.Add(CreateRun(run));
            }
        }
        block.Measure(new Size(Math.Max(1, width), double.PositiveInfinity));
        return block;
    }

    private void SetTextContent(CanvasElement element, string text)
        => SetTextContent(element, new List<TextRunInfo>
        {
            new() { Text = NormalizeNewlines(text), FontSize = element.FontSize, Weight = element.Weight, Style = element.Style, Decorations = element.Decorations, Color = element.Color },
        });

    private void SetTextContent(CanvasElement element, IReadOnlyList<TextRunInfo> runs)
    {
        element.Text = BuildPlainText(runs);
        element.TextRuns = runs.ToList();
        var width = Math.Max(40, element.WorldWidth); // 与编辑时一致，提交前后无宽度变化
        var block = CreateTextBlock(element, Math.Max(1, width - 8));
        var contentHeight = Math.Max(20, block.DesiredSize.Height + 8);
        // 高度始终保留编辑时的框高（文字超出时扩展），保证提交前后几何完全一致、无位移
        var height = Math.Max(contentHeight, element.WorldHeight);
        element.Root.Width = width;
        element.Root.Height = height;
        element.WorldWidth = width;
        element.WorldHeight = height;
        element.Root.Children.Clear();
        element.Root.Children.Add(block);
        element.Content = block;
        UpdateElementLayout(element);
        UpdateSelectionAdorner();
    }

    // ---------- .pdfrx 持久化 ----------

    /// <summary>导出全部元素与图片字节（图片文件名对应 images/ 包内路径）。</summary>
    public (List<CanvasElementData> Elements, Dictionary<string, byte[]> Images) ExportElements()
    {
        var elements = new List<CanvasElementData>();
        var images = new Dictionary<string, byte[]>();
        var imageCounter = 0;
        foreach (var element in _elements)
        {
            if (element.IsText)
            {
                elements.Add(new CanvasElementData
                {
                    Type = "text",
                    Text = element.Text,
                    X = element.WorldX,
                    Y = element.WorldY,
                    Width = element.WorldWidth,
                    Height = element.WorldHeight,
                    FontSize = element.FontSize,
                    Bold = element.Weight == FontWeights.Bold,
                    Italic = element.Style == FontStyles.Italic,
                    Underline = element.Decorations is { Count: > 0 },
                    Color = element.Color.ToString(),
                    TextRuns = element.TextRuns?.Select(r => new CanvasTextRunData
                    {
                        Text = r.Text,
                        FontSize = r.FontSize,
                        Bold = r.Weight == FontWeights.Bold,
                        Italic = r.Style == FontStyles.Italic,
                        Underline = r.Decorations is { Count: > 0 },
                        Color = r.Color.ToString(),
                    }).ToList(),
                });
            }
            else if (element.ImageSource is not null)
            {
                var fileName = $"img_{imageCounter++:d3}.png";
                images[fileName] = EncodePng(element.ImageSource);
                elements.Add(new CanvasElementData
                {
                    Type = "image",
                    ImageFile = fileName,
                    X = element.WorldX,
                    Y = element.WorldY,
                    Width = element.WorldWidth,
                    Height = element.WorldHeight,
                });
            }
        }
        return (elements, images);
    }

    /// <summary>导出指定页墨迹（ISF 字节），无墨迹返回 null。</summary>
    public byte[]? ExportPageInk(int pageIndex)
    {
        if (!_inkCanvases.TryGetValue(pageIndex, out var ink) || ink.Strokes.Count == 0)
        {
            return null;
        }
        using var stream = new MemoryStream();
        ink.Strokes.Save(stream);
        return stream.ToArray();
    }

    /// <summary>导出自由画布墨迹（ISF 字节），无墨迹返回 null。</summary>
    public byte[]? ExportFreeInk()
    {
        if (_freeInk is null || _freeInk.Strokes.Count == 0)
        {
            return null;
        }
        using var stream = new MemoryStream();
        _freeInk.Strokes.Save(stream);
        return stream.ToArray();
    }

    /// <summary>恢复 .pdfrx 包中的完整画布状态：墨迹、元素、缩放与平移。</summary>
    public void RestoreFromPackage(PdfrxPackage package)
    {
        ClearElements();

        foreach (var (index, bytes) in package.PageInks)
        {
            if (_inkCanvases.TryGetValue(index, out var ink))
            {
                ink.Strokes = LoadStrokes(bytes);
            }
        }
        if (package.FreeInk is not null && _freeInk is not null)
        {
            _freeInk.Strokes = LoadStrokes(package.FreeInk);
        }

        foreach (var data in package.Elements)
        {
            ImportElement(data, package.Images);
        }

        Zoom = package.Zoom;
        _pan = new Point(package.PanX, package.PanY);
        UpdatePanTransform();
        UpdateCurrentPage();
        UpdateScrollState();
        ScheduleRerender();
        ResetModified();
    }

    private static StrokeCollection LoadStrokes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return new StrokeCollection(stream);
    }

    private void ImportElement(CanvasElementData data, IReadOnlyDictionary<string, byte[]> images)
    {
        if (data.Type == "image" && data.ImageFile is not null && images.TryGetValue(data.ImageFile, out var imageBytes))
        {
            var source = LoadBitmap(imageBytes);
            var image = new Image { Source = source, Stretch = Stretch.Uniform };
            var element = new CanvasElement
            {
                Root = CreateElementRoot(Math.Max(40, data.Width), Math.Max(28, data.Height)),
                Content = image,
                ImageSource = source,
                WorldX = data.X,
                WorldY = data.Y,
                WorldWidth = Math.Max(40, data.Width),
                WorldHeight = Math.Max(28, data.Height),
            };
            element.Root.Children.Add(image);
            AddElementInternal(element);
            return;
        }

        if (data.Type == "text")
        {
            var element = new CanvasElement
            {
                Root = CreateElementRoot(Math.Max(40, data.Width), Math.Max(28, data.Height)),
                Content = new TextBlock(),
                IsText = true,
                Text = data.Text ?? string.Empty,
                FontSize = data.FontSize > 0 ? data.FontSize : DefaultTextFontSize,
                Weight = data.Bold ? FontWeights.Bold : FontWeights.Normal,
                Style = data.Italic ? FontStyles.Italic : FontStyles.Normal,
                Decorations = data.Underline ? TextDecorations.Underline : null,
                Color = ParseColor(data.Color),
                TextRuns = data.TextRuns is { Count: > 0 }
                    ? data.TextRuns.Select(r => new TextRunInfo
                    {
                        Text = NormalizeNewlines(r.Text),
                        FontSize = r.FontSize > 0 ? r.FontSize : DefaultTextFontSize,
                        Weight = r.Bold ? FontWeights.Bold : FontWeights.Normal,
                        Style = r.Italic ? FontStyles.Italic : FontStyles.Normal,
                        Decorations = r.Underline ? TextDecorations.Underline : null,
                        Color = ParseColor(r.Color),
                    }).ToList()
                    : null,
                WorldX = data.X,
                WorldY = data.Y,
                WorldWidth = Math.Max(40, data.Width),
                WorldHeight = Math.Max(28, data.Height),
            };
            SetTextContent(element, element.TextRuns ?? new List<TextRunInfo> { new() { Text = NormalizeNewlines(element.Text) } });
            AddElementInternal(element);
        }
    }

    private static BitmapSource LoadBitmap(byte[] bytes)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Color ParseColor(string value)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch
        {
            return Colors.Black;
        }
    }
}
