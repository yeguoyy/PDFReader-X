using System.IO;
using System.Windows.Media.Imaging;
using PDFReaderX.App.Controls;
using PDFReaderX.App.Helpers;
using PDFReaderX.Core.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PDFReaderX.App.Services;

/// <summary>把页面墨迹、自由墨迹与元素合并渲染导出为新的 PDF 文件。</summary>
public static class PdfExportService
{
    /// <summary>导出全部页面（150 DPI 合并渲染），输出为单文件 PDF。</summary>
    public static async Task ExportAsync(
        string outputPath,
        InfiniteCanvas canvas,
        PdfRenderService document,
        IProgress<string>? progress = null)
    {
        const int dpi = 150;
        using var pdf = new PdfDocument();
        pdf.Info.Title = Path.GetFileNameWithoutExtension(outputPath);

        for (var i = 0; i < document.PageCount; i++)
        {
            progress?.Report($"正在导出第 {i + 1}/{document.PageCount} 页…");
            var size = document.GetPageSize(i);

            using var baseImage = document.RenderPage(i, dpi, PdfiumViewer.PdfRenderFlags.LcdText);
            var baseSource = baseImage.ToBitmapSource();
            var merged = canvas.RenderPageWithAnnotations(i, baseSource, dpi);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(merged));
            using var memory = new MemoryStream();
            encoder.Save(memory);
            var pngBytes = memory.ToArray();

            var page = pdf.AddPage();
            page.Width = XUnit.FromPoint(size.Width);
            page.Height = XUnit.FromPoint(size.Height);

            using var graphics = XGraphics.FromPdfPage(page);
            using var pngStream = new MemoryStream(pngBytes);
            using var image = XImage.FromStream(pngStream);
            graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);

            if (i % 4 == 0)
            {
                await Task.Yield(); // 让状态栏有机会刷新
            }
        }

        pdf.Save(outputPath);
        progress?.Report("导出完成");
    }
}
