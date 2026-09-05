using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Text.Json;

namespace PDFReaderX.App.Services;

public sealed record GitHubReleaseUpdate(string Version, string InstallerUrl, long Size);

/// <summary>
/// GitHub Releases 更新服务：读取最新 Release，下载 x64 安装包并启动静默更新。
/// </summary>
public sealed class GitHubUpdateService
{
    public const string RepositoryUrl = "https://github.com/yeguoyy/PDFReader-X";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/yeguoyy/PDFReader-X/releases/latest";

    private static readonly HttpClient HttpClient = CreateHttpClient();
    /// <summary>读取最新正式版；返回用于更新的 x64 安装包。</summary>
    public async Task<GitHubReleaseUpdate> GetLatestUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var response = await HttpClient.GetAsync(
            LatestReleaseApiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V') ?? "";
        if (!System.Version.TryParse(tag, out _))
        {
            throw new InvalidDataException($"GitHub Release 版本号无效：{tag}");
        }

        var installer = root.GetProperty("assets").EnumerateArray().FirstOrDefault(asset =>
        {
            var name = asset.TryGetProperty("name", out var value) ? value.GetString() ?? "" : "";
            return name.StartsWith("PDFReaderX-Setup-", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        });

        var downloadUrl = installer.ValueKind == JsonValueKind.Undefined
            ? ""
            : installer.GetProperty("browser_download_url").GetString() ?? "";
        var size = installer.ValueKind == JsonValueKind.Undefined
            ? 0
            : installer.TryGetProperty("size", out var sizeProperty) ? sizeProperty.GetInt64() : 0;

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new InvalidDataException("最新 Release 中没有找到安装包（PDFReaderX-Setup-*.exe）。");
        }

        return new GitHubReleaseUpdate(tag, downloadUrl, size);
    }
    /// <summary>把安装包下载到本地临时目录；progress 百分比范围 0~100。</summary>
    public async Task<string> DownloadInstallerAsync(
        GitHubReleaseUpdate update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PDFReaderX", "update");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, $"PDFReaderX-Setup-{update.Version}.exe");

        using var response = await HttpClient.GetAsync(
            update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[1024 * 1024];
        long totalBytes = 0;
        long expectedBytes = update.Size > 0
            ? update.Size
            : response.Content.Headers.ContentLength ?? 0;

        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalBytes += read;
            if (expectedBytes > 0)
            {
                progress?.Report(totalBytes * 100.0 / expectedBytes);
            }
        }

        if (expectedBytes > 0 && totalBytes != expectedBytes)
        {
            throw new InvalidDataException($"安装包下载不完整：已下载 {totalBytes} 字节，应为 {expectedBytes} 字节。");
        }

        progress?.Report(100);
        return filePath;
    }
    /// <summary>启动 Inno Setup 安装包；安装器会关闭当前应用并在完成后尝试重新启动。</summary>
    public static void LaunchInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
            UseShellExecute = true,
        });
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PDFReaderX-App");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
