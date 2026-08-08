using CommunityToolkit.Mvvm.ComponentModel;
using PDFReaderX.LLM;

namespace PDFReaderX.App.ViewModels;

/// <summary>LLM 设置对话框的 ViewModel。</summary>
public sealed partial class LlmSettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _endpoint;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private string _model;

    [ObservableProperty]
    private string _visionModel;

    [ObservableProperty]
    private bool _skipVisionConfirm;

    public LlmSettingsViewModel()
    {
        var settings = LlmSettingsStore.Load();
        Endpoint = settings.Endpoint;
        ApiKey = settings.ApiKey;
        Model = settings.Model;
        VisionModel = settings.VisionModel;
        SkipVisionConfirm = settings.SkipVisionConfirm;
    }

    /// <summary>保存配置。调用方需先把 PasswordBox 内容写入 ApiKey。</summary>
    public void Save()
    {
        LlmSettingsStore.Save(new LlmSettings
        {
            Endpoint = Endpoint.Trim(),
            ApiKey = ApiKey.Trim(),
            Model = Model.Trim(),
            VisionModel = VisionModel.Trim(),
            SkipVisionConfirm = SkipVisionConfirm,
        });
    }
}
