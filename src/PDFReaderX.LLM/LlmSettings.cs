namespace PDFReaderX.LLM;

/// <summary>LLM 服务配置（OpenAI 兼容格式）。</summary>
public sealed class LlmSettings
{
    /// <summary>API 地址，例如阿里云百炼兼容端点。</summary>
    public string Endpoint { get; set; } = "https://dashscope.aliyuncs.com/compatible-mode/v1";

    /// <summary>API Key。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>文本模型名称。</summary>
    public string Model { get; set; } = "deepseek-v4-flash-0731";

    /// <summary>视觉模型名称（扫描版 PDF 看图生成书签时使用）。</summary>
    public string VisionModel { get; set; } = "qwen3.7-flash-2026-07-15";

    /// <summary>使用视觉模式前不再询问确认。</summary>
    public bool SkipVisionConfirm { get; set; }
}
