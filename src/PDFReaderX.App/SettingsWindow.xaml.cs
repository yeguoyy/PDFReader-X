using System.Windows;
using PDFReaderX.App.ViewModels;

namespace PDFReaderX.App;

/// <summary>应用设置对话框：LLM、书签与通用设置。</summary>
public partial class SettingsWindow : Window
{
    private readonly LlmSettingsViewModel _llmViewModel = new();

    public SettingsWindow(MainWindowViewModel ownerViewModel)
    {
        InitializeComponent();
        DataContext = ownerViewModel;
        LlmPanel.DataContext = _llmViewModel;
        ApiKeyBox.Password = _llmViewModel.ApiKey;
    }

    private void OnLlmSaveClick(object sender, RoutedEventArgs e)
    {
        _llmViewModel.ApiKey = ApiKeyBox.Password;
        _llmViewModel.Save();
        SaveStatusText.Text = "已保存";
    }
}
