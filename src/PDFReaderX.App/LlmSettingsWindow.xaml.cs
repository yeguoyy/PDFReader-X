using System.Windows;
using PDFReaderX.App.ViewModels;

namespace PDFReaderX.App;

/// <summary>LLM 设置对话框。</summary>
public partial class LlmSettingsWindow : Window
{
    private readonly LlmSettingsViewModel _viewModel;

    public LlmSettingsWindow()
    {
        InitializeComponent();
        _viewModel = new LlmSettingsViewModel();
        DataContext = _viewModel;
        ApiKeyBox.Password = _viewModel.ApiKey;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _viewModel.ApiKey = ApiKeyBox.Password;
        _viewModel.Save();
        DialogResult = true;
    }
}
