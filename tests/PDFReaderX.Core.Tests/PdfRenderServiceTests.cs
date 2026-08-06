using PDFReaderX.Core.Services;

namespace PDFReaderX.Core.Tests;

public class PdfRenderServiceTests
{
    private static string SamplePdfPath =>
        Path.Combine(AppContext.BaseDirectory, "assets", "sample.pdf");

    [Fact]
    public void Load_ReportsExpectedPageCount()
    {
        using var service = PdfRenderService.Load(SamplePdfPath);
        Assert.Equal(3, service.PageCount);
    }

    [Fact]
    public void RenderPage1_ProducesBitmapAt96Dpi()
    {
        using var service = PdfRenderService.Load(SamplePdfPath);
        using var image = service.RenderPage(0, 96);

        Assert.NotNull(image);
        // Letter 612x792pt @ 96dpi → 816x1056 px
        Assert.InRange(image.Width, 810, 820);
        Assert.InRange(image.Height, 1050, 1062);
    }

    [Fact]
    public void GetPageText_ReturnsExpectedContent()
    {
        using var service = PdfRenderService.Load(SamplePdfPath);
        var text = service.GetPageText(0);
        Assert.Contains("PDFReader X", text);
    }
}
