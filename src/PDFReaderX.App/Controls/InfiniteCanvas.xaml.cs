using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
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
    private const int MaxCachedPages = 6;
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
    private InkCanvas? _freeInk;
    private readonly Dictionary<int, Rectangle> _imagesByPage = new();
    private readonly Dictionary<int, BitmapSource> _bitmapCache = new();
    private readonly List<int> _cacheOrder = new();
    private readonly HashSet<int> _pendingRenders = new();

    private Point _pan;
    private bool _isMousePanning;
    private Point _lastMousePanPoint;
    private bool _touchActive;
    private int _documentEpoch;
    private bool _layoutDirty;
    private DispatcherTimer? _rerenderTimer;

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

    public int CurrentPageIndex
    {
        get => (int)GetValue(CurrentPageIndexProperty);
        set => SetValue(CurrentPageIndexProperty, value);
    }

    /// <summary>垂直滚动偏移：内容顶部相对视口顶部的距离（即 -_pan.Y）。</summary>
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
        PanTransform.X = 0;
        PanTransform.Y = 0;
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
        PanTransform.X = 0;
        PanTransform.Y = _pan.Y;
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
        canvas.PanTransform.X = canvas._pan.X;
        canvas.PanTransform.Y = canvas._pan.Y;
        canvas.LayoutPages();
        canvas.ScheduleRerender();
    }

    private static void OnToolChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (InfiniteCanvas)d;
        canvas.UpdateEditingState();
        canvas.Cursor = canvas.ActiveTool == InkTool.Select ? Cursors.Hand : Cursors.Arrow;
    }

    private void RebuildPages()
    {
        ViewportCanvas.Children.Clear();
        _freeInk = null;
        _pageElements.Clear();
        _inkCanvases.Clear();
        _imagesByPage.Clear();
        _bitmapCache.Clear();
        _cacheOrder.Clear();
        _pendingRenders.Clear();
        _documentEpoch++;
        _pan = default;
        PanTransform.X = 0;
        PanTransform.Y = 0;
        ScrollOffset = 0;

        CreateFreeInkLayer();

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
        const double size = 4_000_000;
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
        ViewportCanvas.Children.Add(ink);
        _freeInk = ink;
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
        _imagesByPage[page.PageIndex] = image;
        return border;
    }

    private void LayoutPages()
    {
        if (Pages is null || Pages.Count == 0)
        {
            UpdateScrollState();
            return;
        }


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
        for (var i = 0; i < Pages.Count; i++)
        {
            var page = Pages[i];
            if (!_pageElements.TryGetValue(page.PageIndex, out var element))
            {
                continue;
            }

            var scaledWidth = page.BaseWidth * Zoom;
            var left = Math.Max(0, (ActualWidth - scaledWidth) / 2);
            Canvas.SetLeft(element, left);
            Canvas.SetTop(element, offsets[i]);
            element.RenderTransform = new ScaleTransform(Zoom, Zoom);
        }
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


        var viewport = new Rect(-_pan.X, -_pan.Y, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
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

    private void RenderPageAsync(PageViewModel page)
    {
        if (Document is null || _pendingRenders.Contains(page.PageIndex))
        {
            return;
        }

        var index = page.PageIndex;
        var dpi = Math.Min(96.0 * Zoom, MaxRenderDpi);
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
        PanTransform.Y = _pan.Y;
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

        var (x, y) = CanvasTransform.ZoomAt(center.X, center.Y, _pan.X, _pan.Y, Zoom, newZoom);
        _pan = new Point(x, y);
        Zoom = newZoom; // 触发 OnZoomChanged → PanTransform + LayoutPages + 重渲染
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
        PanTransform.X = _pan.X;
        PanTransform.Y = _pan.Y;
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
    }

    private void ApplyToolToInk(InkCanvas ink)
    {
        var editingMode = _touchActive || ActiveTool == InkTool.Select
            ? InkCanvasEditingMode.None
            : ActiveTool == InkTool.Eraser
                ? InkCanvasEditingMode.EraseByStroke
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
