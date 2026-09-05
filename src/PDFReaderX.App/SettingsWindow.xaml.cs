using System.Diagnostics;
using System.Windows;
using PDFReaderX.App.Services;
using PDFReaderX.App.ViewModels;

namespace PDFReaderX.App;

/// <summary>应用设置对话框：LLM、书签、通用设置与检查更新。</summary>
public partial class SettingsWindow : Window
{
    private readonly GitHubUpdateService _updateService = new();
    private CancellationTokenSource? _updateCancellation;
    private bool _isCheckingUpdate;
    private GitHubReleaseUpdate? _availableUpdate;

    private readonly LlmSettingsViewModel _llmViewModel = new();

    public SettingsWindow(MainWindowViewModel ownerViewModel)
    {
        InitializeComponent();
        DataContext = ownerViewModel;
        LlmPanel.DataContext = _llmViewModel;
        ApiKeyBox.Password = _llmViewModel.ApiKey;

        var currentVersion = typeof(SettingsWindow).Assembly.GetName().Version;
        CurrentVersionText.Text = $"v{(currentVersion?.ToString(3) ?? "unknown")}";

        Loaded += async (_, _) => await CheckForUpdateAsync();
    }

    private void OnLlmSaveClick(object sender, RoutedEventArgs e)
    {
        _llmViewModel.ApiKey = ApiKeyBox.Password;
        _llmViewModel.Save();
        SaveStatusText.Text = "已保存";
    }

    private void OnOpenReleasePageClick(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = GitHubUpdateService.RepositoryUrl,
            UseShellExecute = true,
        });
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        await CheckForUpdateAsync();
    }

    private async void OnOneClickUpdateClick(object sender, RoutedEventArgs e)
    {
        var update = _availableUpdate;
        if (update is null || _isCheckingUpdate || _updateCancellation is null)
        {
            return;
        }

        OneClickUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateStatusText.Text = "正在下载更新安装包…";

        try
        {
            var progress = new Progress<double>(value => UpdateProgressBar.Value = Math.Clamp(value, 0, 100));
            var installerPath = await _updateService.DownloadInstallerAsync(
                update, progress, _updateCancellation.Token);

            UpdateStatusText.Text = "下载完成，正在启动更新程序…";
            GitHubUpdateService.LaunchInstaller(installerPath);
            UpdateStatusText.Text = "更新程序已启动。安装器会关闭当前应用，完成更新后自动重启。";
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText.Text = "更新已取消。";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"下载更新失败：{ex.Message}";
        }
        finally
        {
            if (_isCheckingUpdate)
            {
                // 检查流程已经完成；这里的取消源由检查 finally 释放。
                _updateCancellation = null;
            }
            else
            {
                _updateCancellation?.Dispose();
                _updateCancellation = null;
            }

            OneClickUpdateButton.IsEnabled = _availableUpdate is not null;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async Task CheckForUpdateAsync()
    {
        if (_isCheckingUpdate)
        {
            return;
        }

        _isCheckingUpdate = true;
        _updateCancellation?.Dispose();
        _updateCancellation = new CancellationTokenSource();
        CheckUpdateButton.IsEnabled = false;
        OneClickUpdateButton.Visibility = Visibility.Collapsed;
        OneClickUpdateButton.IsEnabled = false;
        _availableUpdate = null;
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "正在检查更新…";

        try
        {
            var latest = await _updateService.GetLatestUpdateAsync(_updateCancellation.Token);
            var current = typeof(SettingsWindow).Assembly.GetName().Version;
            if (current is not null && System.Version.TryParse(latest.Version, out var latestVersion) && latestVersion <= current)
            {
                UpdateStatusText.Text = $"已是最新版本（v{current.ToString(3)}）。";
                return;
            }

            _availableUpdate = latest;
            OneClickUpdateButton.Visibility = Visibility.Visible;
            OneClickUpdateButton.IsEnabled = true;
            UpdateStatusText.Text = $"发现新版本 v{latest.Version}，可一键更新。";
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText.Text = "检查更新已取消。";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            _isCheckingUpdate = false;
            CheckUpdateButton.IsEnabled = true;
            if (_availableUpdate is null)
            {
                _updateCancellation?.Dispose();
                _updateCancellation = null;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _updateCancellation?.Cancel();
        base.OnClosed(e);
    }
}
