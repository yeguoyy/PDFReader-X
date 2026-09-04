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

    /// <summary>侧栏宽度；null 表示按默认值初始化。</summary>
    public double? SidebarWidth { get; set; }

    /// <summary>性能模式：降低渲染清晰度上限与缓存页数，适合配置较低的电脑。</summary>
    public bool PerformanceMode { get; set; }

    /// <summary>最近使用的颜色（"#RRGGBB" 列表，最多 8 个）。</summary>
    public List<string> RecentColors { get; set; } = new();

    /// <summary>用户自定义快捷笔列表（空表示使用默认预设）。</summary>
    public List<QuickPenData> QuickPens { get; set; } = new();
}

/// <summary>快捷笔的本地持久化数据（JSON 友好格式）。</summary>
public sealed class QuickPenData
{
    public string Key { get; set; } = string.Empty;

    /// <summary>InkTool 枚举名（Pen/Highlighter）。</summary>
    public string Tool { get; set; } = "Pen";

    /// <summary>笔种标识（"pen"/"highlighter"，决定图标外观）。</summary>
    public string Kind { get; set; } = "pen";

    /// <summary>颜色（"#RRGGBB"）。</summary>
    public string Color { get; set; } = "#000000";

    public double Width { get; set; } = 2.5;

    public string Name { get; set; } = string.Empty;
}
