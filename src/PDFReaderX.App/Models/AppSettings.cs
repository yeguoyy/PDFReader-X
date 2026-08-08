namespace PDFReaderX.App.Models;

/// <summary>文本框边框样式选项（Key 用于持久化，Name 用于界面显示）。</summary>
public sealed record TextBorderStyleOption(string Key, string Name);

/// <summary>应用通用设置（本地持久化，不进入仓库）。</summary>
public sealed class AppSettings
{
    /// <summary>文本框边框样式 Key（默认黑色细虚线）。</summary>
    public string TextBoxBorderStyle { get; set; } = "black-dashed";
}
