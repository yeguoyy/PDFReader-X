using System.IO;
using System.Text.Json;

namespace PDFReaderX.LLM;

/// <summary>
/// LLM 设置的本地持久化。
/// 默认路径：%AppData%\PDFReaderX\llm-settings.json（API Key 不进入仓库）。
/// </summary>
public static class LlmSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PDFReaderX", "llm-settings.json");

    public static LlmSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<LlmSettings>(File.ReadAllText(path)) ?? new LlmSettings();
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }
        return new LlmSettings();
    }

    public static void Save(LlmSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
