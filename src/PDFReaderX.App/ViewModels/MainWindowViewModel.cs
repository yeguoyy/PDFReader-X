using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PDFReaderX.App;
using PDFReaderX.App.Controls;
using PDFReaderX.App.Helpers;
using PDFReaderX.App.Models;
using PDFReaderX.App.Services;
using PDFReaderX.Core.Services;
using PDFReaderX.LLM;

namespace PDFReaderX.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private const double DisplayDpi = 96; // 页面基准尺寸按 96 DIP（zoom=1 时 1:1）
    private int _thumbnailEpoch; // 缩略图生成批次号，换文档时失效
    private int _lastCurrentThumb = -1;

    [ObservableProperty]
    private PdfRenderService? _document;

    public ObservableCollection<PageViewModel> Pages { get; } = new();

    /// <summary>侧边栏页面缩略图（后台线程生成）。</summary>
    public ObservableCollection<ThumbnailViewModel> Thumbnails { get; } = new();

    /// <summary>侧边栏书签目录（来自 PDF Outline）。</summary>
    public ObservableCollection<BookmarkViewModel> Bookmarks { get; } = new();

    [ObservableProperty]
    private bool _hasDocument;

    [ObservableProperty]
    private string _fileName = "未打开文件";

    [ObservableProperty]
    private string _statusText = "就绪";

    /// <summary>是否正在生成 AI 书签（书签面板进度区显示）。</summary>
    [ObservableProperty]
    private bool _isGeneratingBookmarks;

    /// <summary>AI 书签生成进度文案。</summary>
    [ObservableProperty]
    private string _bookmarkGenerationStatus = "";

    [ObservableProperty]
    private bool _isBusy;

    // ---------- 画布状态 ----------

    [ObservableProperty]
    private double _zoom = 1.0;

    /// <summary>当前所在页（0 基），由画布视口中心推算，供侧边栏高亮与跟随。</summary>
    [ObservableProperty]
    private int _currentPageIndex;

    [ObservableProperty]
    private InkTool _activeTool = InkTool.Pen;

    [ObservableProperty]
    private Color _penColor = Colors.Black;

    [ObservableProperty]
    private double _penWidth = 3.0;

    [ObservableProperty]
    private Color _highlightColor = Colors.Yellow;

    [ObservableProperty]
    private double _highlightWidth = 24.0;

    [ObservableProperty]
    private double _eraserWidth = 16.0;

    public IReadOnlyList<Color> PenColors { get; } = new[]
    {
        Colors.Black, Colors.Red, Colors.Orange, Colors.Yellow,
        Colors.Green, Colors.Lime, Colors.Cyan, Colors.Blue,
        Colors.Purple, Colors.Magenta, Colors.Pink, Colors.Brown,
        Colors.Gray, Colors.DarkSlateGray, Colors.White,
    };

    public IReadOnlyList<Color> HighlightColors { get; } = new[]
    {
        Colors.Yellow, Colors.Orange, Colors.Lime, Colors.Green,
        Colors.Cyan, Colors.Blue, Colors.Magenta, Colors.Pink,
    };

    public IReadOnlyList<TextBorderStyleOption> TextBorderStyles { get; } = new[]
    {
        new TextBorderStyleOption("black-dashed", "黑色细虚线"),
        new TextBorderStyleOption("blue-solid", "蓝色实线"),
        new TextBorderStyleOption("black-solid", "黑色实线"),
        new TextBorderStyleOption("gray-thin", "灰色细线"),
        new TextBorderStyleOption("none", "无边框"),
    };

    /// <summary>文本框边框样式（从本地设置加载，修改即保存）。</summary>
    [ObservableProperty]
    private string _textBorderStyle = AppSettingsStore.Load().TextBoxBorderStyle;

    /// <summary>缩略图当前页指示框颜色（跟随边框设置，实线显示）。</summary>
    public Brush CurrentPageBorderBrush => TextBorderStyle switch
    {
        "blue-solid" => new SolidColorBrush(Color.FromRgb(0x2D, 0x6C, 0xDF)),
        "black-solid" => Brushes.Black,
        "gray-thin" => new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
        "none" => Brushes.Transparent,
        _ => Brushes.Black,
    };

    partial void OnTextBorderStyleChanged(string value)
    {
        var settings = AppSettingsStore.Load();
        settings.TextBoxBorderStyle = value;
        AppSettingsStore.Save(settings);
        OnPropertyChanged(nameof(CurrentPageBorderBrush));
    }

    public IReadOnlyList<double> TextFontSizes { get; } = new[] { 10.0, 12.0, 14.0, 16.0, 18.0, 20.0, 24.0, 28.0, 32.0, 36.0, 48.0 };

    /// <summary>文本框字号（新建文本框与编辑中生效）。</summary>
    [ObservableProperty]
    private double _textFontSize = 14.0;

    /// <summary>性能模式（低配电脑）：渲染上限与缓存页数降低，滚动缩放更流畅。</summary>
    [ObservableProperty]
    private bool _performanceMode = AppSettingsStore.Load().PerformanceMode;

    partial void OnPerformanceModeChanged(bool value)
    {
        var settings = AppSettingsStore.Load();
        settings.PerformanceMode = value;
        AppSettingsStore.Save(settings);
    }

    /// <summary>工具栏快捷笔：点击直接切换工具+颜色+粗细（可增删并持久化）。</summary>
    public ObservableCollection<QuickPenStyle> QuickPens { get; } = LoadQuickPens();

    private static ObservableCollection<QuickPenStyle> LoadQuickPens()
    {
        var saved = AppSettingsStore.Load().QuickPens;
        if (saved.Count == 0)
        {
            return new ObservableCollection<QuickPenStyle>(DefaultQuickPens());
        }
        var list = new ObservableCollection<QuickPenStyle>();
        foreach (var data in saved)
        {
            if (TryParseColorHex(data.Color, out var color)
                && Enum.TryParse<InkTool>(data.Tool, out var tool))
            {
                list.Add(new QuickPenStyle
                {
                    Key = data.Key,
                    Tool = tool,
                    Kind = string.IsNullOrEmpty(data.Kind) ? "pen" : data.Kind,
                    Color = color,
                    Width = data.Width,
                    Name = data.Name,
                });
            }
        }
        return list.Count > 0 ? list : new ObservableCollection<QuickPenStyle>(DefaultQuickPens());
    }

    private static IEnumerable<QuickPenStyle> DefaultQuickPens() => new[]
    {
        new QuickPenStyle { Key = "pen-black", Tool = InkTool.Pen, Kind = "pen", Color = Colors.Black, Width = 2.5, Name = "黑色钢笔" },
        new QuickPenStyle { Key = "pen-red", Tool = InkTool.Pen, Kind = "pen", Color = Colors.Red, Width = 2.5, Name = "红色钢笔" },
        new QuickPenStyle { Key = "pen-blue", Tool = InkTool.Pen, Kind = "pen", Color = Colors.Blue, Width = 2.5, Name = "蓝色钢笔" },
        new QuickPenStyle { Key = "pen-green", Tool = InkTool.Pen, Kind = "pen", Color = Colors.Green, Width = 2.5, Name = "绿色钢笔" },
        new QuickPenStyle { Key = "hl-yellow", Tool = InkTool.Highlighter, Kind = "highlighter", Color = Colors.Yellow, Width = 24, Name = "荧光黄" },
        new QuickPenStyle { Key = "hl-cyan", Tool = InkTool.Highlighter, Kind = "highlighter", Color = Colors.Cyan, Width = 24, Name = "荧光蓝" },
        new QuickPenStyle { Key = "hl-magenta", Tool = InkTool.Highlighter, Kind = "highlighter", Color = Colors.Magenta, Width = 24, Name = "荧光紫" },
        new QuickPenStyle { Key = "hl-lime", Tool = InkTool.Highlighter, Kind = "highlighter", Color = Colors.Lime, Width = 24, Name = "荧光绿" },
    };

    /// <summary>新增快捷笔并保存。</summary>
    public void AddQuickPen(QuickPenStyle style)
    {
        QuickPens.Add(style);
        SaveQuickPens();
    }

    /// <summary>删除快捷笔并保存。</summary>
    public void RemoveQuickPen(QuickPenStyle style)
    {
        QuickPens.Remove(style);
        SaveQuickPens();
    }

    public void SaveQuickPens()
    {
        var settings = AppSettingsStore.Load();
        settings.QuickPens = QuickPens.Select(style => new QuickPenData
        {
            Key = style.Key,
            Tool = style.Tool.ToString(),
            Kind = style.Kind,
            Color = $"#{style.Color.R:X2}{style.Color.G:X2}{style.Color.B:X2}",
            Width = style.Width,
            Name = style.Name,
        }).ToList();
        AppSettingsStore.Save(settings);
    }
    public IReadOnlyList<double> PenWidths { get; } = new[] { 1.0, 1.5, 2.5, 3.0, 4.5, 6.0 };
    public IReadOnlyList<double> HighlightWidths { get; } = new[] { 12.0, 16.0, 20.0, 24.0, 32.0, 40.0 };
    public IReadOnlyList<double> EraserWidths { get; } = new[] { 6.0, 10.0, 16.0, 24.0, 36.0, 48.0 };

    /// <summary>最近使用的颜色（笔/荧光笔共用，最多 8 个，本地持久化）。</summary>
    public ObservableCollection<Color> RecentColors { get; } = LoadRecentColors();

    private static ObservableCollection<Color> LoadRecentColors()
    {
        var list = new ObservableCollection<Color>();
        foreach (var hex in AppSettingsStore.Load().RecentColors)
        {
            if (TryParseColorHex(hex, out var color))
            {
                list.Add(color);
            }
        }
        return list;
    }

    /// <summary>记录最近使用的颜色（去重置顶，最多保留 8 个）。</summary>
    public void AddRecentColor(Color color)
    {
        var existing = RecentColors.FirstOrDefault(c => c == color);
        if (existing != default)
        {
            RecentColors.Remove(existing);
        }
        RecentColors.Insert(0, color);
        while (RecentColors.Count > 8)
        {
            RecentColors.RemoveAt(RecentColors.Count - 1);
        }
        var settings = AppSettingsStore.Load();
        settings.RecentColors = RecentColors
            .Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}")
            .ToList();
        AppSettingsStore.Save(settings);
    }

    private static bool TryParseColorHex(string hex, out Color color)
    {
        color = default;
        var text = hex.Trim().TrimStart('#');
        if (text.Length != 6
            || !byte.TryParse(text.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(text.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(text.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return false;
        }
        color = Color.FromRgb(r, g, b);
        return true;
    }

    public RelayCommand OpenCommand { get; }

    public RelayCommand CloseCommand { get; }

    public RelayCommand OpenLlmSettingsCommand { get; }

    public RelayCommand OpenSettingsCommand { get; }

    public AsyncRelayCommand GenerateBookmarksCommand { get; }

    /// <summary>侧边栏的 AI 生成书签按钮（有文档且当前没有书签时可用/显示）。</summary>
    public AsyncRelayCommand AutoGenerateBookmarksCommand { get; }

    /// <summary>书签整体下移 1 页（所有书签目标页 +1）。</summary>
    public RelayCommand ShiftBookmarksDownCommand { get; }

    /// <summary>书签整体上移 1 页（所有书签目标页 -1）。</summary>
    public RelayCommand ShiftBookmarksUpCommand { get; }

    /// <summary>当前是否有书签（PDF 自带或 AI 生成）。</summary>
    public bool HasBookmarks => Bookmarks.Count > 0;

    /// <summary>侧边栏是否显示“AI 生成书签”按钮：有文档且没有书签时显示，重生成入口在“设置”里。</summary>
    public bool ShowAutoBookmarkButton => HasDocument && !HasBookmarks;

    partial void OnHasDocumentChanged(bool value)
    {
        GenerateBookmarksCommand.NotifyCanExecuteChanged();
        AutoGenerateBookmarksCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowAutoBookmarkButton));
    }

    partial void OnIsBusyChanged(bool value)
    {
        GenerateBookmarksCommand.NotifyCanExecuteChanged();
        AutoGenerateBookmarksCommand.NotifyCanExecuteChanged();
    }

    partial void OnCurrentPageIndexChanged(int value)
    {
        if (_lastCurrentThumb >= 0 && _lastCurrentThumb < Thumbnails.Count)
        {
            Thumbnails[_lastCurrentThumb].IsCurrent = false;
        }
        if (value >= 0 && value < Thumbnails.Count)
        {
            Thumbnails[value].IsCurrent = true;
        }
        _lastCurrentThumb = value;
    }

    public MainWindowViewModel()
    {
        OpenCommand = new RelayCommand(Open, () => !IsBusy);
        CloseCommand = new RelayCommand(Close, () => HasDocument);
        OpenLlmSettingsCommand = new RelayCommand(OpenLlmSettings);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        GenerateBookmarksCommand = new AsyncRelayCommand(GenerateBookmarksAsync, () => HasDocument && !IsBusy);
        AutoGenerateBookmarksCommand = new AsyncRelayCommand(
            GenerateBookmarksAsync, () => HasDocument && !IsBusy && !HasBookmarks);
        ShiftBookmarksDownCommand = new RelayCommand(() => ShiftBookmarks(1), () => HasBookmarks);
        ShiftBookmarksUpCommand = new RelayCommand(() => ShiftBookmarks(-1), () => HasBookmarks);
        Bookmarks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasBookmarks));
            OnPropertyChanged(nameof(ShowAutoBookmarkButton));
            AutoGenerateBookmarksCommand.NotifyCanExecuteChanged();
            ShiftBookmarksDownCommand.NotifyCanExecuteChanged();
            ShiftBookmarksUpCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>把所有书签的目标页整体平移 delta 页（±1），钳制到文档页范围。</summary>
    private void ShiftBookmarks(int delta)
    {
        if (Document is null || Bookmarks.Count == 0)
        {
            return;
        }
        var pageCount = Document.PageCount;
        var shifted = Bookmarks.Select(b => ShiftBookmarkNode(b, delta, pageCount)).ToList();
        Bookmarks.Clear();
        foreach (var node in shifted)
        {
            Bookmarks.Add(node);
        }
        StatusText = delta > 0
            ? "书签已整体下移 1 页（保存后生效）"
            : "书签已整体上移 1 页（保存后生效）";
    }

    private static BookmarkViewModel ShiftBookmarkNode(BookmarkViewModel node, int delta, int pageCount)
    {
        var shifted = new BookmarkViewModel(
            node.Title,
            node.PageIndex < 0 ? -1 : Math.Clamp(node.PageIndex + delta, 0, Math.Max(0, pageCount - 1)));
        foreach (var child in node.Children)
        {
            shifted.Children.Add(ShiftBookmarkNode(child, delta, pageCount));
        }
        return shifted;
    }

    /// <summary>提示用户配置 LLM 并打开设置页；返回 false 表示用户取消。</summary>
    private bool PromptForLlmConfiguration(string message)
    {
        var result = MessageBox.Show(
            message,
            "PDFReader X",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (result != MessageBoxResult.Yes)
        {
            return false;
        }
        OpenSettings();
        return true;
    }

    private void OpenLlmSettings()
    {
        var window = new LlmSettingsWindow { Owner = Application.Current.MainWindow };
        window.ShowDialog();
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow(this) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
    }

    private async Task GenerateBookmarksAsync()
    {
        if (Document is null)
        {
            return;
        }

        var settings = LlmSettingsStore.Load();
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            if (!PromptForLlmConfiguration(
                    "使用 AI 书签需要先配置大模型 API Key。\n\n" +
                    "默认使用阿里云百炼（DashScope）：\n" +
                    "1. 打开 bailian.console.aliyun.com 申请 API Key\n" +
                    "2. 在「设置 → LLM 设置」中粘贴 Key 并确认模型名称\n\n" +
                    "现在打开设置页面配置吗？"))
            {
                StatusText = "未配置 API Key，已取消生成";
                return;
            }
            settings = LlmSettingsStore.Load();
            if (string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                StatusText = "未配置 API Key，已取消生成";
                return;
            }
        }

        IsBusy = true;
        IsGeneratingBookmarks = true;
        try
        {
            var document = Document;
            var pageCount = document.PageCount; // 提前缓存，等待期间文档可能被关闭

            // 快速探测前几页是否有文字层，没有则走视觉模式
            var probe = await Task.Run(() => ExtractPageTexts(document, 0, Math.Min(5, pageCount)));
            if (probe.All(string.IsNullOrWhiteSpace))
            {
                await GenerateBookmarksByVisionAsync(document, pageCount);
                return;
            }

            var service = new OpenAiCompatibleService(settings);

            // 智能目录模式：先读目录 + 章节页码，确认偏移后生成书签
            if (await TryGenerateBookmarksSmartAsync(service, document, pageCount))
            {
                return;
            }

            StatusText = "未找到目录，已停止（避免全本读取）";
            BookmarkGenerationStatus = "未找到目录或无法确认页码偏移";
            MessageBox.Show(
                "整本 PDF 中未找到目录页，或无法确认章节页码偏移，未生成书签。",
                "PDFReader X",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText = "AI 生成书签失败";
            BookmarkGenerationStatus = "生成失败：" + ex.Message;
            MessageBox.Show(
                $"AI 生成书签失败：\n{ex.Message}",
                "PDFReader X",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            IsGeneratingBookmarks = false;
        }
    }

    /// <summary>
    /// 智能目录模式：每批 10 页轻量探测是否含目录，找到目录后再读完目录，
    /// 并定位前几个章节标题确认页码偏移，推算全本书签。
    /// 找不到目录或偏移无法确认时返回 false，不再回退全量读取。
    /// </summary>
    private async Task<bool> TryGenerateBookmarksSmartAsync(
        OpenAiCompatibleService service,
        PdfRenderService document,
        int pageCount)
    {
        const int tocBatchPages = 10;
        try
        {
            // 阶段一：逐批往后找目录，每批只发当前批，token 消耗恒定
            var tocStart = -1;
            for (var start = 0; start < pageCount; start += tocBatchPages)
            {
                var count = Math.Min(tocBatchPages, pageCount - start);
                SetGenerationStatus($"正在找目录…（第 {FormatPageRange(start + 1, count)}）");
                var batch = await Task.Run(() => ExtractPageTexts(document, start, count));
                var isLastBatch = start + count >= pageCount;
                var probe = await service.ProbeTocAsync(batch, start + 1, isLastBatch);
                if (probe.HasToc)
                {
                    tocStart = start;
                    break;
                }
                if (isLastBatch)
                {
                    return false; // 整本读完都没有目录
                }
            }
            if (tocStart < 0)
            {
                return false;
            }

            // 阶段二：从目录所在批开始累积读完整目录
            var frontTexts = new List<string>();
            TocResult? toc = null;
            for (var start = tocStart; start < pageCount; start += tocBatchPages)
            {
                var count = Math.Min(tocBatchPages, pageCount - start);
                SetGenerationStatus($"正在读取目录…（第 {FormatPageRange(start + 1, count)}，条目较多时需等待片刻）");
                var batch = await Task.Run(() => ExtractPageTexts(document, start, count));
                frontTexts.AddRange(batch);

                var isLastBatch = start + count >= pageCount;
                var result = await service.GenerateTocAsync(frontTexts, isLastBatch, firstPageNumber: tocStart + 1);
                if (!result.HasToc)
                {
                    return false;
                }
                if (!result.TocComplete)
                {
                    if (isLastBatch)
                    {
                        toc = new TocResult { HasToc = true, TocComplete = true, Bookmarks = result.Bookmarks };
                        break;
                    }
                    continue; // 目录还没读完，继续往后读
                }
                toc = result;
                break;
            }
            if (toc is null || toc.Bookmarks.Count == 0)
            {
                return false;
            }

            SetGenerationStatus("目录读取成功，正在核对章节页码与 PDF 页码的偏移…");
            var offset = await ConfirmOffsetBatchedAsync(document, toc.Bookmarks, tocStart + frontTexts.Count, pageCount);
            if (offset is null)
            {
                return false;
            }

            var bookmarks = BookmarkLocator.ShiftPages(toc.Bookmarks, offset.Value, pageCount);
            ApplyBookmarkPreview(bookmarks, pageCount);
            return true;
        }
        catch (LlmOutputTruncatedException)
        {
            return false;
        }
    }

    /// <summary>页码范围文案：单页显示"x 页"，多页显示"x~y 页"。</summary>
    private static string FormatPageRange(int startPage, int count) =>
        count <= 1 ? $"{startPage} 页" : $"{startPage}~{startPage + count - 1} 页";

    /// <summary>
    /// 分批提取正文文本核对目录偏移：从目录之后按 40 页一批向后读，
    /// 每批统计前几个章节标题的绝对页码偏移，累计票数 ≥2 且唯一时立即确认，
    /// 避免为确认偏移而提取整本 PDF 的文本。
    /// </summary>
    private static async Task<int?> ConfirmOffsetBatchedAsync(
        PdfRenderService document,
        IReadOnlyList<LlmBookmark> bookmarks,
        int searchStartPage,
        int pageCount)
    {
        const int batchPages = 40;
        const int maxCheckedTitles = 10;
        var counts = new Dictionary<int, int>();
        var matchedTitles = new HashSet<string>();
        var checkedTitles = 0;

        for (var start = searchStartPage; start < pageCount; start += batchPages)
        {
            var count = Math.Min(batchPages, pageCount - start);
            var texts = await Task.Run(() => ExtractPageTexts(document, start, count));

            foreach (var bookmark in bookmarks)
            {
                if (checkedTitles >= maxCheckedTitles)
                {
                    break;
                }
                var normalizedTitle = NormalizeTitle(bookmark.Title);
                if (normalizedTitle.Length == 0 || !matchedTitles.Add(normalizedTitle))
                {
                    continue;
                }
                checkedTitles++;
                for (var localPage = 0; localPage < texts.Count; localPage++)
                {
                    if (NormalizeTitle(texts[localPage]).Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        var offset = start + localPage - bookmark.PageIndex;
                        counts[offset] = counts.GetValueOrDefault(offset) + 1;
                        break;
                    }
                }
            }

            var best = counts.OrderByDescending(pair => pair.Value).FirstOrDefault();
            if (best.Value >= 2 && counts.Count(pair => pair.Value == best.Value) == 1)
            {
                return best.Key;
            }
        }
        return null;
    }

    private static string NormalizeTitle(string text)
    {
        return string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    /// <summary>扫描版 PDF：渲染页面图片，交给视觉模型识别生成书签。</summary>
    private async Task GenerateBookmarksByVisionAsync(PdfRenderService document, int pageCount)
    {
        var savedSettings = LlmSettingsStore.Load();
        if (!savedSettings.SkipVisionConfirm)
        {
            var confirm = new VisionConfirmWindow { Owner = Application.Current.MainWindow };
            if (confirm.ShowDialog() != true)
            {
                StatusText = "已取消生成书签";
                return;
            }
            if (confirm.DontAskAgain)
            {
                savedSettings.SkipVisionConfirm = true;
                LlmSettingsStore.Save(savedSettings);
            }
        }

        var settings = LlmSettingsStore.Load();
        if (string.IsNullOrWhiteSpace(settings.VisionModel))
        {
            if (!PromptForLlmConfiguration(
                    "扫描版 PDF 需要视觉模型识别页面图片，请先在「设置 → LLM 设置」中填写视觉模型名称\n" +
                    "（建议 qwen 视觉系列，如 qwen3.7-flash-2026-07-15）。\n\n" +
                    "现在打开设置页面配置吗？"))
            {
                StatusText = "未配置视觉模型，已取消生成";
                return;
            }
            settings = LlmSettingsStore.Load();
            if (string.IsNullOrWhiteSpace(settings.VisionModel))
            {
                StatusText = "未配置视觉模型，已取消生成";
                return;
            }
        }

        var service = new OpenAiCompatibleService(settings);

        // 智能目录模式：先少量渲染找目录，读完目录后确认偏移推算书签
        var (ok, failReason) = await TryGenerateBookmarksByVisionSmartAsync(service, document, pageCount);
        if (ok)
        {
            return;
        }

        StatusText = "未找到目录，已停止（避免全本读取）";
        BookmarkGenerationStatus = failReason ?? "未找到目录或无法确认页码偏移";
        MessageBox.Show(
            "未生成书签：" + (failReason ?? "整本 PDF 中未找到目录页，或无法确认章节页码偏移。"),
            "PDFReader X",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>
    /// 扫描版智能目录模式：每批 10 页轻量探测是否含目录，找到目录后再读完目录，
    /// 并定位前几个章节标题确认页码偏移，推算全本书签。
    /// 找不到目录或偏移无法确认时返回 false，不再回退全本视觉识别。
    /// </summary>
    private async Task<(bool Ok, string? FailReason)> TryGenerateBookmarksByVisionSmartAsync(
        OpenAiCompatibleService service,
        PdfRenderService document,
        int pageCount)
    {
        const int tocBatchPages = 10;
        try
        {
            // 阶段一：逐批往后找目录，每批只发当前批，token 消耗恒定
            var tocStart = -1;
            for (var start = 0; start < pageCount; start += tocBatchPages)
            {
                var count = Math.Min(tocBatchPages, pageCount - start);
                SetGenerationStatus($"正在找目录…（第 {FormatPageRange(start + 1, count)}）");
                var images = await Task.Run(() => RenderPageImages(document, start, count));
                var isLastBatch = start + count >= pageCount;
                var probe = await service.ProbeVisionTocAsync(images, start + 1, isLastBatch);
                if (probe.HasToc)
                {
                    tocStart = start;
                    break;
                }
                if (isLastBatch)
                {
                    return (false, "整本 PDF 中未找到目录页");
                }
            }
            if (tocStart < 0)
            {
                return (false, "整本 PDF 中未找到目录页");
            }

            // 阶段二：从目录所在批开始累积读完整目录
            var tocImages = new List<byte[]>();
            TocResult? toc = null;
            for (var start = tocStart; start < pageCount; start += tocBatchPages)
            {
                var count = Math.Min(tocBatchPages, pageCount - start);
                SetGenerationStatus($"正在读取目录…（第 {FormatPageRange(start + 1, count)}，条目较多时需等待片刻）");
                var images = await Task.Run(() => RenderPageImages(document, start, count));
                tocImages.AddRange(images);

                var isLastBatch = start + count >= pageCount;
                var result = await service.GenerateVisionTocAsync(tocImages, isLastBatch, firstPageNumber: tocStart + 1);
                if (!result.HasToc)
                {
                    return (false, "目录读取中断（模型未再识别到目录）");
                }
                if (!result.TocComplete)
                {
                    if (isLastBatch)
                    {
                        toc = new TocResult { HasToc = true, TocComplete = true, Bookmarks = result.Bookmarks };
                        break;
                    }
                    continue; // 目录还没读完，继续往后读
                }
                toc = result;
                break;
            }
            if (toc is null || toc.Bookmarks.Count == 0)
            {
                return (false, "目录读取失败或目录为空");
            }

            SetGenerationStatus("目录读取成功，正在核对章节页码与 PDF 页码的偏移…");
            var (offset, failReason) = await ConfirmVisionOffsetAsync(service, document, pageCount, toc, tocStart + tocImages.Count);
            if (offset is null)
            {
                return (false, failReason ?? "无法确认章节页码偏移");
            }

            var bookmarks = BookmarkLocator.ShiftPages(toc.Bookmarks, offset.Value, pageCount);
            ApplyBookmarkPreview(bookmarks, pageCount);
            return (true, null);
        }
        catch (LlmOutputTruncatedException)
        {
            return (false, "模型输出被截断，请重试");
        }
    }

    /// <summary>章节标题与视觉定位结果匹配：去空白后相等，或较长标题包含较短标题（≥3 字）。</summary>
    private static bool TitleMatches(string a, string b)
    {
        var na = NormalizeTitle(a);
        var nb = NormalizeTitle(b);
        if (na.Length == 0 || nb.Length == 0)
        {
            return false;
        }
        if (string.Equals(na, nb, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return (na.Length >= 3 && na.Contains(nb, StringComparison.OrdinalIgnoreCase))
            || (nb.Length >= 3 && nb.Contains(na, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把接近的偏移（差距 ≤2 页）聚为一簇，返回按偏移升序排列的簇列表。</summary>
    private static List<List<int>> ClusterOffsets(IEnumerable<int> offsets)
    {
        var sorted = offsets.OrderBy(o => o).ToList();
        var clusters = new List<List<int>>();
        foreach (var offset in sorted)
        {
            var cluster = clusters.LastOrDefault(c => offset - c[^1] <= 2);
            if (cluster is null)
            {
                clusters.Add(new List<int> { offset });
            }
            else
            {
                cluster.Add(offset);
            }
        }
        return clusters;
    }

    /// <summary>从聚簇后的偏移取值：众数优先（并列取中间值），全部唯一时取中位数。</summary>
    private static int PickOffset(List<int> offsets)
    {
        var byCount = offsets
            .GroupBy(v => v)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToList();
        var maxCount = byCount[0].Count();
        if (maxCount == 1)
        {
            return offsets[offsets.Count / 2];
        }
        var top = byCount.Where(g => g.Count() == maxCount).Select(g => g.Key).ToList();
        return top[top.Count / 2];
    }

    /// <summary>
    /// 用章节标题定位校准已确认的偏移：在推算页前后各 2 页内让视觉模型定位章节标题，
    /// 多个章节的偏差多数一致（|偏差| ≤1）时修正偏移，抵消页码识别或目录页码的系统性 ±1 误差。
    /// </summary>
    private static async Task<int> CalibrateOffsetWithTitlesAsync(
        OpenAiCompatibleService service,
        PdfRenderService document,
        int pageCount,
        TocResult toc,
        int offset)
    {
        var chapters = toc.Bookmarks
            .Where(b => b.Children.Count > 0 || b.Title.Contains('章'))
            .Take(3)
            .ToList();
        if (chapters.Count == 0)
        {
            chapters = toc.Bookmarks.Take(3).ToList();
        }
        if (chapters.Count == 0)
        {
            return offset;
        }

        const int verifySpan = 5;
        var deltas = new List<int>();
        foreach (var chapter in chapters)
        {
            var predicted = chapter.PageIndex + offset + 1; // 1 基 PDF 页
            var start = Math.Max(0, predicted - 2);
            var count = Math.Min(verifySpan, pageCount - start);
            var images = await Task.Run(() => RenderPageImages(document, start, count));
            var pages = await service.LocateChapterPagesAsync(new[] { chapter }, images, start + 1);
            var hit = pages.FirstOrDefault(p => TitleMatches(chapter.Title, p.Title));
            if (hit is not null && Math.Abs(hit.PageNumber - predicted) <= 1)
            {
                deltas.Add(hit.PageNumber - predicted);
            }
        }
        if (deltas.Count < 2)
        {
            return offset;
        }
        var groups = deltas.GroupBy(d => d).OrderByDescending(g => g.Count()).ToList();
        if (groups[0].Count() > 1 || groups[0].Key == 0)
        {
            return offset + groups[0].Key;
        }
        return offset;
    }

    /// <summary>
    /// 分窗口渲染目录之后的页面，让视觉模型定位前几个章节标题的真实页码；
    /// 多个章节偏移一致（≥2）时确认偏移，并用确认偏移修正被模型重置的章节页码。
    /// </summary>
    private static async Task<(int? Offset, string? FailReason)> ConfirmVisionOffsetAsync(
        OpenAiCompatibleService service,
        PdfRenderService document,
        int pageCount,
        TocResult toc,
        int tocPdfPages)
    {
        // 方案一：识别页眉/页脚书本页码直接确认偏移，比章节标题定位更精确
        const int numberWindowPages = 20;
        if (tocPdfPages < pageCount)
        {
            var numberCount = Math.Min(numberWindowPages, pageCount - tocPdfPages);
            var numberImages = await Task.Run(() => RenderPageImages(document, tocPdfPages, numberCount));
            var locatedNumbers = await service.LocatePageNumbersAsync(numberImages, tocPdfPages + 1);
            var numberClusters = ClusterOffsets(locatedNumbers.Select(p => p.PdfPage - p.BookPage));
            var bestNumberCluster = numberClusters.OrderByDescending(c => c.Count).FirstOrDefault();
            if (bestNumberCluster is not null
                && bestNumberCluster.Count >= 3
                && numberClusters.Where(c => c != bestNumberCluster).Sum(c => c.Count) < bestNumberCluster.Count)
            {
                var pageNumberOffset = PickOffset(bestNumberCluster);
                // 页码识别可能有系统性 ±1 误差，用章节标题定位做最终校准
                pageNumberOffset = await CalibrateOffsetWithTitlesAsync(service, document, pageCount, toc, pageNumberOffset);
                return (pageNumberOffset, null);
            }
        }

        var chapters = toc.Bookmarks
            .Where(b => b.Children.Count > 0 || b.Title.Contains('章'))
            .Take(6)
            .ToList();
        if (chapters.Count < 2)
        {
            // 目录标题不含"章"字或无子节点时，放宽为直接取前几个顶层条目
            chapters = toc.Bookmarks.Take(6).ToList();
        }
        if (chapters.Count < 2)
        {
            return (null, "目录条目不足，无法核对页码偏移");
        }

        const int windowPages = 20;
        var located = new List<(LlmBookmark Chapter, int RealPdfPage)>();
        for (var window = 0; window < 8 && located.Count < 5; window++)
        {
            var start = tocPdfPages + window * windowPages;
            if (start >= pageCount)
            {
                break;
            }
            var count = Math.Min(windowPages, pageCount - start);
            var images = await Task.Run(() => RenderPageImages(document, start, count));
            var pages = await service.LocateChapterPagesAsync(chapters, images, start + 1);
            foreach (var page in pages)
            {
                var chapter = chapters.FirstOrDefault(c => TitleMatches(c.Title, page.Title));
                if (chapter is not null && !located.Any(l => l.Chapter == chapter))
                {
                    located.Add((chapter, page.PageNumber));
                }
            }
        }
        if (located.Count < 2)
        {
            return (null, $"在目录后最多 160 页内仅定位到 {located.Count} 个章节标题（需 ≥2 个）");
        }

        // 视觉定位与目录页码可能各有 1~2 页误差，把接近的偏移聚为一簇，取最大簇中位数作为确认偏移
        var rawOffsets = located
            .Select(l => l.RealPdfPage - 1 - l.Chapter.PageIndex)
            .ToList();
        var clusters = ClusterOffsets(rawOffsets);
        var bestCluster = clusters.OrderByDescending(c => c.Count).First();
        var otherVotes = clusters.Where(c => c != bestCluster).Sum(c => c.Count);
        if (bestCluster.Count < 2 || otherVotes >= bestCluster.Count)
        {
            return (null, $"已定位 {located.Count} 个章节标题，但页码偏移不一致（{string.Join("、", rawOffsets)}），无法确认");
        }
        var offset = PickOffset(bestCluster);

        // 用确认偏移反推章节应有页码，修正模型可能重置的页码（整棵子树一起平移）
        var locatedDeltas = new List<int>();
        foreach (var (chapter, realPage) in located)
        {
            var expectedIndex = realPage - 1 - offset;
            var delta = expectedIndex - chapter.PageIndex;
            if (delta != 0)
            {
                BookmarkLocator.ShiftSubtree(chapter, delta);
                locatedDeltas.Add(delta);
            }
        }
        // 所有已定位章节的修正量一致时，说明模型整体平移了页码，未定位章节一并修正
        if (located.Count >= 2 && locatedDeltas.Count > 0
            && locatedDeltas.Distinct().Count() == 1
            && located.Select(l => l.RealPdfPage - 1 - offset - l.Chapter.PageIndex).Distinct().Count() == 1)
        {
            foreach (var node in toc.Bookmarks)
            {
                if (!located.Any(l => l.Chapter == node))
                {
                    BookmarkLocator.ShiftSubtree(node, locatedDeltas[0]);
                }
            }
        }
        return (offset, null);
    }

    /// <summary>把 AI 结果展示到预览窗口，确认后应用到侧边栏书签树。</summary>    /// <summary>把 AI 结果展示到预览窗口，确认后应用到侧边栏书签树。</summary>    /// <summary>把 AI 结果展示到预览窗口，确认后应用到侧边栏书签树。</summary>
    private void ApplyBookmarkPreview(List<LlmBookmark> bookmarks, int pageCount)
    {
        var preview = new BookmarkPreviewWindow(bookmarks, pageCount)
        {
            Owner = Application.Current.MainWindow,
        };
        if (preview.ShowDialog() == true && preview.Result is not null)
        {
            Bookmarks.Clear();
            foreach (var bookmark in preview.Result)
            {
                Bookmarks.Add(bookmark);
            }
            StatusText = $"已应用 AI 生成的书签（{Bookmarks.Count} 个顶层）";
        }
        else
        {
            StatusText = "已取消应用 AI 书签";
        }
    }

    /// <summary>渲染指定页码范围（startIndex 起 count 页）为 PNG，供视觉模型识别。</summary>
    private static List<byte[]> RenderPageImages(
        PdfRenderService document,
        int startIndex,
        int count,
        IProgress<string>? progress = null)
    {
        var images = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            progress?.Report(count == 1
                ? $"正在渲染页面图片 {startIndex + 1} 页…"
                : $"正在渲染页面图片 {startIndex + i + 1}~{startIndex + count} 页…");
            using var bitmap = document.RenderPage(startIndex + i, 72);
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            images.Add(stream.ToArray());
        }
        return images;
    }

    private void SetGenerationStatus(string status)
    {
        StatusText = status;
        BookmarkGenerationStatus = status;
    }

    /// <summary>逐页提取文本供 LLM 使用：压缩空白并限制长度，防止请求过大。</summary>
    private static List<string> ExtractPageTexts(PdfRenderService document, int startIndex, int count)
    {
        const int frontMatterPages = 5; // 目录通常在前几页，尽量完整保留
        const int maxCharsFront = 4000;
        const int maxCharsPerPage = 1200;

        var texts = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var pageIndex = startIndex + i;
            var text = document.GetPageText(pageIndex) ?? string.Empty;
            text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var limit = pageIndex < frontMatterPages ? maxCharsFront : maxCharsPerPage;
            if (text.Length > limit)
            {
                text = text[..limit];
            }
            texts.Add(text);
        }
        return texts;
    }

    private async void Open()
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开 PDF 或批注文档",
            Filter = "PDF / 批注文档 (*.pdf;*.pdfrx)|*.pdf;*.pdfrx|PDF 文件 (*.pdf)|*.pdf|PDFReader X 批注 (*.pdfrx)|*.pdfrx",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (Path.GetExtension(dialog.FileName).Equals(".pdfrx", StringComparison.OrdinalIgnoreCase))
        {
            await OpenPdfrxAsync(dialog.FileName);
        }
        else
        {
            await OpenFileAsync(dialog.FileName);
        }
    }

    /// <summary>当前文档关联的 .pdfrx 批注文件路径；普通 PDF 打开或另存前为 null。</summary>
    public string? CurrentPdfrxPath { get; set; }

    /// <summary>打开 .pdfrx 批注包完成、等待画布恢复时触发（携带包数据）。</summary>
    public event Action<PdfrxPackage>? PdfrxReady;

    /// <summary>文档加载完成（含书签、画布恢复）后触发，供界面重置修改标记。</summary>
    public event Action? DocumentOpened;

    public async Task OpenFileAsync(string filePath)
    {
        try
        {
            var document = await Task.Run(() => PdfRenderService.Load(filePath));
            var sessionFile = SessionStore.FindSession(filePath);
            if (sessionFile is not null)
            {
                try
                {
                    var package = await Task.Run(() => PdfrxStore.Read(sessionFile));
                    await LoadAsync(document, package.Bookmarks);
                    PdfrxReady?.Invoke(package);
                    StatusText = $"已恢复上次阅读位置与书签 · {Path.GetFileName(filePath)}";
                    return;
                }
                catch
                {
                    // 会话包损坏时按普通 PDF 打开
                }
            }
            await LoadAsync(document);
        }
        catch (Exception ex)
        {
            StatusText = $"打开失败：{ex.Message}";
            MessageBox.Show(
                $"无法打开文件：\n{ex.Message}",
                "PDFReader X",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>打开 .pdfrx 批注包：解包读取内嵌 PDF 与画布状态，加载后触发 PdfrxReady 供画布恢复。</summary>
    public async Task OpenPdfrxAsync(string filePath)
    {
        try
        {
            StatusText = "正在读取批注文档…";
            var package = await Task.Run(() => PdfrxStore.Read(filePath));
            PdfRenderService document;
            if (package.PdfBytes.Length > 0)
            {
                document = await Task.Run(() => PdfRenderService.Load(
                    package.PdfBytes, Path.GetFileName(filePath), package.PdfSource));
            }
            else if (package.PdfSource is { } sourcePath && TryResolveSourcePdf(filePath, sourcePath, out var resolvedPath))
            {
                document = await Task.Run(() => PdfRenderService.Load(resolvedPath));
            }
            else if (AskUserForSourcePdf(package.PdfSource, out var chosenPath))
            {
                document = await Task.Run(() => PdfRenderService.Load(chosenPath!));
            }
            else
            {
                throw new InvalidDataException("已取消打开：批注包未内嵌 PDF，且未选择源 PDF 文件");
            }
            await LoadAsync(document, package.Bookmarks);
            CurrentPdfrxPath = filePath;
            PdfrxReady?.Invoke(package);
        }
        catch (Exception ex)
        {
            StatusText = $"打开失败：{ex.Message}";
            MessageBox.Show(
                $"无法打开批注文档：\n{ex.Message}",
                "PDFReader X",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>解析包内记录的源 PDF 路径：相对路径按包所在目录解析，并校验是存在的 .pdf 文件。</summary>
    private static bool TryResolveSourcePdf(string packagePath, string sourcePath, out string resolvedPath)
    {
        var candidate = Path.IsPathRooted(sourcePath)
            ? sourcePath
            : Path.Combine(Path.GetDirectoryName(packagePath) ?? string.Empty, sourcePath);
        resolvedPath = candidate;
        return File.Exists(candidate)
            && Path.GetExtension(candidate).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>引用式批注包找不到源 PDF 时，提示并让用户手动选择；返回 false 表示用户取消。</summary>
    private bool AskUserForSourcePdf(string? originalPath, out string? chosenPath)
    {
        chosenPath = null;
        var message = originalPath is null
            ? "该批注文档未包含 PDF 内容，也没有记录源 PDF 路径。\n请选择对应的 PDF 文件继续打开。"
            : $"该批注文档未内嵌 PDF，记录的源文件不存在：\n{originalPath}\n\n请选择对应的 PDF 文件继续打开（把源 PDF 放回原位置可免去选择）。";
        if (MessageBox.Show(message, "选择源 PDF", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
        {
            return false;
        }
        var dialog = new OpenFileDialog
        {
            Title = "选择批注文档对应的 PDF 文件",
            Filter = "PDF 文件 (*.pdf)|*.pdf|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return false;
        }
        chosenPath = dialog.FileName;
        return true;
    }

    private async Task LoadAsync(PdfRenderService document, IReadOnlyList<BookmarkData>? embeddedBookmarks = null)
    {
        IsBusy = true;
        try
        {
            StatusText = "正在加载 PDF…";
            CurrentPdfrxPath = null;
            CloseDocument();
            Document = document;

            HasDocument = true;
            FileName = Path.GetFileName(document.FilePath);
            Zoom = 1.0;
            StatusText = "正在创建页面…";

            const int batchSize = 32;
            for (var i = 0; i < document.PageCount; i++)
            {
                var size = document.GetPageSize(i);
                var width = Math.Ceiling(size.Width * DisplayDpi / 72.0);
                var height = Math.Ceiling(size.Height * DisplayDpi / 72.0);
                Pages.Add(new PageViewModel(i, width, height));

                if ((i + 1) % batchSize == 0)
                {
                    StatusText = $"正在创建页面 {i + 1}/{document.PageCount}…";
                    await Task.Yield(); // 让 UI 有机会刷新状态栏
                }
            }

            // 书签目录：.pdfrx 包内书签优先，否则后台读取 PDF 大纲，避免大 PDF 卡住 UI
            if (embeddedBookmarks is not null)
            {
                foreach (var data in embeddedBookmarks)
                {
                    Bookmarks.Add(ToBookmarkViewModel(data));
                }
            }
            else
            {
                try
                {
                    var outline = await Task.Run(() => document.GetOutline());
                    foreach (var node in outline)
                    {
                        Bookmarks.Add(BookmarkViewModel.FromCore(node));
                    }
                }
                catch
                {
                    // 个别 PDF 大纲读取失败不影响打开
                }
            }

            StartThumbnailGeneration(document);

            StatusText = $"已加载 {document.PageCount} 页 · {Path.GetFileName(document.FilePath)}";
            DocumentOpened?.Invoke();
        }
        finally
        {
            IsBusy = false;
            OpenCommand.NotifyCanExecuteChanged();
            CloseCommand.NotifyCanExecuteChanged();
        }
    }

    private void StartThumbnailGeneration(PdfRenderService document)
    {
        _thumbnailEpoch++;
        var epoch = _thumbnailEpoch;

        for (var i = 0; i < document.PageCount; i++)
        {
            var size = document.GetPageSize(i);
            var width = Math.Ceiling(size.Width * DisplayDpi / 72.0);
            var height = Math.Ceiling(size.Height * DisplayDpi / 72.0);
            Thumbnails.Add(new ThumbnailViewModel(i, width, height));
        }

        _ = GenerateThumbnailsAsync(document, epoch);
    }

    private async Task GenerateThumbnailsAsync(PdfRenderService document, int epoch)
    {
        const int targetWidthPx = 160;
        const int yieldInterval = 6;

        for (var i = 0; i < document.PageCount; i++)
        {
            if (epoch != _thumbnailEpoch)
            {
                return; // 文档已关闭或更换
            }

            BitmapSource? source = null;
            try
            {
                using var bitmap = document.RenderThumbnail(i, targetWidthPx);
                source = bitmap.ToBitmapSource();
            }
            catch
            {
                // 单页缩略图失败：跳过，不阻塞后续页
            }

            var index = i;
            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (epoch != _thumbnailEpoch || index >= Thumbnails.Count)
                    {
                        return;
                    }
                    Thumbnails[index].Thumbnail = source;
                });
            }
            catch
            {
                return; // 窗口已关闭
            }

            if (i % yieldInterval == yieldInterval - 1)
            {
                await Task.Delay(1); // 让出 CPU，保持 UI 流畅
            }
        }
    }

    private static BookmarkViewModel ToBookmarkViewModel(BookmarkData data)
    {
        var viewModel = new BookmarkViewModel(data.Title, data.PageIndex);
        foreach (var child in data.Children)
        {
            viewModel.Children.Add(ToBookmarkViewModel(child));
        }
        return viewModel;
    }

    private void Close()
    {
        CloseDocument();
        StatusText = "就绪";
    }

    private void CloseDocument()
    {
        _thumbnailEpoch++; // 取消进行中的缩略图生成
        _lastCurrentThumb = -1;
        Document?.Dispose();
        Document = null;
        HasDocument = false;
        FileName = "未打开文件";
        Pages.Clear();
        Thumbnails.Clear();
        Bookmarks.Clear();
        CurrentPageIndex = 0;
    }
}
