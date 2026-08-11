using System.IO;
using System.IO.Compression;
using System.Text.Json;
using PDFReaderX.App.Controls;
using PDFReaderX.App.Models;
using PDFReaderX.App.ViewModels;
using PDFReaderX.Core.Services;

namespace PDFReaderX.App.Services;

/// <summary>
/// .pdfrx 包读写：ZIP 压缩包，内含 manifest.json / canvas.json / bookmarks.json、
/// ink/*.isf 墨迹、images/* 图片、pdf/original.pdf 原始 PDF 副本。
/// </summary>
public static class PdfrxStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>把当前画布与书签打包保存为 .pdfrx（写文件在后台线程，不阻塞 UI）。</summary>
    public static async Task SaveAsync(
        string path,
        InfiniteCanvas canvas,
        PdfRenderService document,
        IReadOnlyList<BookmarkViewModel> bookmarks,
        IProgress<string>? progress = null,
        bool embedPdf = true)
    {
        progress?.Report("正在收集画布内容…");
        var (elements, images) = canvas.ExportElements();
        var bookmarkData = bookmarks.Select(ToData).ToList();
        var freeInk = canvas.ExportFreeInk();
        var pageCount = document.PageCount;
        var pageInks = new Dictionary<int, byte[]>();
        for (var i = 0; i < pageCount; i++)
        {
            var ink = canvas.ExportPageInk(i);
            if (ink is { Length: > 0 })
            {
                pageInks[i] = ink;
            }
        }

        progress?.Report("正在写入文件…");
        var zoom = canvas.Zoom;
        var panX = canvas.PanX;
        var panY = canvas.PanY;
        // 真实源 PDF 路径：文件打开的用文件路径，内嵌包打开的用包内记录（若仍不可用则回退内嵌，避免生成打不开的引用式包）
        var sourcePath = document.SourcePdfPath ?? document.FilePath;
        if (!File.Exists(sourcePath)
            || !Path.GetExtension(sourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            embedPdf = true;
        }
        var pdfBytes = embedPdf ? await Task.Run(() => document.GetSourceBytes()) : null;
        await Task.Run(() => WriteArchive(
            path, sourcePath, embedPdf, zoom, panX, panY,
            elements, images, pageInks, freeInk, bookmarkData, pdfBytes));
        progress?.Report("保存完成");
    }

    private static void WriteArchive(
        string path,
        string sourcePath,
        bool embedPdf,
        double zoom,
        double panX,
        double panY,
        List<CanvasElementData> elements,
        Dictionary<string, byte[]> images,
        Dictionary<int, byte[]> pageInks,
        byte[]? freeInk,
        List<BookmarkData> bookmarkData,
        byte[]? pdfBytes)
    {
        // 先写临时文件再原子替换：直接截断覆盖已存在文件时，
        // 若文件正被杀毒软件等以内存映射方式扫描（大文件常见），会报
        // "请求的操作无法在使用用户映射区域打开的文件上执行"，故改为替换并重试。
        var tmpPath = path + ".tmp";
        try
        {
            var createdAt = File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.Now;
            using (var archiveStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create))
            {
                WriteJson(archive, "manifest.json", new
                {
                    version = 1,
                    createdAt,
                    modifiedAt = DateTime.Now,
                    pdfSource = sourcePath,
                    pdfEmbedded = embedPdf,
                });

                WriteJson(archive, "canvas.json", new
                {
                    zoom,
                    offsetX = panX,
                    offsetY = panY,
                    layout = "continuous-vertical",
                    elements,
                });

                if (bookmarkData.Count > 0)
                {
                    WriteJson(archive, "bookmarks.json", bookmarkData);
                }

                foreach (var (pageIndex, ink) in pageInks)
                {
                    WriteBytes(archive, $"ink/page_{pageIndex:D3}.isf", ink);
                }

                if (freeInk is { Length: > 0 })
                {
                    WriteBytes(archive, "ink/free.isf", freeInk);
                }

                foreach (var (fileName, bytes) in images)
                {
                    WriteBytes(archive, $"images/{fileName}", bytes);
                }

                if (embedPdf && pdfBytes is not null)
                {
                    // PDF 内部已压缩，不再二次压缩：体积几乎不变，保存大文件时避免长时间卡顿
                    WriteBytes(archive, "pdf/original.pdf", pdfBytes, CompressionLevel.NoCompression);
                }
            }

            // 目标文件可能正被扫描占用，替换失败时短暂等待后重试（最多 6 次）
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmpPath, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (
                    attempt < 5 &&
                    (ex is IOException or UnauthorizedAccessException))
                {
                    Thread.Sleep(1000);
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tmpPath))
                {
                    File.Delete(tmpPath);
                }
            }
            catch
            {
                // 临时文件清理失败不影响结果
            }
        }
    }

    /// <summary>读取 .pdfrx 包为内存数据（不触碰画布），供打开后恢复。</summary>
    public static PdfrxPackage Read(string path)
    {
        var package = new PdfrxPackage
        {
            PdfBytes = Array.Empty<byte>(),
        };

        using var archive = ZipFile.OpenRead(path);

        var manifestEntry = archive.GetEntry("manifest.json");
        if (manifestEntry is not null)
        {
            using var manifestStream = manifestEntry.Open();
            using var manifest = JsonDocument.Parse(manifestStream);
            if (manifest.RootElement.TryGetProperty("pdfSource", out var source))
            {
                package.PdfSource = source.GetString();
            }
        }

        var canvasEntry = archive.GetEntry("canvas.json")
            ?? throw new InvalidDataException("包内缺少 canvas.json");
        using (var canvasStream = canvasEntry.Open())
        {
            var canvas = JsonSerializer.Deserialize<CanvasJson>(canvasStream, JsonOptions)
                ?? throw new InvalidDataException("canvas.json 解析失败");
            package.Zoom = canvas.zoom;
            package.PanX = canvas.offsetX;
            package.PanY = canvas.offsetY;
            if (canvas.elements is not null)
            {
                package.Elements.AddRange(canvas.elements);
            }
        }

        var bookmarkEntry = archive.GetEntry("bookmarks.json");
        if (bookmarkEntry is not null)
        {
            using var bookmarkStream = bookmarkEntry.Open();
            var bookmarks = JsonSerializer.Deserialize<List<BookmarkData>>(bookmarkStream, JsonOptions);
            if (bookmarks is not null)
            {
                package.Bookmarks.AddRange(bookmarks);
            }
        }

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.StartsWith("ink/", StringComparison.Ordinal) && entry.FullName.EndsWith(".isf", StringComparison.Ordinal))
            {
                var bytes = ReadAll(entry);
                if (entry.FullName == "ink/free.isf")
                {
                    package.FreeInk = bytes;
                }
                else if (int.TryParse(Path.GetFileNameWithoutExtension(entry.FullName)["page_".Length..], out var pageIndex))
                {
                    package.PageInks[pageIndex] = bytes;
                }
            }
            else if (entry.FullName.StartsWith("images/", StringComparison.Ordinal))
            {
                package.Images[Path.GetFileName(entry.FullName)] = ReadAll(entry);
            }
            else if (entry.FullName == "pdf/original.pdf")
            {
                package.PdfBytes = ReadAll(entry);
            }
        }

        return package;
    }

    private static BookmarkData ToData(BookmarkViewModel viewModel)
    {
        var data = new BookmarkData { Title = viewModel.Title, PageIndex = viewModel.PageIndex };
        foreach (var child in viewModel.Children)
        {
            data.Children.Add(ToData(child));
        }
        return data;
    }

    private static void WriteJson(ZipArchive archive, string entryName, object value)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(JsonSerializer.Serialize(value, JsonOptions));
    }

    private static void WriteBytes(ZipArchive archive, string entryName, byte[] bytes, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = archive.CreateEntry(entryName, level);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private sealed class CanvasJson
    {
        public double zoom { get; set; }
        public double offsetX { get; set; }
        public double offsetY { get; set; }
        public string? layout { get; set; }
        public List<CanvasElementData>? elements { get; set; }
    }
}
