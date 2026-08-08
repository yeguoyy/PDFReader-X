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
        Colors.Black, Colors.Red, Colors.Blue, Colors.Green, Colors.Orange, Colors.Purple,
    };

    public IReadOnlyList<Color> HighlightColors { get; } = new[]
    {
        Colors.Yellow, Colors.Lime, Colors.Cyan, Colors.Magenta, Colors.Orange,
    };

    public IReadOnlyList<double> PenWidths { get; } = new[] { 1.5, 3.0, 5.0 };
    public IReadOnlyList<double> HighlightWidths { get; } = new[] { 16.0, 24.0, 32.0 };
    public IReadOnlyList<double> EraserWidths { get; } = new[] { 8.0, 16.0, 32.0 };

    public RelayCommand OpenCommand { get; }

    public RelayCommand CloseCommand { get; }

    public RelayCommand OpenLlmSettingsCommand { get; }

    public RelayCommand OpenSettingsCommand { get; }

    public AsyncRelayCommand GenerateBookmarksCommand { get; }

    /// <summary>侧边栏的 AI 生成书签按钮（有文档且当前没有书签时可用/显示）。</summary>
    public AsyncRelayCommand AutoGenerateBookmarksCommand { get; }

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
        Bookmarks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasBookmarks));
            OnPropertyChanged(nameof(ShowAutoBookmarkButton));
            AutoGenerateBookmarksCommand.NotifyCanExecuteChanged();
        };
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
            StatusText = "请先配置 LLM 设置";
            OpenLlmSettings();
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

            // 回退：文本模式按 100 页一批处理，覆盖整本书
            const int batchPages = 100;
            var batches = (pageCount + batchPages - 1) / batchPages;
            var allBookmarks = new List<LlmBookmark>();
            for (var batch = 0; batch < batches; batch++)
            {
                var start = batch * batchPages;
                var count = Math.Min(batchPages, pageCount - start);
                SetGenerationStatus($"正在处理第 {batch + 1}/{batches} 批（第 {FormatPageRange(start + 1, count)}）…");
                var bookmarks = await GenerateTextBatchAsync(service, document, start, count);
                allBookmarks.AddRange(bookmarks);
            }
            ApplyBookmarkPreview(allBookmarks, pageCount);
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
    /// 智能目录模式：按批往后读文本交给 AI 识别目录，读完目录后提示用户，
    /// 再从目录之后定位前几个章节标题确认页码偏移，推算全本书签，不再把剩余页面交给 AI。
    /// 无目录、目录输出异常或偏移无法确认时返回 false，由调用方回退全量读取。
    /// </summary>
    private async Task<bool> TryGenerateBookmarksSmartAsync(
        OpenAiCompatibleService service,
        PdfRenderService document,
        int pageCount)
    {
        const int tocBatchPages = 10;
        const int maxTocSearchPages = 50; // 前 50 页内没找到目录就回退全量读取
        var frontTexts = new List<string>();
        try
        {
            var searchLimit = Math.Min(maxTocSearchPages, pageCount);
            for (var start = 0; start < searchLimit; start += tocBatchPages)
            {
                var count = Math.Min(tocBatchPages, pageCount - start);
                SetGenerationStatus($"正在读取目录…（第 {FormatPageRange(start + 1, count)}）");
                var batch = await Task.Run(() => ExtractPageTexts(document, start, count));
                frontTexts.AddRange(batch);

                var isLastBatch = start + count >= pageCount;
                var toc = await service.GenerateTocAsync(frontTexts, isLastBatch);
                if (!toc.HasToc)
                {
                    if (isLastBatch)
                    {
                        return false; // 整本读完都没有目录
                    }
                    continue; // 目录还没出现，继续往后读
                }
                if (!toc.TocComplete)
                {
                    if (isLastBatch)
                    {
                        toc = new TocResult { HasToc = true, TocComplete = true, Bookmarks = toc.Bookmarks };
                    }
                    else
                    {
                        continue; // 目录还没读完，继续往后读
                    }
                }

                SetGenerationStatus("目录读取成功，正在核对章节页码与 PDF 页码的偏移…");
                var allTexts = await Task.Run(() => ExtractAllPageTexts(document, pageCount));
                var offset = await Task.Run(() =>
                    BookmarkLocator.ConfirmOffset(toc.Bookmarks, allTexts, frontTexts.Count));
                if (offset is null)
                {
                    return false; // 无法确认偏移，回退全量
                }

                var bookmarks = BookmarkLocator.ShiftPages(toc.Bookmarks, offset.Value, pageCount);
                ApplyBookmarkPreview(bookmarks, pageCount);
                return true;
            }
            return false;
        }
        catch (LlmOutputTruncatedException)
        {
            return false; // 目录输出异常，回退全量
        }
    }

    /// <summary>页码范围文案：单页显示"x 页"，多页显示"x~y 页"。</summary>
    private static string FormatPageRange(int startPage, int count) =>
        count <= 1 ? $"{startPage} 页" : $"{startPage}~{startPage + count - 1} 页";

    /// <summary>提取全部页面文本（每页截断），供标题定位使用。</summary>
    private static List<string> ExtractAllPageTexts(PdfRenderService document, int pageCount)
    {
        const int maxCharsPerPage = 2000;
        var texts = new List<string>(pageCount);
        for (var i = 0; i < pageCount; i++)
        {
            var text = document.GetPageText(i) ?? string.Empty;
            text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length > maxCharsPerPage)
            {
                text = text[..maxCharsPerPage];
            }
            texts.Add(text);
        }
        return texts;
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
            StatusText = "未配置视觉模型，请先在 LLM 设置中填写";
            OpenLlmSettings();
            return;
        }

        var service = new OpenAiCompatibleService(settings);

        // 智能目录模式：先少量渲染找目录，读完目录后确认偏移推算书签
        if (await TryGenerateBookmarksByVisionSmartAsync(service, document, pageCount))
        {
            return;
        }

        // 视觉模式：30 页一批，最多 3 个请求并发，覆盖整本书
        const int visionBatchPages = 30;
        const int maxConcurrency = 3;
        var batches = (pageCount + visionBatchPages - 1) / visionBatchPages;
        var results = new List<LlmBookmark>[batches];
        var completedBatches = 0;
        using var gate = new SemaphoreSlim(maxConcurrency);
        IProgress<string> progress = new Progress<string>(SetGenerationStatus);
        var tasks = new List<Task>();
        for (var batch = 0; batch < batches; batch++)
        {
            var index = batch;
            var start = batch * visionBatchPages;
            var count = Math.Min(visionBatchPages, pageCount - start);
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync();
                try
                {
                    results[index] = await GenerateVisionBatchAsync(
                        service, document, start, count, settings.VisionModel, progress);
                    var done = Interlocked.Increment(ref completedBatches);
                    progress.Report($"AI 看图生成书签：已完成 {done}/{batches} 批…");
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        await Task.WhenAll(tasks);

        var bookmarks = new List<LlmBookmark>();
        foreach (var result in results)
        {
            if (result is not null)
            {
                bookmarks.AddRange(result);
            }
        }
        if (bookmarks.Count == 0)
        {
            StatusText = "AI 未能识别出有效书签";
            MessageBox.Show(
                "AI 未能从页面图片中识别出有效书签，请重试或检查视觉模型配置。",
                "PDFReader X",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        ApplyBookmarkPreview(bookmarks, pageCount);
    }

    /// <summary>
    /// 扫描版智能目录模式：按批渲染少量页面图片，让视觉模型识别目录；
    /// 目录读完后提示用户，再渲染目录之后的少量页面定位前几个章节的真实页码，
    /// 确认偏移后推算全本书签，不再渲染剩余页面。
    /// 无目录或无法确认偏移时返回 false，回退全本视觉识别。
    /// </summary>
    private async Task<bool> TryGenerateBookmarksByVisionSmartAsync(
        OpenAiCompatibleService service,
        PdfRenderService document,
        int pageCount)
    {
        const int tocBatchPages = 10;
        const int maxTocSearchPages = 20; // 前 20 页内没找到目录就回退全本识别
        var tocImages = new List<byte[]>();
        TocResult? toc = null;
        try
        {
            var searchLimit = Math.Min(maxTocSearchPages, pageCount);
            for (var start = 0; start < searchLimit; start += tocBatchPages)
            {
                var count = Math.Min(tocBatchPages, pageCount - start);
                SetGenerationStatus($"正在渲染并识别目录…（第 {FormatPageRange(start + 1, count)}）");
                var images = await Task.Run(() => RenderPageImages(document, start, count));
                tocImages.AddRange(images);

                var isLastBatch = start + count >= pageCount;
                var result = await service.GenerateVisionTocAsync(tocImages, isLastBatch);
                if (!result.HasToc)
                {
                    if (isLastBatch)
                    {
                        return false;
                    }
                    continue; // 目录还没出现，继续往后读
                }
                if (!result.TocComplete)
                {
                    if (isLastBatch)
                    {
                        toc = result;
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
            var offset = await ConfirmVisionOffsetAsync(service, document, pageCount, toc, tocImages.Count);
            if (offset is null)
            {
                return false; // 无法确认偏移，回退全本识别
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

    /// <summary>
    /// 分窗口渲染目录之后的页面，让视觉模型定位前几个章节标题的真实页码；
    /// 多个章节偏移一致（≥2）时确认偏移，并用确认偏移修正被模型重置的章节页码。
    /// </summary>
    private static async Task<int?> ConfirmVisionOffsetAsync(
        OpenAiCompatibleService service,
        PdfRenderService document,
        int pageCount,
        TocResult toc,
        int tocPdfPages)
    {
        var chapters = toc.Bookmarks
            .Where(b => b.Children.Count > 0 || b.Title.Contains('章'))
            .Take(6)
            .ToList();
        if (chapters.Count < 2)
        {
            return null;
        }

        const int windowPages = 20;
        var located = new List<(LlmBookmark Chapter, int RealPdfPage)>();
        for (var window = 0; window < 4 && located.Count < 3; window++)
        {
            var start = tocPdfPages + window * windowPages;
            if (start >= pageCount)
            {
                break;
            }
            var count = Math.Min(windowPages, pageCount - start);
            var images = RenderPageImages(document, start, count);
            var pages = await service.LocateChapterPagesAsync(chapters, images, start + 1);
            foreach (var page in pages)
            {
                var chapter = chapters.FirstOrDefault(c => c.Title == page.Title);
                if (chapter is not null && !located.Any(l => l.Chapter == chapter))
                {
                    located.Add((chapter, page.PageNumber));
                }
            }
        }
        if (located.Count < 2)
        {
            return null;
        }

        var offsets = located
            .Select(l => l.RealPdfPage - 1 - l.Chapter.PageIndex)
            .GroupBy(o => o)
            .OrderByDescending(g => g.Count())
            .ToList();
        var best = offsets[0];
        if (best.Count() < 2 || (offsets.Count > 1 && offsets[1].Count() == best.Count()))
        {
            return null; // 票数不足或并列，无法确认
        }
        var offset = best.Key;

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
        return offset;
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

    /// <summary>文本批处理：AI 输出截断时自动拆半重试，直到成功。</summary>
    private async Task<List<LlmBookmark>> GenerateTextBatchAsync(
        OpenAiCompatibleService service,
        PdfRenderService document,
        int start,
        int count)
    {
        try
        {
            var pageTexts = await Task.Run(() => ExtractPageTexts(document, start, count));
            return await service.GenerateBookmarksAsync(pageTexts, start + 1);
        }
        catch (LlmOutputTruncatedException) when (count > 1)
        {
            var half = count / 2;
            SetGenerationStatus($"输出过长，正在把第 {FormatPageRange(start + 1, count)} 拆成两组重试…");
            var left = await GenerateTextBatchAsync(service, document, start, half);
            var right = await GenerateTextBatchAsync(service, document, start + half, count - half);
            left.AddRange(right);
            return left;
        }
    }

    /// <summary>视觉批处理：AI 输出截断时自动拆半重试，直到成功。</summary>
    private static async Task<List<LlmBookmark>> GenerateVisionBatchAsync(
        OpenAiCompatibleService service,
        PdfRenderService document,
        int start,
        int count,
        string visionModel,
        IProgress<string> progress)
    {
        try
        {
            var images = await Task.Run(() => RenderPageImages(document, start, count, progress));
            return await service.GenerateBookmarksFromImagesAsync(images, visionModel, start + 1);
        }
        catch (LlmOutputTruncatedException) when (count > 1)
        {
            var half = count / 2;
            progress.Report($"输出过长，正在把第 {FormatPageRange(start + 1, count)} 拆成两组重试…");
            var left = await GenerateVisionBatchAsync(service, document, start, half, visionModel, progress);
            var right = await GenerateVisionBatchAsync(service, document, start + half, count - half, visionModel, progress);
            left.AddRange(right);
            return left;
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
            Title = "打开 PDF 文件",
            Filter = "PDF 文件 (*.pdf)|*.pdf|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await OpenFileAsync(dialog.FileName);
    }

    public async Task OpenFileAsync(string filePath)
    {
        try
        {
            await LoadAsync(filePath);
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

    private async Task LoadAsync(string filePath)
    {
        IsBusy = true;
        try
        {
            StatusText = "正在加载 PDF…";
            var document = await Task.Run(() => PdfRenderService.Load(filePath));
            CloseDocument();
            Document = document;

            HasDocument = true;
            FileName = Path.GetFileName(filePath);
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

            // 书签目录：后台读取，避免大 PDF 卡住 UI
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

            StartThumbnailGeneration(document);

            StatusText = $"已加载 {document.PageCount} 页 · {Path.GetFileName(filePath)}";
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
