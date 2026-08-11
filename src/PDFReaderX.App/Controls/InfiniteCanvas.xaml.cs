using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using PDFReaderX.App.Helpers;
using PDFReaderX.App.ViewModels;
using PDFReaderX.Core.Services;
using PDFReaderX.Core.Utilities;

namespace PDFReaderX.App.Controls;

/// <summary>
/// 无限画布：PDF 页面位图层 + 每页墨迹层（InkCanvas），统一缩放/平移。
/// 页面按视口按需渲染，位图 LRU 缓存；墨迹坐标始终基于 zoom=1 的页面坐标系。
/// </summary>
public partial class InfiniteCanvas : UserControl
{
    public const double MinZoom = 0.25;
    public const double MaxZoom = 4.0;
    private const double MaxRenderDpi = 240;
    private int MaxCachedPages => PerformanceMode ? 3 : 6; // 性能模式减少缓存页数，降低内存占用
    private const double WheelScrollStep = 40;

    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(PdfRenderService), typeof(InfiniteCanvas),
        new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty PagesProperty = DependencyProperty.Register(
        nameof(Pages), typeof(ObservableCollection<PageViewModel>), typeof(InfiniteCanvas),
        new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(InfiniteCanvas),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnZoomChanged));

    public static readonly DependencyProperty ActiveToolProperty = DependencyProperty.Register(
        nameof(ActiveTool), typeof(InkTool), typeof(InfiniteCanvas),
        new PropertyMetadata(InkTool.Pen, OnToolChanged));

    public static readonly DependencyProperty PenColorProperty = DependencyProperty.Register(
        nameof(PenColor), typeof(Color), typeof(InfiniteCanvas),
        new PropertyMetadata(Colors.Black, OnToolChanged));

    public static readonly DependencyProperty PenWidthProperty = DependencyProperty.Register(
        nameof(PenWidth), typeof(double), typeof(InfiniteCanvas),
        new PropertyMetadata(3.0, OnToolChanged));

    public static readonly DependencyProperty HighlightColorProperty = DependencyProperty.Register(
        nameof(HighlightColor), typeof(Color), typeof(InfiniteCanvas),
        new PropertyMetadata(Colors.Yellow, OnToolChanged));

    public static readonly DependencyProperty HighlightWidthProperty = DependencyProperty.Register(
        nameof(HighlightWidth), typeof(double), typeof(InfiniteCanvas),
        new PropertyMetadata(24.0, OnToolChanged));

    public static readonly DependencyProperty EraserWidthProperty = DependencyProperty.Register(
        nameof(EraserWidth), typeof(double), typeof(InfiniteCanvas),
        new PropertyMetadata(16.0, OnToolChanged));

    public static readonly DependencyProperty TextBorderStyleProperty = DependencyProperty.Register(
        nameof(TextBorderStyle), typeof(string), typeof(InfiniteCanvas),
        new PropertyMetadata("black-dashed", OnTextBorderStyleChanged));

    private static void OnTextBorderStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((InfiniteCanvas)d).RefreshElementBorders();
    }

    public static readonly DependencyProperty TextFontSizeProperty = DependencyProperty.Register(
        nameof(TextFontSize), typeof(double), typeof(InfiniteCanvas),
        new PropertyMetadata(14.0, OnTextFontSizeChanged));

    public static readonly DependencyProperty PerformanceModeProperty = DependencyProperty.Register(
        nameof(PerformanceMode), typeof(bool), typeof(InfiniteCanvas),
        new PropertyMetadata(false, OnPerformanceModeChanged));

    public static readonly DependencyProperty CurrentPageIndexProperty = DependencyProperty.Register(
        nameof(CurrentPageIndex), typeof(int), typeof(InfiniteCanvas),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ScrollOffsetProperty = DependencyProperty.Register(
        nameof(ScrollOffset), typeof(double), typeof(InfiniteCanvas),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnScrollOffsetChanged));

    public static readonly DependencyProperty MaxScrollOffsetProperty = DependencyProperty.Register(
        nameof(MaxScrollOffset), typeof(double), typeof(InfiniteCanvas),
        new PropertyMetadata(0.0));

    public static readonly DependencyProperty ViewportHeightProperty = DependencyProperty.Register(
        nameof(ViewportHeight), typeof(double), typeof(InfiniteCanvas),
        new PropertyMetadata(0.0));

    private readonly Dictionary<int, Border> _pageElements = new();
    private readonly Dictionary<int, InkCanvas> _inkCanvases = new();
    private readonly Dictionary<InkCanvas, int> _inkPageIndex = new();
    private InkCanvas? _freeInk;
    private InkCanvas? _liveInk;
    private readonly Dictionary<int, Rectangle> _imagesByPage = new();
    private readonly Dictionary<int, BitmapSource> _bitmapCache = new();
    private readonly List<int> _cacheOrder = new();
    private readonly HashSet<int> _pendingRenders = new();

    private Point _pan;
    private bool _isMousePanning;
    private Point _lastMousePanPoint;
    private bool _touchActive;
    private bool _eraserActive; // 手动跨层橡皮擦：拖动中持续擦除所有图层
    private int _documentEpoch;
    private bool _layoutDirty;
    private DispatcherTimer? _rerenderTimer;
    private const double FreeInkLayerSize = 4_000_000;

    public InfiniteCanvas()
    {
        InitializeComponent();
    }

    public PdfRenderService? Document
    {
        get => (PdfRenderService?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public ObservableCollection<PageViewModel>? Pages
    {
        get => (ObservableCollection<PageViewModel>?)GetValue(PagesProperty);
        set => SetValue(PagesProperty, value);
    }

    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public InkTool ActiveTool
    {
        get => (InkTool)GetValue(ActiveToolProperty);
        set => SetValue(ActiveToolProperty, value);
    }

    public Color PenColor
    {
        get => (Color)GetValue(PenColorProperty);
        set => SetValue(PenColorProperty, value);
    }

    public double PenWidth
    {
        get => (double)GetValue(PenWidthProperty);
        set => SetValue(PenWidthProperty, value);
    }

    public Color HighlightColor
    {
        get => (Color)GetValue(HighlightColorProperty);
        set => SetValue(HighlightColorProperty, value);
    }

    public double HighlightWidth
    {
        get => (double)GetValue(HighlightWidthProperty);
        set => SetValue(HighlightWidthProperty, value);
    }

    public double EraserWidth
    {
        get => (double)GetValue(EraserWidthProperty);
        set => SetValue(EraserWidthProperty, value);
    }

    public string TextBorderStyle
    {
        get => (string)GetValue(TextBorderStyleProperty);
        set => SetValue(TextBorderStyleProperty, value);
    }

    public double TextFontSize
    {
        get => (double)GetValue(TextFontSizeProperty);
        set => SetValue(TextFontSizeProperty, value);
    }

    /// <summary>性能模式：降低渲染清晰度上限与缓存页数（滚动缩放更流畅）。</summary>
    public bool PerformanceMode
    {
        get => (bool)GetValue(PerformanceModeProperty);
        set => SetValue(PerformanceModeProperty, value);
    }

    private static void OnPerformanceModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (InfiniteCanvas)d;
        canvas.ClearPageBitmapCache(); // 旧分辨率位图全部作废，按新模式重新渲染
        canvas.LayoutPages();
        canvas.ScheduleRerender();
    }

    private static void OnTextFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (InfiniteCanvas)d;
        if (canvas._editingTextElement?.Content is RichTextBox box)
        {
            ApplySelectionFormat(box, TextElement.FontSizeProperty, (double)e.NewValue); // 编辑中改字号：作用于选中部分，无选中时作用于后续输入
            canvas.AutoSizeTextEditBox(); // 字号变化后同步扩展框高
        }
    }

    public int CurrentPageIndex
    {
        get => (int)GetValue(CurrentPageIndexProperty);
        set => SetValue(CurrentPageIndexProperty, value);
    }

    /// <summary>垂直滚动偏移：内容顶部相对视口顶部的距离（即 -_pan.Y）。</summary>
    /// <summary>画布水平平移量（世界坐标偏移，供 .pdfrx 保存）。</summary>
    public double PanX => _pan.X;

    /// <summary>画布垂直平移量（世界坐标偏移，供 .pdfrx 保存）。</summary>
    public double PanY => _pan.Y;

    public double ScrollOffset
    {
        get => (double)GetValue(ScrollOffsetProperty);
        set => SetValue(ScrollOffsetProperty, value);
    }

    /// <summary>垂直可滚动范围上限（内容总高 - 视口高，不小于 0）。</summary>
    public double MaxScrollOffset
    {
        get => (double)GetValue(MaxScrollOffsetProperty);
        set => SetValue(MaxScrollOffsetProperty, value);
    }

    /// <summary>视口高度，供右侧滚动条滑块比例使用。</summary>
    public double ViewportHeight
    {
        get => (double)GetValue(ViewportHeightProperty);
        set => SetValue(ViewportHeightProperty, value);
    }

    /// <summary>回到 100% 缩放并居中显示第一页顶部。</summary>
    public void ResetView()
    {
        _pan = default;
        Zoom = 1.0;
        UpdatePanTransform();
        LayoutPages();
        ScheduleRerender();
        UpdateCurrentPage();
        UpdateScrollState();
    }

    /// <summary>缩放到指定比例，以屏幕中心为缩放中心。</summary>
    public void ZoomTo(double newZoom)
    {
        ApplyZoom(newZoom, new Point(ActualWidth / 2, ActualHeight / 2));
    }

    /// <summary>跳转到指定页：页面顶部对齐视口顶部，水平保持居中。</summary>
    public void GoToPage(int pageIndex)
    {
        if (Pages is null || Pages.Count == 0)
        {
            return;
        }

        var index = Math.Clamp(pageIndex, 0, Pages.Count - 1);
        if (!_pageElements.TryGetValue(index, out var element))
        {
            return;
        }

        _pan = new Point(0, -Canvas.GetTop(element));
        UpdatePanTransform();
        ScheduleRerender();
        SetCurrentPage(index);
        UpdateScrollState();
    }

    /// <summary>根据视口中心推算当前页，并写入 CurrentPageIndex（仅在变化时通知）。</summary>
    private void UpdateCurrentPage()
    {
        if (Pages is null || Pages.Count == 0)
        {
            SetCurrentPage(0);
            return;
        }

        var contentY = ActualHeight / 2 - _pan.Y;
        var current = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < Pages.Count; i++)
        {
            var rect = GetPageRect(Pages[i]);
            if (rect.Top <= contentY && contentY < rect.Bottom)
            {
                current = i;
                break;
            }
            var distance = Math.Min(Math.Abs(contentY - rect.Top), Math.Abs(contentY - rect.Bottom));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                current = i;
            }
        }
        SetCurrentPage(current);
    }

    private void SetCurrentPage(int index)
    {
        if (CurrentPageIndex != index)
        {
            CurrentPageIndex = index;
            MarkModified();
        }
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (InfiniteCanvas)d;
        if (e.OldValue is ObservableCollection<PageViewModel> oldPages)
        {
            oldPages.CollectionChanged -= canvas.OnPagesCollectionChanged;
        }
        if (e.NewValue is ObservableCollection<PageViewModel> newPages)
        {
            newPages.CollectionChanged += canvas.OnPagesCollectionChanged;
        }
        canvas.RebuildPages();
    }

    private void OnPagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            RebuildPages();
            return;
        }

        if (e.NewItems is not null)
        {
            foreach (PageViewModel page in e.NewItems)
            {
                var element = CreatePageElement(page);
                ViewportCanvas.Children.Add(element);
                _pageElements[page.PageIndex] = element;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (PageViewModel page in e.OldItems)
            {
                if (_pageElements.Remove(page.PageIndex, out var removed))
                {
                    ViewportCanvas.Children.Remove(removed);
                }
                _inkCanvases.Remove(page.PageIndex);
                _imagesByPage.Remove(page.PageIndex);
            }
        }

        QueueLayoutPass();
    }

    private void QueueLayoutPass()
    {
        if (_layoutDirty)
        {
            return;
        }

        _layoutDirty = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _layoutDirty = false;
            LayoutPages();
            UpdateEditingState();
            ScheduleRerender();
        });
    }

    private static void OnZoomChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (InfiniteCanvas)d;
        canvas.UpdatePanTransform();
        canvas.LayoutPages();
        canvas.ScheduleRerender();
    }

    private static void OnToolChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (InfiniteCanvas)d;
        if (e.Property == ActiveToolProperty)
        {
            canvas.CommitTextElement(); // 仅切换工具时提交/清理未完成的输入框
        }
        else if (e.Property == PenColorProperty && canvas._editingTextElement?.Content is RichTextBox box)
        {
            ApplySelectionFormat(box, TextElement.ForegroundProperty, new SolidColorBrush(canvas.PenColor)); // 编辑中改颜色：作用于选中部分，无选中时作用于后续输入
        }
        canvas.UpdateEditingState();
        canvas.Cursor = canvas.ActiveTool switch
        {
            InkTool.Select => Cursors.Hand,
            InkTool.Text => Cursors.IBeam,
            InkTool.Lasso => Cursors.Cross,
            _ => Cursors.Arrow,
        };
    }

    private void RebuildPages()
    {
        ViewportCanvas.Children.Clear();
        _freeInk = null;
        _liveInk = null;
        _pageElements.Clear();
        _inkCanvases.Clear();
        _inkPageIndex.Clear();
        _imagesByPage.Clear();
        _bitmapCache.Clear();
        _cacheOrder.Clear();
        _pendingRenders.Clear();
        _documentEpoch++;
        _pan = default;
        UpdatePanTransform();
        ScrollOffset = 0;

        ClearElements();
        ClearUndoHistory();
        CreateFreeInkLayer();
        CreateLiveInkLayer();

        if (Pages is null)
        {
            return;
        }

        foreach (var page in Pages)
        {
            var element = CreatePageElement(page);
            ViewportCanvas.Children.Add(element);
            _pageElements[page.PageIndex] = element;
        }

        LayoutPages();
        UpdateEditingState();
        ScheduleRerender();
        UpdateCurrentPage();
    }

    /// <summary>
    /// 创建铺满画布的底层自由墨迹层：页面之间及画布空白区域也可书写。
    /// 页面元素通过 ZIndex 覆盖在其上；墨迹随画布统一平移缩放。
    /// </summary>
    private void CreateFreeInkLayer()
    {
        const double size = FreeInkLayerSize;
        var ink = new InkCanvas
        {
            Width = size,
            Height = size,
            Background = Brushes.Transparent,
        };
        Canvas.SetLeft(ink, -size / 2);
        Canvas.SetTop(ink, -size / 2);
        // 缩放以画布中心（世界原点）为锚点，保证自由墨迹与页面墨迹缩放时不发生相对漂移
        ink.RenderTransformOrigin = new Point(0.5, 0.5);
        Panel.SetZIndex(ink, int.MinValue);
        ink.PreviewTouchDown += OnInkPreviewTouchDown;
        ink.StrokeCollected += OnStrokeCollected;
        ink.StrokeErasing += OnStrokeErasing;
        ViewportCanvas.Children.Add(ink);
        _freeInk = ink;
        ApplyToolToInk(ink);
    }

    /// <summary>实时墨迹覆盖层：钢笔/荧光笔绘制时置于最上层，跨页与自由画布的笔画绘制过程中实时可见，松手后拆分到各层。</summary>
    private void CreateLiveInkLayer()
    {
        var ink = new InkCanvas
        {
            Width = FreeInkLayerSize,
            Height = FreeInkLayerSize,
            Background = Brushes.Transparent,
        };
        Canvas.SetLeft(ink, -FreeInkLayerSize / 2);
        Canvas.SetTop(ink, -FreeInkLayerSize / 2);
        ink.RenderTransformOrigin = new Point(0.5, 0.5);
        Panel.SetZIndex(ink, int.MaxValue);
        ink.PreviewTouchDown += OnInkPreviewTouchDown;
        ink.StrokeCollected += OnStrokeCollected;
        ViewportCanvas.Children.Add(ink);
        _liveInk = ink;
        ApplyToolToInk(ink);
    }

    private Border CreatePageElement(PageViewModel page)
    {
        var pageBrush = new ImageBrush { Stretch = Stretch.Fill };
        var image = new Rectangle
        {
            Fill = pageBrush,
            Width = page.BaseWidth,
            Height = page.BaseHeight,
            IsHitTestVisible = false,
        };
        var ink = new InkCanvas
        {
            Background = Brushes.Transparent,
            Strokes = page.Strokes,
        };
        ink.PreviewTouchDown += OnInkPreviewTouchDown;
        ink.StrokeCollected += OnStrokeCollected;
        ink.StrokeErasing += OnStrokeErasing;

        var grid = new Grid();
        grid.Children.Add(image);
        grid.Children.Add(ink);

        var border = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderThickness = new Thickness(1),
            Child = grid,
            Width = page.BaseWidth,
            Height = page.BaseHeight,
            RenderTransform = new ScaleTransform(Zoom, Zoom),
        };

        ApplyToolToInk(ink);
        _inkCanvases[page.PageIndex] = ink;
        _inkPageIndex[ink] = page.PageIndex;
        _imagesByPage[page.PageIndex] = image;
        return border;
    }

    private double _docOffsetX; // 文档整体水平居中偏移（并入平移量，保证缩放时页面与元素相对位置不变）

    /// <summary>文档水平居中偏移：文档宽度小于视口时居中，否则贴左。</summary>
    private double GetDocOffsetX(double zoom)
    {
        var docWidth = 0.0;
        if (Pages is not null)
        {
            foreach (var page in Pages)
            {
                docWidth = Math.Max(docWidth, page.BaseWidth);
            }
        }
        return Math.Max(0, (ActualWidth - docWidth * zoom) / 2);
    }

    /// <summary>平移总量 = 手动平移 + 文档居中偏移，统一各调用点。</summary>
    private void UpdatePanTransform()
    {
        MarkModified();
        PanTransform.X = _pan.X + _docOffsetX;
        PanTransform.Y = _pan.Y;
    }

    private void LayoutPages()
    {
        UpdateElementsLayout();
        if (Pages is null || Pages.Count == 0)
        {
            UpdateScrollState();
            return;
        }

        _docOffsetX = GetDocOffsetX(Zoom);

        var heights = new double[Pages.Count];
        for (var i = 0; i < Pages.Count; i++)
        {
            heights[i] = Pages[i].BaseHeight * Zoom;
        }

        var offsets = PageLayout.ComputeTopOffsets(heights, 0);
        if (_freeInk is not null)
        {
            _freeInk.RenderTransform = new ScaleTransform(Zoom, Zoom);
        }
        if (_liveInk is not null)
        {
            _liveInk.RenderTransform = new ScaleTransform(Zoom, Zoom);
        }
        for (var i = 0; i < Pages.Count; i++)
        {
            var page = Pages[i];
            if (!_pageElements.TryGetValue(page.PageIndex, out var element))
            {
                continue;
            }

            // 页面水平定位统一在文档原点（居中偏移并入平移量），保证缩放时页面与元素不产生相对位移
            Canvas.SetLeft(element, 0);
            Canvas.SetTop(element, offsets[i]);
            element.RenderTransform = new ScaleTransform(Zoom, Zoom);
        }
        UpdatePanTransform();
        UpdateScrollState();
    }

    private Rect GetPageRect(PageViewModel page)
    {
        if (_pageElements.TryGetValue(page.PageIndex, out var element))
        {
            return new Rect(
                Canvas.GetLeft(element), Canvas.GetTop(element),
                page.BaseWidth * Zoom, page.BaseHeight * Zoom);
        }
        return Rect.Empty;
    }

    private void ScheduleRerender()
    {
        _rerenderTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _rerenderTimer.Stop();
        _rerenderTimer.Tick -= OnRerenderTick;
        _rerenderTimer.Tick += OnRerenderTick;
        _rerenderTimer.Start();
    }

    private void OnRerenderTick(object? sender, EventArgs e)
    {
        if (_rerenderTimer is not null)
        {
            _rerenderTimer.Stop();
        }
        UpdateVisiblePages();
    }

    private void UpdateVisiblePages()
    {
        if (Pages is null || Pages.Count == 0 || Document is null)
        {
                return;
        }


        var viewport = new Rect(-(_pan.X + _docOffsetX), -_pan.Y, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        viewport.Inflate(120, 120);

        foreach (var page in Pages)
        {
            var visible = GetPageRect(page).IntersectsWith(viewport);
            if (!_imagesByPage.TryGetValue(page.PageIndex, out var image))
            {
                continue;
            }

            if (visible)
            {
                if (((ImageBrush)image.Fill).ImageSource is null)
                {
                    if (_bitmapCache.TryGetValue(page.PageIndex, out var cached))
                    {
                        ((ImageBrush)image.Fill).ImageSource = cached;
                    }
                    else
                    {
                        RenderPageAsync(page);
                    }
                }
            }
            else if (((ImageBrush)image.Fill).ImageSource is not null)
            {
                // 离开视口：释放视觉引用，LRU 缓存仍保留位图
                ((ImageBrush)image.Fill).ImageSource = null;
            }
        }
    }

    /// <summary>清空页面位图缓存并释放视觉引用（渲染分辨率等参数变化时调用）。</summary>
    private void ClearPageBitmapCache()
    {
        foreach (var image in _imagesByPage.Values)
        {
            if (image.Fill is ImageBrush brush)
            {
                brush.ImageSource = null;
            }
        }
        _bitmapCache.Clear();
        _cacheOrder.Clear();
        _pendingRenders.Clear();
        _documentEpoch++; // 使进行中的渲染任务结果作废
    }

    private void RenderPageAsync(PageViewModel page)
    {
        if (Document is null || _pendingRenders.Contains(page.PageIndex))
        {
            return;
        }

        var index = page.PageIndex;
        var dpi = Math.Min(96.0 * Zoom, PerformanceMode ? 144.0 : MaxRenderDpi); // 性能模式降低渲染分辨率上限
        var epoch = _documentEpoch;
        var document = Document; // 在 UI 线程捕获，后台线程不能访问依赖属性
        _pendingRenders.Add(index);

        _ = Task.Run(() =>
        {
            try
            {
                using var bitmap = document.RenderPage(index, (int)dpi);
                var source = bitmap.ToBitmapSource();
                source.Freeze();
                return (BitmapSource?)source;
            }
            catch
            {
                return null;
            }

        }).ContinueWith(t =>
        {
            _pendingRenders.Remove(index);
            if (t.IsFaulted || epoch != _documentEpoch)
            {
                return;
            }

            var source = t.Result;
            if (source is null)
            {
                // 渲染失败：状态栏提示 + 页面显示占位文本
                if (_pageElements.TryGetValue(index, out var element))
                {
                    ShowPageRenderError(element);
                }
                return;
            }

            _bitmapCache[index] = source;
            _cacheOrder.Remove(index);
            _cacheOrder.Add(index);
            while (_cacheOrder.Count > MaxCachedPages)
            {
                var oldest = _cacheOrder[0];
                _cacheOrder.RemoveAt(0);
                _bitmapCache.Remove(oldest);
            }

            if (_imagesByPage.TryGetValue(index, out var image))
            {
                ((ImageBrush)image.Fill).ImageSource = source;
                var imgPos = image.TransformToAncestor(ViewportCanvas).Transform(new System.Windows.Point(0, 0));
                if (_inkCanvases.TryGetValue(index, out var inkRef))
                {
                    var inkPos = inkRef.TransformToAncestor(ViewportCanvas).Transform(new System.Windows.Point(0, 0));
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                {
                    var lateImg = image.TransformToAncestor(ViewportCanvas).Transform(new System.Windows.Point(0, 0));
                });
                }
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static void ShowPageRenderError(Border element)
    {
        var text = new TextBlock
        {
            Text = "该页渲染失败",
            Foreground = new SolidColorBrush(Colors.Red),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (element.Child is Grid grid && grid.Children.Count >= 1)
        {
            grid.Children[0] = text;
        }
    }

    // ---------- 垂直滚动条 ----------

    private static void OnScrollOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (InfiniteCanvas)d;
        canvas.ApplyScrollOffset((double)e.NewValue);
    }

    private void ApplyScrollOffset(double value)
    {
        var clamped = Math.Max(0, value);
        if (Math.Abs(clamped + _pan.Y) < 0.001)
        {
            return;
        }
        _pan.Y = -clamped;
        UpdatePanTransform();
        ScheduleRerender();
        UpdateCurrentPage();
    }

    /// <summary>按当前内容总高与视口高度刷新滚动范围，并把滚动偏移限制在范围内。</summary>
    private void UpdateScrollState()
    {
        var max = 0.0;
        if (Pages is not null && Pages.Count > 0)
        {
            var heights = new double[Pages.Count];
            for (var i = 0; i < Pages.Count; i++)
            {
                heights[i] = Pages[i].BaseHeight * Zoom;
            }
            var offsets = PageLayout.ComputeTopOffsets(heights, 0);
            max = Math.Max(0, offsets[^1] + heights[^1] - ActualHeight);
        }
        MaxScrollOffset = max;
        ScrollOffset = Math.Clamp(ScrollOffset, 0, max);
    }

    // ---------- 缩放与平移 ----------

    private void ApplyZoom(double newZoom, Point center)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - Zoom) < 0.0001)
        {
            return;
        }

        var oldOffsetX = GetDocOffsetX(Zoom);
        var newOffsetX = GetDocOffsetX(newZoom);
        var (x, y) = CanvasTransform.ZoomAt(center.X, center.Y, _pan.X + oldOffsetX, _pan.Y, Zoom, newZoom);
        _pan = new Point(x - newOffsetX, y);
        Zoom = newZoom; // 触发 OnZoomChanged → PanTransform + LayoutPages + 重渲染
        UpdatePanTransform();
        UpdateCurrentPage();
        UpdateScrollState();
    }

    private void PanBy(double dx, double dy)
    {
        if (dx == 0 && dy == 0)
        {
            return;
        }

        _pan = new Point(_pan.X + dx, _pan.Y + dy);
        UpdatePanTransform();
        ScheduleRerender();
        UpdateCurrentPage();
        UpdateScrollState();
    }

    // ---------- 鼠标输入 ----------

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var position = e.GetPosition(RootGrid); // 视口坐标，缩放锚点不受平移影响
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
            ApplyZoom(Zoom * factor, position);
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            PanBy(e.Delta / 120.0 * WheelScrollStep, 0);
        }
        else
        {
            PanBy(0, e.Delta / 120.0 * WheelScrollStep);
        }
        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && ActiveTool == InkTool.Text && !_touchActive)
        {
            var position = e.GetPosition(RootGrid);
            // 点击已有输入框：不重建，放行给 TextBox 获得焦点输入
            if (_editingTextElement is not null && IsPointInElement(position, _editingTextElement))
            {
                return;
            }
            var hadEditor = _editingTextElement is not null;
            if (hadEditor)
            {
                // 先确认当前输入框，再判断点击目标，支持直接切换到另一个文本
                CommitTextElement();
            }
            // 点击已有文本：直接进入编辑（OneNote 风格），光标定位到点击处
            if (FindTextElementAt(position) is { } hit)
            {
                StartTextEdit(hit, position);
                e.Handled = true;
                return;
            }
            if (hadEditor)
            {
                // 有字点其他空白处确认提交，不再自动开新文本框
                e.Handled = true;
                return;
            }
            // 创建新文本框；按住拖动可调整初始大小，松开后聚焦输入
            _textCreateActive = true;
            _textCreateStart = position;
            StartTextElementAt(position);
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left && ActiveTool == InkTool.Select
            && _selectedElement is not null)
        {
            Deselect(); // 点击空白取消选择，随后继续平移
        }

        if (e.ChangedButton == MouseButton.Left && ActiveTool == InkTool.Eraser && !_touchActive)
        {
            _eraserActive = true;
            RootGrid.CaptureMouse();
            EraseAt(e.GetPosition(RootGrid));
            e.Handled = true;
            return;
        }

        var isPanButton = e.ChangedButton == MouseButton.Middle
            || (e.ChangedButton == MouseButton.Left && ActiveTool == InkTool.Select);
        if (isPanButton)
        {
            _isMousePanning = true;
            _lastMousePanPoint = e.GetPosition(this);
            RootGrid.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_eraserActive)
        {
            EraseAt(e.GetPosition(RootGrid));
            e.Handled = true;
            return;
        }
        if (_textCreateActive && _editingTextElement is not null)
        {
            ResizeTextCreate(e.GetPosition(RootGrid));
            e.Handled = true;
            return;
        }
        if (_isMousePanning)
        {
            var point = e.GetPosition(this);
            PanBy(point.X - _lastMousePanPoint.X, point.Y - _lastMousePanPoint.Y);
            _lastMousePanPoint = point;
            e.Handled = true;
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_eraserActive && e.ChangedButton == MouseButton.Left)
        {
            _eraserActive = false;
            RootGrid.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (_textCreateActive)
        {
            _textCreateActive = false;
            if (_editingTextElement is { Content: RichTextBox textBox })
            {
                FocusTextEditBox(textBox); // 拖动（或点击）结束后聚焦输入
            }
            e.Handled = true;
            return;
        }
        if (_isMousePanning
            && (e.ChangedButton == MouseButton.Middle
                || (e.ChangedButton == MouseButton.Left && ActiveTool == InkTool.Select)))
        {
            _isMousePanning = false;
            RootGrid.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ViewportHeight = ActualHeight;
        LayoutPages();
        ScheduleRerender();
        UpdateCurrentPage();
    }

    // ---------- 墨迹撤销 ----------

    private void OnStrokeCollected(object? sender, InkCanvasStrokeCollectedEventArgs e)
    {
        var stroke = e.Stroke;
        var ink = (InkCanvas)sender!;
        if (ReferenceEquals(ink, _freeInk))
        {
            SplitFreeStroke(stroke);
            return;
        }
        if (ReferenceEquals(ink, _liveInk))
        {
            SplitFreeStroke(ink, stroke);
            return;
        }
        if (_inkPageIndex.TryGetValue(ink, out var pageIndex))
        {
            SplitPageStroke(ink, pageIndex, stroke);
            return;
        }
        var strokes = ink.Strokes;
        RecordUndo(
            undo: () => strokes.Remove(stroke),
            redo: () => strokes.Add(stroke));
    }

    /// <summary>把点序列按区域切分为连续段（段内点属于同一区域）。</summary>
    private static List<List<StylusPoint>> SplitByRegion(
        StylusPointCollection points,
        Func<StylusPoint, bool> inRegion)
    {
        var segments = new List<List<StylusPoint>>();
        List<StylusPoint>? current = null;
        var currentIn = false;
        foreach (var point in points)
        {
            var inside = inRegion(point);
            if (current is null || inside != currentIn)
            {
                current = new List<StylusPoint>();
                segments.Add(current);
                currentIn = inside;
            }
            current.Add(point);
        }
        return segments;
    }

    /// <summary>页面笔划跨出页面边界时拆分：页面内段留在页面，页面外段转入自由画布。</summary>
    private void SplitPageStroke(InkCanvas pageInk, int pageIndex, Stroke stroke)
    {
        var page = _pageElements[pageIndex];
        var pageWidth = page.ActualWidth;
        var pageHeight = page.ActualHeight;
        var top = Canvas.GetTop(page);
        var freeOffset = FreeInkLayerSize / 2;

        bool InPage(StylusPoint p) =>
            p.X >= -0.5 && p.X <= pageWidth + 0.5
            && p.Y >= -0.5 && p.Y <= pageHeight + 0.5;

        var segments = SplitByRegion(stroke.StylusPoints, InPage);
        var added = new List<(InkCanvas Ink, Stroke Stroke)>();
        foreach (var segment in segments)
        {
            if (segment.Count == 0)
            {
                continue;
            }
            if (InPage(segment[0]))
            {
                added.Add((pageInk, new Stroke(new StylusPointCollection(segment), stroke.DrawingAttributes)));
            }
            else
            {
                var points = new StylusPointCollection();
                foreach (var p in segment)
                {
                    points.Add(new StylusPoint(p.X + freeOffset + page.BorderThickness.Left, p.Y + top + freeOffset + page.BorderThickness.Top, p.PressureFactor));
                }
                added.Add((_freeInk!, new Stroke(points, stroke.DrawingAttributes)));
            }
        }

        pageInk.Strokes.Remove(stroke);
        foreach (var (target, s) in added)
        {
            target.Strokes.Add(s);
        }
        RecordUndo(
            undo: () =>
            {
                pageInk.Strokes.Add(stroke);
                foreach (var (target, s) in added)
                {
                    target.Strokes.Remove(s);
                }
            },
            redo: () =>
            {
                pageInk.Strokes.Remove(stroke);
                foreach (var (target, s) in added)
                {
                    target.Strokes.Add(s);
                }
            });
    }

    /// <summary>自由画布笔划进入页面区域时拆分：页面内段转入对应页面，其余留在自由画布。</summary>
    private void SplitFreeStroke(Stroke stroke) => SplitFreeStroke(_freeInk!, stroke);

    private void SplitFreeStroke(InkCanvas source, Stroke stroke)
    {
        var freeOffset = FreeInkLayerSize / 2;
        var points = stroke.StylusPoints;
        var assignments = new List<int?>(points.Count);
        foreach (var p in points)
        {
            int? owner = null;
            foreach (var (pageIndex, element) in _pageElements)
            {
                var left = Canvas.GetLeft(element);
                var top = Canvas.GetTop(element);
                var localX = p.X - freeOffset - left - element.BorderThickness.Left;
                var localY = p.Y - freeOffset - top - element.BorderThickness.Top;
                if (localX >= -0.5 && localX <= element.ActualWidth + 0.5
                    && localY >= -0.5 && localY <= element.ActualHeight + 0.5)
                {
                    owner = pageIndex;
                    break;
                }
            }
            assignments.Add(owner);
        }

        var segments = new List<(int? Owner, List<StylusPoint> Segment)>();
        for (var i = 0; i < points.Count; i++)
        {
            var owner = assignments[i];
            if (segments.Count == 0 || segments[^1].Owner != owner)
            {
                segments.Add((owner, new List<StylusPoint>()));
            }
            segments[^1].Segment.Add(points[i]);
        }

        var added = new List<(InkCanvas Ink, Stroke Stroke)>();
        foreach (var (owner, segment) in segments)
        {
            if (segment.Count == 0)
            {
                continue;
            }
            if (owner is int pageIndex)
            {
                var element = _pageElements[pageIndex];
                var converted = new StylusPointCollection();
                foreach (var p in segment)
                {
                    converted.Add(new StylusPoint(
                        p.X - freeOffset - Canvas.GetLeft(element) - element.BorderThickness.Left,
                        p.Y - freeOffset - Canvas.GetTop(element) - element.BorderThickness.Top,
                        p.PressureFactor));
                }
                added.Add((_inkCanvases[pageIndex], new Stroke(converted, stroke.DrawingAttributes)));
            }
            else
            {
                added.Add((_freeInk!, new Stroke(new StylusPointCollection(segment), stroke.DrawingAttributes)));
            }
        }

        source.Strokes.Remove(stroke);
        foreach (var (target, s) in added)
        {
            target.Strokes.Add(s);
        }
        RecordUndo(
            undo: () =>
            {
                source.Strokes.Add(stroke);
                foreach (var (target, s) in added)
                {
                    target.Strokes.Remove(s);
                }
            },
            redo: () =>
            {
                source.Strokes.Remove(stroke);
                foreach (var (target, s) in added)
                {
                    target.Strokes.Add(s);
                }
            });
    }

    /// <summary>手动橡皮擦：以根坐标位置为中心，同时命中自由画布与所有页面图层（跨边界连续擦除）。</summary>
    private void EraseAt(Point rootPosition)
    {
        var diameter = EraserWidth;
        if (_freeInk is not null)
        {
            EraseStrokesAt(_freeInk, RootGrid.TranslatePoint(rootPosition, _freeInk), diameter);
        }
        foreach (var (pageIndex, element) in _pageElements)
        {
            if (!_inkCanvases.TryGetValue(pageIndex, out var ink))
            {
                continue;
            }
            EraseStrokesAt(ink, RootGrid.TranslatePoint(rootPosition, ink), diameter);
        }
    }

    /// <summary>擦除指定图层中与圆形区域相交的笔画，并逐笔记录撤销。</summary>
    private void EraseStrokesAt(InkCanvas ink, Point local, double diameter)
    {
        var hits = ink.Strokes.HitTest(local, diameter);
        if (hits.Count == 0)
        {
            return;
        }
        foreach (var stroke in hits.ToList())
        {
            ink.Strokes.Remove(stroke);
            RecordUndo(
                undo: () => ink.Strokes.Add(stroke),
                redo: () => ink.Strokes.Remove(stroke));
        }
    }

    private void OnStrokeErasing(object? sender, InkCanvasStrokeErasingEventArgs e)
    {
        var stroke = e.Stroke;
        var strokes = ((InkCanvas)sender!).Strokes;
        RecordUndo(
            undo: () => strokes.Add(stroke),
            redo: () => strokes.Remove(stroke));
    }

    // ---------- 触摸输入（单指平移 / 双指缩放） ----------

    private void OnInkPreviewTouchDown(object? sender, TouchEventArgs e)
    {
        // 触摸只用于平移/缩放，不写墨迹
        _touchActive = true;
        SetEditingModeNone();
    }

    private void OnManipulationStarting(object sender, ManipulationStartingEventArgs e)
    {
        _touchActive = true;
        SetEditingModeNone();
        e.Mode = ManipulationModes.Translate | ManipulationModes.Scale;
        e.Handled = true;
    }

    private void OnManipulationDelta(object sender, ManipulationDeltaEventArgs e)
    {
        PanBy(e.DeltaManipulation.Translation.X, e.DeltaManipulation.Translation.Y);
        var scale = e.DeltaManipulation.Scale.X;
        if (scale > 0 && Math.Abs(scale - 1) > 0.0001)
        {
            ApplyZoom(Zoom * scale, e.ManipulationOrigin);
        }
        e.Handled = true;
    }

    private void OnManipulationInertiaStarting(object sender, ManipulationInertiaStartingEventArgs e)
    {
        e.TranslationBehavior = new InertiaTranslationBehavior
        {
            InitialVelocity = e.InitialVelocities.LinearVelocity,
            DesiredDeceleration = 3000,
        };
        e.ExpansionBehavior = new InertiaExpansionBehavior
        {
            InitialVelocity = e.InitialVelocities.ExpansionVelocity,
            DesiredDeceleration = 2000,
        };
        e.Handled = true;
    }

    private void OnManipulationCompleted(object sender, ManipulationCompletedEventArgs e)
    {
        _touchActive = false;
        UpdateEditingState();
        e.Handled = true;
    }

    /// <summary>是否有套索选中的墨迹笔划。</summary>
    public bool HasSelectedInk =>
        _inkCanvases.Values.Any(ink => ink.GetSelectedStrokes().Count > 0)
        || (_freeInk is not null && _freeInk.GetSelectedStrokes().Count > 0);

    /// <summary>删除所有被套索选中的墨迹笔划（记录撤销）。</summary>
    public void DeleteSelectedInk()
    {
        foreach (var ink in _inkCanvases.Values)
        {
            DeleteSelectedStrokes(ink);
        }
        if (_freeInk is not null)
        {
            DeleteSelectedStrokes(_freeInk);
        }
    }

    private void DeleteSelectedStrokes(InkCanvas ink)
    {
        var selected = ink.GetSelectedStrokes().ToList();
        var strokes = ink.Strokes;
        foreach (var stroke in selected)
        {
            RecordUndo(
                undo: () => strokes.Remove(stroke),
                redo: () => strokes.Add(stroke));
            strokes.Remove(stroke);
        }
    }

    // ---------- 导出渲染 ----------

    /// <summary>把某页 PDF 位图与页面墨迹、自由墨迹、元素合并渲染（导出 PDF 用）。</summary>
    public BitmapSource RenderPageWithAnnotations(int pageIndex, BitmapSource basePage, double dpi)
    {
        var width = basePage.PixelWidth;
        var height = basePage.PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(basePage, new Rect(0, 0, width, height));

            var scale = dpi / 96.0; // 墨迹/元素是世界 DIP 坐标，位图是 dpi 像素
            dc.PushTransform(new ScaleTransform(scale, scale));

            var pageWidth = 0.0;
            var pageHeight = 0.0;
            if (Pages is not null && pageIndex >= 0 && pageIndex < Pages.Count)
            {
                pageWidth = Pages[pageIndex].BaseWidth;
                pageHeight = Pages[pageIndex].BaseHeight;
            }
            var pageTop = GetPageWorldY(pageIndex);
            dc.PushClip(new RectangleGeometry(new Rect(0, pageTop, pageWidth, pageHeight)));

            if (_inkCanvases.TryGetValue(pageIndex, out var ink))
            {
                DrawStrokes(dc, ink.Strokes);
            }
            if (_freeInk is not null)
            {
                DrawStrokes(dc, _freeInk.Strokes);
            }
            foreach (var element in _elements)
            {
                DrawElement(dc, element);
            }

            dc.Pop();
            dc.Pop();
        }

        var renderTarget = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        renderTarget.Render(visual);
        renderTarget.Freeze();
        return renderTarget;
    }

    /// <summary>页面 i 顶部在画布世界坐标中的 Y（连续垂直排列，页距 0）。</summary>
    private double GetPageWorldY(int pageIndex)
    {
        if (Pages is null)
        {
            return 0;
        }
        var y = 0.0;
        for (var i = 0; i < pageIndex && i < Pages.Count; i++)
        {
            y += Pages[i].BaseHeight;
        }
        return y;
    }

    private static void DrawStrokes(DrawingContext dc, StrokeCollection strokes)
    {
        foreach (var stroke in strokes)
        {
            var geometry = stroke.GetGeometry();
            var brush = new SolidColorBrush(stroke.DrawingAttributes.Color);
            brush.Freeze();
            dc.DrawGeometry(brush, null, geometry);
        }
    }

    private static void DrawElement(DrawingContext dc, CanvasElement element)
    {
        if (element.IsText)
        {
            var width = Math.Max(1, element.WorldWidth - 8);
            var block = new TextBlock
            {
                Text = element.Text,
                FontSize = element.FontSize,
                FontWeight = element.Weight,
                FontStyle = element.Style,
                TextDecorations = element.Decorations,
                Foreground = new SolidColorBrush(element.Color),
                TextWrapping = TextWrapping.Wrap,
            };
            block.Measure(new Size(width, double.PositiveInfinity));
            block.Arrange(new Rect(0, 0, width, Math.Max(1, block.DesiredSize.Height)));
            var blockTarget = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(width)),
                Math.Max(1, (int)Math.Ceiling(block.DesiredSize.Height)),
                96, 96, PixelFormats.Pbgra32);
            blockTarget.Render(block);
            dc.DrawImage(blockTarget, new Rect(element.WorldX + 4, element.WorldY + 4, width, block.DesiredSize.Height));
        }
        else if (element.ImageSource is not null)
        {
            dc.DrawImage(
                element.ImageSource,
                new Rect(element.WorldX, element.WorldY, element.WorldWidth, element.WorldHeight));
        }
    }

    // ---------- 墨迹工具状态 ----------

    private void SetEditingModeNone()
    {
        foreach (var ink in _inkCanvases.Values)
        {
            ink.EditingMode = InkCanvasEditingMode.None;
        }
        if (_freeInk is not null)
        {
            _freeInk.EditingMode = InkCanvasEditingMode.None;
        }
        if (_liveInk is not null)
        {
            _liveInk.EditingMode = InkCanvasEditingMode.None;
            _liveInk.IsHitTestVisible = false;
        }
    }

    private void UpdateEditingState()
    {
        foreach (var ink in _inkCanvases.Values)
        {
            ApplyToolToInk(ink);
        }
        if (_freeInk is not null)
        {
            ApplyToolToInk(_freeInk);
        }
        if (_liveInk is not null)
        {
            ApplyToolToInk(_liveInk);
        }
    }

    private void ApplyToolToInk(InkCanvas ink)
    {
        if (ReferenceEquals(ink, _liveInk))
        {
            var drawing = !_touchActive && ActiveTool is InkTool.Pen or InkTool.Highlighter;
            ink.IsHitTestVisible = drawing;
            ink.EditingMode = drawing ? InkCanvasEditingMode.Ink : InkCanvasEditingMode.None;
            ink.DefaultDrawingAttributes = CreateDrawingAttributes();
            return;
        }
        var editingMode = _touchActive || ActiveTool is InkTool.Select or InkTool.Text or InkTool.Eraser
            ? InkCanvasEditingMode.None // 橡皮擦由 OnMouseDown/Move 手动跨层处理，避免只能擦当前图层
            : ActiveTool == InkTool.Lasso
                ? InkCanvasEditingMode.Select
                : InkCanvasEditingMode.Ink;

        ink.EditingMode = editingMode;
        ink.DefaultDrawingAttributes = CreateDrawingAttributes();
        ink.EraserShape = new EllipseStylusShape(EraserWidth, EraserWidth);
    }

    private DrawingAttributes CreateDrawingAttributes()
    {
        if (ActiveTool == InkTool.Highlighter)
        {
            var color = HighlightColor;
            color.A = 0x55; // 半透明
            return new DrawingAttributes
            {
                Color = color,
                Width = HighlightWidth,
                Height = HighlightWidth,
                FitToCurve = true,
                IgnorePressure = true,
                StylusTip = StylusTip.Rectangle,
                IsHighlighter = true,
            };
        }

        return new DrawingAttributes
        {
            Color = PenColor,
            Width = PenWidth,
            Height = PenWidth,
            FitToCurve = true,
            IgnorePressure = false, // 钢笔开启压感
            StylusTip = StylusTip.Ellipse,
            IsHighlighter = false,
        };
    }
}
