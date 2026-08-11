namespace PDFReaderX.App.Models;

/// <summary>文本框边框样式选项（Key 用于持久化，Name 用于界面显示）。</summary>
public sealed record TextBorderStyleOption(string Key, string Name);

/// <summary>应用通用设置（本地持久化，不进入仓库）。</summary>
public sealed class AppSettings
{
    /// <summary>文本框边框样式 Key（默认黑色细虚线）。</summary>
    public string TextBoxBorderStyle { get; set; } = "black-dashed";

    /// <summary>上次关闭时的窗口宽度（null 表示未记录，用默认值）。</summary>
    public double? WindowWidth { get; set; }

    /// <summary>上次关闭时的窗口高度（null 表示未记录，用默认值）。</summary>
    public double? WindowHeight { get; set; }

    /// <summary>上次关闭时窗口是否最大化。</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>性能模式：降低渲染清晰度上限与缓存页数，适合配置较低的电脑。</summary>
    public bool PerformanceMode { get; set; }
}
