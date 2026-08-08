using System.Windows;

namespace PDFReaderX.App;

/// <summary>扫描版 PDF 的视觉模式确认对话框，支持"不再提示"。</summary>
public partial class VisionConfirmWindow : Window
{
    public VisionConfirmWindow()
    {
        InitializeComponent();
    }

    /// <summary>用户勾选了"不再提示"。</summary>
    public bool DontAskAgain => DontAskBox.IsChecked == true;

    private void OnYesClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
