namespace PDFReaderX.LLM;

/// <summary>
/// LLM 返回内容在输出预算内未生成完（finish_reason = length）。
/// 调用方可捕获后拆分输入重试。
/// </summary>
public sealed class LlmOutputTruncatedException : Exception
{
    public LlmOutputTruncatedException()
        : base("AI 返回内容过长被截断")
    {
    }
}

