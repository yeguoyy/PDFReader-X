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

    [Fact]
    public void GetOutline_ReturnsBookmarkTree()
    {
        using var service = PdfRenderService.Load(SamplePdfPath);
        var outline = service.GetOutline();

        Assert.Equal(3, outline.Count);
        Assert.Equal("Page 1 - Introduction", outline[0].Title);
        Assert.Equal(0, outline[0].PageIndex);
        Assert.Equal("Page 2 - Bookmarks and Ink", outline[1].Title);
        Assert.Equal(1, outline[1].PageIndex);

        // 第三项带一个子书签
        Assert.Equal("Page 3 - LLM Bookmarks", outline[2].Title);
        Assert.Equal(2, outline[2].PageIndex);
        var child = Assert.Single(outline[2].Children);
        Assert.Equal("LLM Generation Test", child.Title);
        Assert.Equal(2, child.PageIndex);
    }

    [Fact]
    public void RenderThumbnail_ProducesBitmapNearTargetWidth()
    {
        using var service = PdfRenderService.Load(SamplePdfPath);
        using var image = service.RenderThumbnail(0, 160);

        Assert.NotNull(image);
        Assert.InRange(image.Width, 150, 175);
        Assert.InRange(image.Height, 195, 230);
    }
}
