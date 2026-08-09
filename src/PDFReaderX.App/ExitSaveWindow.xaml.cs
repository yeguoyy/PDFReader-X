using System.Windows;

namespace PDFReaderX.App;

public partial class ExitSaveWindow : Window
{
    public ExitSaveWindow()
    {
        InitializeComponent();
    }

    /// <summary>更新保存阶段文字与进度条（收集画布 → 写入文件 → 完成）。</summary>
    public void SetStatus(string text)
    {
        StatusText.Text = text;
        Progress.Value = text switch
        {
            var t when t.Contains("收集") => 25,
            var t when t.Contains("写入") => 65,
            var t when t.Contains("完成") => 100,
            _ => Progress.Value,
        };
    }

    public void SetCompleted()
    {
        StatusText.Text = "保存完成";
        Progress.Value = 100;
    }

    /// <summary>保存失败时询问是否仍然退出，返回 true 表示继续退出。</summary>
    public bool AskExitAfterFailure(string message)
    {
        return MessageBox.Show(
            this,
            $"退出前自动保存失败：\n{message}\n\n仍然退出吗？",
            "PDFReader X",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }
}
