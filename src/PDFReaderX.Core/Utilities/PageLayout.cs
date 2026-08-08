namespace PDFReaderX.Core.Utilities;

/// <summary>
/// 连续垂直页面布局：计算每一页的顶部偏移。
/// </summary>
public static class PageLayout
{
    /// <summary>
    /// 按页面缩放后高度与页间距，计算每页顶部偏移（累计）。
    /// </summary>
    /// <param name="scaledHeights">各页缩放后的高度（长度须与页数一致）</param>
    /// <param name="gap">页间距</param>
    /// <returns>每页的顶部偏移数组</returns>
    public static double[] ComputeTopOffsets(IReadOnlyList<double> scaledHeights, double gap)
    {
        var offsets = new double[scaledHeights.Count];
        double top = 0;
        for (var i = 0; i < scaledHeights.Count; i++)
        {
            offsets[i] = top;
            top += scaledHeights[i] + gap;
        }
        return offsets;
    }
}
