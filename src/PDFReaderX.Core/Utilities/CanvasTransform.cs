namespace PDFReaderX.Core.Utilities;

/// <summary>
/// 无限画布的坐标变换数学：平移 + 缩放，全部与 WPF 控件解耦，便于单元测试。
/// </summary>
public static class CanvasTransform
{
    /// <summary>
    /// 以屏幕上一点为中心缩放：缩放前后该点在视口中的位置保持不变。
    /// </summary>
    /// <param name="screenCenter">缩放中心（视口坐标）</param>
    /// <param name="pan">当前平移量（视口坐标 → 世界坐标的偏移）</param>
    /// <param name="oldZoom">旧缩放比例</param>
    /// <param name="newZoom">新缩放比例</param>
    /// <returns>缩放后应设置的平移量</returns>
    public static (double X, double Y) ZoomAt(
        double screenCenterX, double screenCenterY,
        double panX, double panY,
        double oldZoom, double newZoom)
    {
        // 世界坐标 = (屏幕坐标 - 平移) / 缩放
        double worldX = (screenCenterX - panX) / oldZoom;
        double worldY = (screenCenterY - panY) / oldZoom;
        // 新平移 = 屏幕坐标 - 世界坐标 * 新缩放
        return (screenCenterX - worldX * newZoom, screenCenterY - worldY * newZoom);
    }

    /// <summary>
    /// 屏幕坐标 → 世界坐标。
    /// </summary>
    public static (double X, double Y) ScreenToWorld(
        double screenX, double screenY, double panX, double panY, double zoom)
        => ((screenX - panX) / zoom, (screenY - panY) / zoom);
}
