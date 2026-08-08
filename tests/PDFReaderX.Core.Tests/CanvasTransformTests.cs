using PDFReaderX.Core.Utilities;

namespace PDFReaderX.Core.Tests;

public class CanvasTransformTests
{
    [Fact]
    public void ZoomAt_KeepsCenterPointFixed()
    {
        // 缩放中心在视口中的世界坐标应保持不变
        double cx = 400, cy = 300;
        double panX = -120, panY = 45;
        double oldZoom = 1.0, newZoom = 2.0;

        double oldWorldX = (cx - panX) / oldZoom;
        double oldWorldY = (cy - panY) / oldZoom;

        var (newPanX, newPanY) = CanvasTransform.ZoomAt(cx, cy, panX, panY, oldZoom, newZoom);

        double newWorldX = (cx - newPanX) / newZoom;
        double newWorldY = (cy - newPanY) / newZoom;

        Assert.Equal(oldWorldX, newWorldX, 6);
        Assert.Equal(oldWorldY, newWorldY, 6);
    }

    [Fact]
    public void ZoomAt_IdentityZoom_KeepsPan()
    {
        var (x, y) = CanvasTransform.ZoomAt(100, 200, 30, -40, 1.0, 1.0);
        Assert.Equal(30, x, 6);
        Assert.Equal(-40, y, 6);
    }

    [Fact]
    public void ScreenToWorld_ConvertsBackWithZoomAndPan()
    {
        var (wx, wy) = CanvasTransform.ScreenToWorld(500, 300, -100, 50, 2.5);
        Assert.Equal((500 - (-100)) / 2.5, wx, 6);
        Assert.Equal((300 - 50) / 2.5, wy, 6);
    }
}

public class PageLayoutTests
{
    [Fact]
    public void ComputeTopOffsets_AccumulatesHeightsAndGaps()
    {
        var offsets = PageLayout.ComputeTopOffsets(new[] { 100.0, 200.0, 300.0 }, gap: 24);
        Assert.Equal(3, offsets.Length);
        Assert.Equal(0, offsets[0], 6);
        Assert.Equal(124, offsets[1], 6);
        Assert.Equal(124 + 200 + 24, offsets[2], 6);
    }

    [Fact]
    public void ComputeTopOffsets_EmptyList_ReturnsEmpty()
    {
        var offsets = PageLayout.ComputeTopOffsets(Array.Empty<double>(), 24);
        Assert.Empty(offsets);
    }
}
