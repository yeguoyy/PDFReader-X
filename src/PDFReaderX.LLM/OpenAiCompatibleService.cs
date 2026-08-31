using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PDFReaderX.LLM;

/// <summary>
/// OpenAI 兼容格式的 LLM 服务：
/// 根据 PDF 逐页文本调用大模型，生成结构化书签目录。
/// </summary>
public sealed class OpenAiCompatibleService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly LlmSettings _settings;
    private readonly HttpClient _http;

    public OpenAiCompatibleService(LlmSettings settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", settings.ApiKey);
    }

    /// <summary>
    /// 根据逐页文本生成书签树。
    /// 每页文本已由调用方预处理（截断长度），传入顺序即页码顺序。
    /// </summary>
    public async Task<List<LlmBookmark>> GenerateBookmarksAsync(
        IReadOnlyList<string> pageTexts,
        int firstPageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        if (pageTexts.Count == 0)
        {
            return new List<LlmBookmark>();
        }

        var payload = new
        {
            model = _settings.Model,
            temperature = 0.2,
            enable_thinking = false,
            max_tokens = 8192,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = BuildPrompt(pageTexts, firstPageNumber) },
            },
        };

        var url = _settings.Endpoint.TrimEnd('/') + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"LLM 接口返回 {(int)response.StatusCode}：{Truncate(body, 300)}");
        }

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("LLM 返回中没有 choices 字段");
        }

        var contentText = choices[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        if (string.IsNullOrWhiteSpace(contentText))
        {
            throw new InvalidOperationException("LLM 返回内容为空");
        }
        if (choices[0].TryGetProperty("finish_reason", out var finishReason)
            && finishReason.GetString() == "length")
        {
            throw new LlmOutputTruncatedException();
        }
        try
        {
            return ParseBookmarks(contentText);
        }
        catch (JsonException)
        {
            throw new LlmOutputTruncatedException();
        }
    }

    /// <summary>
    /// 轻量目录探测（文本）：只判断当前批次页面是否包含目录页，不生成书签，
    /// 用于逐批往后找目录，降低 token 消耗。
    /// </summary>
    public async Task<TocResult> ProbeTocAsync(
        IReadOnlyList<string> pageTexts,
        int firstPageNumber,
        bool isLastBatch,
        CancellationToken cancellationToken = default)
    {
        if (pageTexts.Count == 0)
        {
            return new TocResult { HasToc = false };
        }

        var userContent = BuildTocProbePrompt(firstPageNumber, pageTexts.Count)
            + (isLastBatch ? "\n注意：以上已经是 PDF 的最后几页。" : "");
        var payload = new
        {
            model = _settings.Model,
            temperature = 0.2,
            enable_thinking = false,
            max_tokens = 256,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = TocProbePrompt },
                new { role = "user", content = userContent },
            },
        };

        var url = _settings.Endpoint.TrimEnd('/') + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"LLM 接口返回 {(int)response.StatusCode}：{Truncate(body, 300)}");
        }

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("LLM 返回中没有 choices 字段");
        }
        var contentText = choices[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        if (string.IsNullOrWhiteSpace(contentText))
        {
            throw new InvalidOperationException("LLM 返回内容为空");
        }
        return ParseTocProbe(contentText);
    }

    /// <summary>
    /// 轻量目录探测（视觉）：只判断当前批次页面是否包含目录页，不生成书签，
    /// 用于逐批往后找目录，降低 token 消耗。
    /// </summary>
    public async Task<TocResult> ProbeVisionTocAsync(
        IReadOnlyList<byte[]> pageImages,
        int firstPageNumber,
        bool isLastBatch,
        CancellationToken cancellationToken = default)
    {
        if (pageImages.Count == 0)
        {
            return new TocResult { HasToc = false };
        }

        var contentItems = new List<object>(pageImages.Count + 1);
        foreach (var image in pageImages)
        {
            contentItems.Add(new
            {
                type = "image_url",
                image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(image) },
            });
        }
        var pageLabel = pageImages.Count > 1
            ? $"{firstPageNumber}~{firstPageNumber + pageImages.Count - 1}"
            : $"{firstPageNumber}";
        contentItems.Add(new
        {
            type = "text",
            text = $"这批图片对应 PDF 第 {pageLabel} 页（可能位于书的中间部分，不一定有目录）。"
                + (isLastBatch ? "\n注意：以上已经是 PDF 的最后几页。" : ""),
        });

        var payload = new
        {
            model = _settings.VisionModel,
            enable_thinking = false,
            max_tokens = 256,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = VisionTocProbePrompt },
                new { role = "user", content = contentItems },
            },
        };

        var url = _settings.Endpoint.TrimEnd('/') + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"LLM 接口返回 {(int)response.StatusCode}：{Truncate(body, 300)}");
        }

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("LLM 返回中没有 choices 字段");
        }
        var contentText = choices[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        if (string.IsNullOrWhiteSpace(contentText))
        {
            throw new InvalidOperationException("LLM 返回内容为空");
        }
        return ParseTocProbe(contentText);
    }

    /// <summary>
    /// 目录识别模式：读取 PDF 前若干页文本，判断是否有目录并提取目录书签树（书本页码）。
    /// 调用方分批传入文本，用 isLastBatch 告知是否已读到 PDF 末尾。
    /// 目录输出过长被截断时，自动携带已保存的条目续传剩余部分（分批传回）。
    /// 无目录时返回 HasToc = false；目录未读完时 TocComplete = false。
    /// </summary>
    public async Task<TocResult> GenerateTocAsync(
        IReadOnlyList<string> frontPageTexts,
        bool isLastBatch,
        int firstPageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        if (frontPageTexts.Count == 0)
        {
            return new TocResult { HasToc = false };
        }

        var savedBookmarks = new List<LlmBookmark>();
        const int maxRounds = 5;
        for (var round = 0; round < maxRounds; round++)
        {
            var (contentText, truncated) = await PostTocRequestAsync(
                frontPageTexts, isLastBatch, firstPageNumber, savedBookmarks, cancellationToken).ConfigureAwait(false);

            if (truncated)
            {
                if (!TrySalvageBookmarks(contentText, out var partial))
                {
                    throw new LlmOutputTruncatedException();
                }
                savedBookmarks = partial;
                continue;
            }

            List<LlmBookmark> parsedBookmarks;
            var tocComplete = true;
            try
            {
                if (savedBookmarks.Count > 0)
                {
                    parsedBookmarks = ParseBookmarks(contentText);
                }
                else
                {
                    var toc = ParseToc(contentText);
                    if (!toc.HasToc)
                    {
                        return toc;
                    }
                    parsedBookmarks = toc.Bookmarks;
                    tocComplete = toc.TocComplete;
                }
            }
            catch
            {
                throw new LlmOutputTruncatedException(); // 输出不可用，调用方回退全量读取
            }

            if (savedBookmarks.Count > 0)
            {
                var merged = new List<LlmBookmark>(savedBookmarks.Count + parsedBookmarks.Count);
                merged.AddRange(savedBookmarks);
                merged.AddRange(parsedBookmarks);
                parsedBookmarks = merged;
                tocComplete = true;
            }
            if (isLastBatch)
            {
                // 已到 PDF 末尾，目录不可能再延伸
                return new TocResult { HasToc = true, TocComplete = true, Bookmarks = parsedBookmarks };
            }
            return new TocResult { HasToc = true, TocComplete = tocComplete, Bookmarks = parsedBookmarks };
        }
        throw new LlmOutputTruncatedException();
    }

    /// <summary>发送一次目录识别/续传请求，返回原始输出与是否截断。</summary>
    private async Task<(string ContentText, bool Truncated)> PostTocRequestAsync(
        IReadOnlyList<string> frontPageTexts,
        bool isLastBatch,
        int firstPageNumber,
        IReadOnlyList<LlmBookmark> savedBookmarks,
        CancellationToken cancellationToken)
    {
        // 续传时不再重发整批文本，只携带已保存条目，减少 token 与耗时
        var userContent = savedBookmarks.Count == 0
            ? BuildPrompt(frontPageTexts, firstPageNumber) + (isLastBatch ? "\n注意：以上已经是 PDF 的最后几页。" : "")
            : BuildContinuationPrompt(savedBookmarks);
        var payload = new
        {
            model = _settings.Model,
            temperature = 0.2,
            enable_thinking = false,
            max_tokens = 8192,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = TocPrompt },
                new { role = "user", content = userContent },
            },
        };

        var url = _settings.Endpoint.TrimEnd('/') + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"LLM 接口返回 {(int)response.StatusCode}：{Truncate(body, 300)}");
        }

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("LLM 返回中没有 choices 字段");
        }
        var contentText = choices[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        if (string.IsNullOrWhiteSpace(contentText))
        {
            throw new InvalidOperationException("LLM 返回内容为空");
        }
        var truncated = choices[0].TryGetProperty("finish_reason", out var finishReason)
            && finishReason.GetString() == "length";
        return (contentText, truncated);
    }

    /// <summary>构造目录续传提示：携带已保存条目，让模型只输出剩余部分。</summary>
    private static string BuildContinuationPrompt(IReadOnlyList<LlmBookmark> savedBookmarks)
    {
        var sb = new StringBuilder();
        sb.Append("你上一轮输出的目录条目因长度限制被截断。以下是已经成功保存的条目（按顺序）：\n");
        var index = 1;
        AppendSavedEntries(sb, savedBookmarks, ref index);
        sb.Append("请只输出剩余尚未输出的目录条目（从第一个未保存的条目继续），保持相同层级结构，");
        sb.Append("页码仍为目录标注的书本页码。不要重复已保存的条目，只输出 JSON 数组，不要输出任何其他文字：\n");
        sb.Append("[{\"title\":\"大标题\",\"page\":1,\"children\":[{\"title\":\"子标题\",\"page\":2,\"children\":[]}]}]");
        return sb.ToString();
    }

    private static void AppendSavedEntries(StringBuilder sb, IReadOnlyList<LlmBookmark> nodes, ref int index)
    {
        foreach (var node in nodes)
        {
            sb.Append(index++).Append(". ").Append(node.Title)
                .Append("（书本第 ").Append(node.PageIndex + 1).Append(" 页）\n");
            if (node.Children.Count > 0)
            {
                AppendSavedEntries(sb, node.Children, ref index);
            }
        }
    }

    /// <summary>解析目录识别结果 JSON。</summary>
    public static TocResult ParseToc(string json)
    {
        var document = ExtractJson(json);
        using var root = JsonDocument.Parse(document);
        // 模型可能直接输出顶层书签数组（未按对象格式包裹），按有目录处理
        if (root.RootElement.ValueKind == JsonValueKind.Array)
        {
            var arrayNodes = ParseNodes(root.RootElement);
            NormalizePageNumbers(arrayNodes);
            RepairTopLevelPages(arrayNodes);
            return new TocResult { HasToc = true, TocComplete = true, Bookmarks = arrayNodes };
        }
        if (root.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new TocResult { HasToc = false };
        }
        var hasToc = root.RootElement.TryGetProperty("hasToc", out var hasTocElement)
            && hasTocElement.ValueKind == JsonValueKind.True;
        if (!hasToc)
        {
            return new TocResult { HasToc = false };
        }
        if (!root.RootElement.TryGetProperty("bookmarks", out var bookmarks)
            || bookmarks.ValueKind != JsonValueKind.Array)
        {
            return new TocResult { HasToc = false };
        }
        var tocComplete = root.RootElement.TryGetProperty("tocComplete", out var tocCompleteElement)
            && tocCompleteElement.ValueKind == JsonValueKind.True;
        var nodes = ParseNodes(bookmarks);
        NormalizePageNumbers(nodes);
        RepairTopLevelPages(nodes);
        return new TocResult { HasToc = true, TocComplete = tocComplete, Bookmarks = nodes };
    }

    /// <summary>解析轻量探测结果：只关心是否包含目录页，书签字段可省略。</summary>
    private static TocResult ParseTocProbe(string text)
    {
        try
        {
            using var root = JsonDocument.Parse(ExtractJson(text));
            if (root.RootElement.TryGetProperty("hasToc", out var hasToc)
                && hasToc.ValueKind == JsonValueKind.True)
            {
                return new TocResult { HasToc = true };
            }
            return new TocResult { HasToc = false };
        }
        catch
        {
            // 模型未按要求输出 JSON 时，尝试从文本中提取 hasToc 字段
            var match = System.Text.RegularExpressions.Regex.Match(
                text, "\"hasToc\"\\s*:\\s*(true|false)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && match.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return new TocResult { HasToc = true };
            }
            return new TocResult { HasToc = false };
        }
    }

    /// <summary>
    /// 视觉模式：把页面图片发给视觉模型，识别标题/目录生成书签。
    /// 用于扫描版 PDF（无法提取文本）。
    /// </summary>
    public async Task<List<LlmBookmark>> GenerateBookmarksFromImagesAsync(
        IReadOnlyList<byte[]> pageImages,
        string visionModel,
        int firstPageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        if (pageImages.Count == 0)
        {
            return new List<LlmBookmark>();
        }

        var contentItems = new List<object>(pageImages.Count + 1);
        foreach (var image in pageImages)
        {
            contentItems.Add(new
            {
                type = "image_url",
                image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(image) },
            });
        }
        contentItems.Add(new { type = "text", text = BuildVisionPrompt(firstPageNumber, pageImages.Count) });

        var payload = new
        {
            model = visionModel,
            enable_thinking = false,
            max_tokens = 8192,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = contentItems },
            },
        };

        var url = _settings.Endpoint.TrimEnd('/') + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"LLM 接口返回 {(int)response.StatusCode}：{Truncate(body, 300)}");
        }

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("LLM 返回中没有 choices 字段");
        }
        var contentText = choices[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        if (string.IsNullOrWhiteSpace(contentText))
        {
            throw new InvalidOperationException("LLM 返回内容为空");
        }
        if (choices[0].TryGetProperty("finish_reason", out var finishReason)
            && finishReason.GetString() == "length")
        {
            throw new LlmOutputTruncatedException();
        }
        try
        {
            return ParseBookmarks(contentText);
        }
        catch (JsonException)
        {
            throw new LlmOutputTruncatedException();
        }
    }

    /// <summary>
    /// 视觉目录识别模式：渲染前若干页图片，判断是否有目录并提取目录书签树（书本页码）。
    /// 调用方分批传入图片，用 isLastBatch 告知是否已读到 PDF 末尾。
    /// 目录输出过长被截断时自动携带已保存条目续传；无目录时返回 HasToc = false。
    /// </summary>
    public async Task<TocResult> GenerateVisionTocAsync(
        IReadOnlyList<byte[]> pageImages,
        bool isLastBatch,
        int firstPageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        if (pageImages.Count == 0)
        {
            return new TocResult { HasToc = false };
        }

        var savedBookmarks = new List<LlmBookmark>();
        const int maxRounds = 5;
        for (var round = 0; round < maxRounds; round++)
        {
            var (contentText, truncated) = await PostVisionTocRequestAsync(
                pageImages, isLastBatch, firstPageNumber, savedBookmarks, cancellationToken).ConfigureAwait(false);

            if (truncated)
            {
                if (!TrySalvageBookmarks(contentText, out var partial))
                {
                    throw new LlmOutputTruncatedException();
                }
                savedBookmarks = partial;
                continue;
            }

            List<LlmBookmark> parsedBookmarks;
            var tocComplete = true;
            try
            {
                if (savedBookmarks.Count > 0)
                {
                    parsedBookmarks = ParseBookmarks(contentText);
                }
                else
                {
                    var toc = ParseToc(contentText);
                    if (!toc.HasToc)
                    {
                        return toc;
                    }
                    parsedBookmarks = toc.Bookmarks;
                    tocComplete = toc.TocComplete;
                }
            }
            catch
            {
                throw new LlmOutputTruncatedException();
            }

            if (savedBookmarks.Count > 0)
            {
                var merged = new List<LlmBookmark>(savedBookmarks.Count + parsedBookmarks.Count);
                merged.AddRange(savedBookmarks);
                merged.AddRange(parsedBookmarks);
                parsedBookmarks = merged;
                tocComplete = true;
            }
            if (isLastBatch)
            {
                return new TocResult { HasToc = true, TocComplete = true, Bookmarks = parsedBookmarks };
            }
            return new TocResult { HasToc = true, TocComplete = tocComplete, Bookmarks = parsedBookmarks };
        }
        throw new LlmOutputTruncatedException();
    }

    /// <summary>发送一次视觉目录识别/续传请求，返回原始输出与是否截断。</summary>
    private async Task<(string ContentText, bool Truncated)> PostVisionTocRequestAsync(
        IReadOnlyList<byte[]> pageImages,
        bool isLastBatch,
        int firstPageNumber,
        IReadOnlyList<LlmBookmark> savedBookmarks,
        CancellationToken cancellationToken)
    {
        // 续传时不再重发图片，只携带已保存条目，避免每轮等待重传 10 张图
        var contentItems = new List<object>();
        if (savedBookmarks.Count > 0)
        {
            contentItems.Add(new { type = "text", text = BuildContinuationPrompt(savedBookmarks) });
        }
        else
        {
            foreach (var image in pageImages)
            {
                contentItems.Add(new
                {
                    type = "image_url",
                    image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(image) },
                });
            }
            contentItems.Add(new
            {
                type = "text",
                text = BuildVisionTocPrompt(pageImages.Count, firstPageNumber) + (isLastBatch ? "\n注意：以上已经是 PDF 的最后几页。" : ""),
            });
        }

        var payload = new
        {
            model = _settings.VisionModel,
            enable_thinking = false,
            max_tokens = 8192,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = TocPrompt },
                new { role = "user", content = contentItems },
            },
        };

        var url = _settings.Endpoint.TrimEnd('/') + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"LLM 接口返回 {(int)response.StatusCode}：{Truncate(body, 300)}");
        }

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("LLM 返回中没有 choices 字段");
        }
        var contentText = choices[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        if (string.IsNullOrWhiteSpace(contentText))
        {
            throw new InvalidOperationException("LLM 返回内容为空");
        }
        var truncated = choices[0].TryGetProperty("finish_reason", out var finishReason)
            && finishReason.GetString() == "length";
        return (contentText, truncated);
    }

    /// <summary>
    /// 视觉页码定位：渲染目录之后的若干页图片，让视觉模型指出目录中前几个章节标题
    /// 出现在第几张图片（图片序号），换算成整个 PDF 的绝对页码返回。
    /// </summary>
    public async Task<List<LocatedPage>> LocateChapterPagesAsync(
        IReadOnlyList<LlmBookmark> topLevelBookmarks,
        IReadOnlyList<byte[]> pageImages,
        int firstPageNumber,
        CancellationToken cancellationToken = default)
    {
        var result = new List<LocatedPage>();
        if (topLevelBookmarks.Count == 0 || pageImages.Count == 0)
        {
            return result;
        }

        var chapters = topLevelBookmarks.Take(6).ToList();
        var contentItems = new List<object>(pageImages.Count + 1);
        foreach (var image in pageImages)
        {
            contentItems.Add(new
            {
                type = "image_url",
                image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(image) },
            });
        }
        contentItems.Add(new { type = "text", text = BuildChapterLocatePrompt(chapters, firstPageNumber, pageImages.Count) });

        var payload = new
        {
            model = _settings.VisionModel,
            enable_thinking = false,
            max_tokens = 2048,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = "你是 PDF 章节页码定位助手，只输出 JSON，不要输出任何其他文字。" },
                new { role = "user", content = contentItems },
            },
        };

        var url = _settings.Endpoint.TrimEnd('/') + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"LLM 接口返回 {(int)response.StatusCode}：{Truncate(body, 300)}");
        }

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("LLM 返回中没有 choices 字段");
        }
        var contentText = choices[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        if (string.IsNullOrWhiteSpace(contentText))
        {
            throw new InvalidOperationException("LLM 返回内容为空");
        }

        var document = ExtractJson(contentText);
        using var root = JsonDocument.Parse(document);
        if (root.RootElement.ValueKind != JsonValueKind.Object
            || !root.RootElement.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Array)
        {
            return result;
        }
        foreach (var item in pages.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var title = item.TryGetProperty("title", out var titleElement) ? titleElement.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }
            if (item.TryGetProperty("image", out var imageElement)
                && imageElement.TryGetInt32(out var image)
                && image > 0)
            {
                result.Add(new LocatedPage(title, firstPageNumber + image - 1));
            }
            else if (item.TryGetProperty("page", out var pageElement)
                && pageElement.TryGetInt32(out var page)
                && page > 0)
            {
                result.Add(new LocatedPage(title, page));
            }
        }
        return result;
    }

    /// <summary>
    /// 识别每张页面图片页眉/页脚印刷的书本页码（阿拉伯数字），
    /// 返回 (PDF 页, 书本页码)，用于精确确认正文页码偏移。
    /// </summary>
    public async Task<List<(int PdfPage, int BookPage)>> LocatePageNumbersAsync(
        IReadOnlyList<byte[]> pageImages,
        int firstPageNumber,
        CancellationToken cancellationToken = default)
    {
        var result = new List<(int PdfPage, int BookPage)>();
        if (pageImages.Count == 0)
        {
            return result;
        }

        var contentItems = new List<object>(pageImages.Count + 1);
        foreach (var image in pageImages)
        {
            contentItems.Add(new
            {
                type = "image_url",
                image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(image) },
            });
        }
        contentItems.Add(new { type = "text", text = BuildPageNumberPrompt(firstPageNumber, pageImages.Count) });

        var payload = new
        {
            model = _settings.VisionModel,
            enable_thinking = false,
            max_tokens = 2048,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = "你是 PDF 页码识别助手，只输出 JSON，不要输出任何其他文字。" },
                new { role = "user", content = contentItems },
            },
        };

        var url = _settings.Endpoint.TrimEnd('/') + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"LLM 接口返回 {(int)response.StatusCode}：{Truncate(body, 300)}");
        }

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("LLM 返回中没有 choices 字段");
        }
        var contentText = choices[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        if (string.IsNullOrWhiteSpace(contentText))
        {
            throw new InvalidOperationException("LLM 返回内容为空");
        }

        var document = ExtractJson(contentText);
        using var root = JsonDocument.Parse(document);
        if (root.RootElement.ValueKind != JsonValueKind.Object
            || !root.RootElement.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Array)
        {
            return result;
        }
        foreach (var item in pages.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (item.TryGetProperty("image", out var imageElement)
                && imageElement.TryGetInt32(out var image)
                && image > 0
                && item.TryGetProperty("number", out var numberElement)
                && numberElement.TryGetInt32(out var number)
                && number > 0)
            {
                result.Add((firstPageNumber + image - 1, number));
            }
        }
        return result;
    }

    /// <summary>构造页眉/页脚页码识别提示。</summary>
    private static string BuildPageNumberPrompt(int firstPageNumber, int pageCount)
    {
        return
            "用户提供了 PDF 中连续若干页的页面图片（按顺序，共 " + pageCount + " 张，第 1 张 = PDF 第 " + firstPageNumber + " 页）。\n" +
            "请识别每页顶部页眉或底部页脚印刷的书本页码（通常位于页面外侧角落或居中，字号较小、单独出现）。\n" +
            "规则：\n" +
            "1. 只输出阿拉伯数字页码；罗马数字（如 i、xii）或该页没有页码时输出 -1。\n" +
            "2. 页眉里的章节名、书名，页脚装饰、网址等都不算页码。\n" +
            "3. 每张图片输出一个结果，按图片序号对应。\n" +
            "只输出 JSON，不要输出任何其他文字：\n" +
            "{\"pages\":[{\"image\":1,\"number\":2},{\"image\":2,\"number\":3},{\"image\":3,\"number\":-1}]}";
    }

    /// <summary>构造视觉目录识别提示（含 tocComplete 判断）。</summary>
    private static string BuildVisionTocPrompt(int pageCount, int firstPageNumber)
    {
        var pageLabel = pageCount > 1
            ? $"{firstPageNumber}~{firstPageNumber + pageCount - 1}"
            : $"{firstPageNumber}";
        return
            "你是 PDF 目录识别助手。用户提供 PDF 中连续若干页的页面图片（按顺序，共 " + pageCount + " 张，对应 PDF 第 " + pageLabel + " 页）。\n" +
            "1. 判断这些页里是否包含目录页（出现\"目录\"、\"目 录\"、\"Contents\"、\"Table of Contents\"等字样）。\n" +
            "2. 有目录：提取目前能看到的所有目录条目，保留层级（子条目放 children）。页码为目录中标注的书本页码：阿拉伯数字直接输出为数字（如 1、23）；罗马数字（如 i、xii）必须输出为字符串（如 \"page\":\"xii\"），不要换算成阿拉伯数字。目录中没有标注页码的条目（如无点线引导的条目标题）不要输出。同时判断目录是否已经完整读完：\n" +
            "   - 如果最后一页底部仍在继续列出目录条目、明显还有后续目录页，tocComplete=false；\n" +
            "   - 如果最后一页的目录已经结束（之后是正文、空白，或用户提示已经是最后几页），tocComplete=true。\n" +
            "3. 没有目录：输出 {\"hasToc\":false}\n" +
            "4. 只输出 JSON，不要输出任何其他文字。\n" +
            "JSON 格式：\n" +
            "{\"hasToc\":true,\"tocComplete\":true,\"bookmarks\":[{\"title\":\"大标题\",\"page\":1,\"children\":[{\"title\":\"子标题\",\"page\":2,\"children\":[]}]}]}";
    }

    /// <summary>
    /// 构造章节定位提示：只给标题，让模型报告标题出现在第几张图片（1 基图片序号），
    /// 由调用方换算绝对页码，避免模型把书本页码当绝对页码输出。
    /// </summary>
    private static string BuildChapterLocatePrompt(
        IReadOnlyList<LlmBookmark> chapters,
        int firstPageNumber,
        int pageCount)
    {
        var sb = new StringBuilder();
        sb.Append("用户提供了 PDF 的页面图片（按顺序，共 ").Append(pageCount).Append(" 张）。\n");
        sb.Append("这些图片按顺序编号：第 1 张 = PDF 第 ").Append(firstPageNumber)
            .Append(" 页，第 ").Append(pageCount).Append(" 张 = PDF 第 ")
            .Append(firstPageNumber + pageCount - 1).Append(" 页。\n");
        sb.Append("请在图片中查找以下章节标题（出现在章节起始页，特征：加粗、字号明显大于正文、独占一行、位置靠上，区别于正文小标题和页眉章节名）：\n");
        var index = 1;
        foreach (var chapter in chapters)
        {
            sb.Append(index++).Append(". ").Append(chapter.Title).Append('\n');
        }
        sb.Append("对每个标题，输出它出现的图片序号（从 1 开始编号）。\n");
        sb.Append("注意：本批图片中如果出现目录页，目录里的同名标题不算；没有找到的标题 image 输出 -1。\n");
        sb.Append("只输出 JSON，不要输出任何其他文字：\n");
        sb.Append("{\"pages\":[{\"title\":\"第1章\",\"image\":4},{\"title\":\"第2章\",\"image\":-1}]}");
        return sb.ToString();
    }

    /// <summary>
    /// 从截断的 JSON 输出中抢救已完整输出的书签条目（本地暂存），供续传请求携带。
    /// 用深度扫描定位最后一个完整的顶层条目（截断点可能落在嵌套子元素中间），
    /// 兼容 {"hasToc":...,"bookmarks":[...]} 与续传时的裸数组两种形式。
    /// </summary>
    public static bool TrySalvageBookmarks(string text, out List<LlmBookmark> bookmarks)
    {
        bookmarks = new List<LlmBookmark>();
        var keyIndex = text.IndexOf("\"bookmarks\"", StringComparison.OrdinalIgnoreCase);
        var arrayStart = keyIndex >= 0 ? text.IndexOf('[', keyIndex) : text.IndexOf('[');
        if (arrayStart < 0)
        {
            return false;
        }

        var depth = 0;
        var lastTopLevelEnd = -1;
        var inString = false;
        var escaped = false;
        for (var i = arrayStart; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }
            if (c == '"')
            {
                inString = true;
            }
            else if (c is '{' or '[')
            {
                depth++;
            }
            else if (c is '}' or ']')
            {
                depth--;
                if (depth == 1 && (c == '}' || c == ']'))
                {
                    lastTopLevelEnd = i; // 顶层元素闭合位置
                }
            }
        }
        if (lastTopLevelEnd <= arrayStart)
        {
            return false;
        }

        var slice = text[arrayStart..(lastTopLevelEnd + 1)];
        var candidate = keyIndex >= 0 && keyIndex < arrayStart
            ? "{\"bookmarks\":" + slice + "]}"
            : slice + "]";
        try
        {
            bookmarks = ParseBookmarks(candidate);
        }
        catch
        {
            return false;
        }
        return bookmarks.Count > 0;
    }

    /// <summary>
    /// 解析 LLM 返回的 JSON 书签树。
    /// 兼容 ```json 代码块包裹、顶层数组、bookmarks 字段三种形式；页码按 1 基解析。
    /// </summary>
    public static List<LlmBookmark> ParseBookmarks(string json)
    {
        var document = ExtractJson(json);
        using var root = JsonDocument.Parse(document);
        JsonElement array;
        if (root.RootElement.ValueKind == JsonValueKind.Array)
        {
            array = root.RootElement;
        }
        else if (root.RootElement.ValueKind == JsonValueKind.Object
            && root.RootElement.TryGetProperty("bookmarks", out var bookmarks))
        {
            array = bookmarks;
        }
        else
        {
            array = root.RootElement;
        }
        var nodes = ParseNodes(array);
        NormalizePageNumbers(nodes);
        return nodes;
    }

    /// <summary>
    /// 修复顶层书签页码单调性：模型偶发把第一章页码重置为 1（整棵子树一起偏移），
    /// 按"不递减"原则把异常条目及其子树平移到上一个条目之后。
    /// </summary>
    private static void RepairTopLevelPages(List<LlmBookmark> nodes)
    {
        var prev = -1;
        foreach (var node in nodes)
        {
            if (node.PageIndex <= prev)
            {
                BookmarkLocator.ShiftSubtree(node, prev + 1 - node.PageIndex);
            }
            prev = node.PageIndex;
        }
    }

    /// <summary>
    /// 页码基数归一化：模型有时返回 1 基页码（第 1 页 = 1），有时返回 0 基（第 1 页 = 0）。
    /// 统一转换为 0 基索引：全部页码最小值为 0 时视为 0 基保持不动，否则按 1 基减 1。
    /// </summary>
    private static void NormalizePageNumbers(List<LlmBookmark> nodes)
    {
        var min = int.MaxValue;
        CollectMin(nodes, ref min);
        if (nodes.Count == 0 || min == int.MaxValue || min == 0)
        {
            return;
        }
        ConvertToZeroBased(nodes);
    }

    private static void CollectMin(List<LlmBookmark> nodes, ref int min)
    {
        foreach (var node in nodes)
        {
            min = Math.Min(min, node.PageIndex);
            CollectMin(node.Children, ref min);
        }
    }

    private static void ConvertToZeroBased(List<LlmBookmark> nodes)
    {
        foreach (var node in nodes)
        {
            node.PageIndex = Math.Max(0, node.PageIndex - 1);
            ConvertToZeroBased(node.Children);
        }
    }

    private static List<LlmBookmark> ParseNodes(JsonElement array)
    {
        var result = new List<LlmBookmark>();
        if (array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue; // 非对象条目（如字符串、数字）跳过
            }
            var title = item.TryGetProperty("title", out var titleElement)
                ? titleElement.GetString()?.Trim()
                : null;
            if (string.IsNullOrEmpty(title))
            {
                continue; // 跳过无标题项
            }

            var page = 0;
            if (item.TryGetProperty("page", out var pageElement))
            {
                if (pageElement.ValueKind == JsonValueKind.Number
                    && pageElement.TryGetInt32(out var rawPage))
                {
                    page = rawPage;
                }
                else if (pageElement.ValueKind == JsonValueKind.String)
                {
                    var rawPageText = pageElement.GetString()?.Trim();
                    if (int.TryParse(rawPageText, out var parsedPage))
                    {
                        page = parsedPage;
                    }
                    else
                    {
                        page = RomanToInt(rawPageText);
                    }
                }
            }
            if (page <= 0)
            {
                continue; // 目录中未标明页码的条目不写入书签
            }

            var node = new LlmBookmark(title, page);
            if (item.TryGetProperty("children", out var children)
                && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in ParseNodes(children))
                {
                    node.Children.Add(child);
                }
            }
            result.Add(node);
        }
        return result;
    }

    /// <summary>把罗马数字（i、xii 等）转为阿拉伯数字，无法解析时返回 0。</summary>
    private static int RomanToInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }
        var roman = text.Trim().ToUpperInvariant();
        var values = new Dictionary<char, int>
        {
            ['I'] = 1,
            ['V'] = 5,
            ['X'] = 10,
            ['L'] = 50,
            ['C'] = 100,
            ['D'] = 500,
            ['M'] = 1000,
        };
        var total = 0;
        var prev = 0;
        for (var i = roman.Length - 1; i >= 0; i--)
        {
            if (!values.TryGetValue(roman[i], out var value))
            {
                return 0;
            }
            if (value < prev)
            {
                total -= value;
            }
            else
            {
                total += value;
                prev = value;
            }
        }
        return total;
    }

    /// <summary>提取文本中最外层 JSON 对象（容忍 LLM 输出前后解释文字或代码块围栏）。</summary>
    private static string ExtractJson(string text)
    {
        var startBrace = text.IndexOf('{');
        var startBracket = text.IndexOf('[');
        var start = startBrace < 0
            ? startBracket
            : startBracket < 0 ? startBrace : Math.Min(startBrace, startBracket);
        var end = Math.Max(text.LastIndexOf('}'), text.LastIndexOf(']'));
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("LLM 输出中未找到 JSON");
        }
        return text[start..(end + 1)];
    }

    private static string BuildPrompt(IReadOnlyList<string> pageTexts, int firstPageNumber)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pageTexts.Count; i++)
        {
            sb.Append("=== 第 ").Append(firstPageNumber + i).Append(" 页 ===\n");
            sb.Append(pageTexts[i]).Append('\n');
        }
        return sb.ToString();
    }

    private static string BuildTocProbePrompt(int firstPageNumber, int pageCount)
    {
        var pageLabel = pageCount > 1
            ? $"{firstPageNumber}~{firstPageNumber + pageCount - 1}"
            : $"{firstPageNumber}";
        return $"以下是 PDF 第 {pageLabel} 页的文本（这批页面可能位于书的中间部分）。\n";
    }

    private static string Truncate(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    private const string TocProbePrompt = """
你是 PDF 目录探测助手。用户提供的是 PDF 中连续若干页的文本，这批页面可能位于书的开头、中间或结尾。
请只判断这批页面中是否包含目录页（出现"目录"、"目 录"、"Contents"、"Table of Contents"等字样）。
- 若包含目录页：输出 {"hasToc":true}
- 若不包含目录页：输出 {"hasToc":false}
不要生成书签，不要输出任何其他文字，只输出 JSON。
""";

    private const string VisionTocProbePrompt = """
你是 PDF 目录探测助手。用户提供 PDF 中连续若干页的页面截图，这批页面可能位于书的开头、中间或结尾。
请只判断这批页面中是否包含目录页（出现"目录"、"目 录"、"Contents"、"Table of Contents"等字样）。
- 若包含目录页：输出 {"hasToc":true}
- 若不包含目录页：输出 {"hasToc":false}
不要生成书签，不要输出任何其他文字，只输出 JSON。
""";

    private const string TocPrompt = """
你是 PDF 目录识别助手。用户提供 PDF 前若干页的文本（按顺序）。
1. 判断这些页里是否包含目录页（出现"目录"、"目 录"、"Contents"、"Table of Contents"等字样）。
2. 有目录：提取目前能看到的所有目录条目，保留层级（子条目放 children），页码为目录中标注的书本页码（从 1 开始的正整数）。目录中没有标注页码的条目（如"出版者的话"、无点线引导的条目标题）不要输出。同时判断目录是否已经完整读完：
   - 如果最后一页底部仍在继续列出目录条目、明显还有后续目录页，tocComplete=false；
   - 如果最后一页的目录已经结束（之后是正文、空白，或用户提示已经是最后几页），tocComplete=true。
3. 没有目录：输出 {"hasToc":false}
4. 只输出 JSON，不要输出任何其他文字。
JSON 格式：
{"hasToc":true,"tocComplete":true,"bookmarks":[{"title":"大标题","page":1,"children":[{"title":"子标题","page":2,"children":[]}]}]}
""";
    private static string BuildVisionPrompt(int firstPageNumber, int pageCount)
    {
        return
            $"你是 PDF 书签生成助手。用户提供了 PDF 的页面图片（按顺序，共 {pageCount} 张）。\n" +
            $"这批图片对应整个 PDF 的第 {firstPageNumber}~{firstPageNumber + pageCount - 1} 页。\n" +
            "1. 先识别是否有目录页（出现\"目录\"、\"Contents\"、\"Table of Contents\"等字样）。若有，以目录内容为准，按层级生成书签，页码以目录标注为准。\n" +
            "2. 若没有目录页，识别每页的大标题（最大字号标题）确定章节结构，没有标题的页面归入最近章节。\n" +
            "3. 书签层级最多 3 层，标题保留原文用词（不超过 30 字）。\n" +
            "4. 页码为整个 PDF 的绝对页码（第 1 页 = page 1），children 的页码必须大于等于父书签页码。\n" +
            "5. 只输出 JSON，不要输出任何其他文字或代码块标记。\n" +
            "JSON 格式：\n" +
            "{\"bookmarks\":[{\"title\":\"大标题\",\"page\":1,\"children\":[{\"title\":\"子标题\",\"page\":2,\"children\":[]}]}]}";
    }

    private const string SystemPrompt = """
你是 PDF 书签生成助手。用户会提供 PDF 的逐页文本，请生成结构化书签目录。
流程：
1. 先浏览所有页文本，识别是否存在目录页（出现"目录"、"目 录"、"Contents"、"Table of Contents"等字样）。若有，以目录内容为主：目录中的大标题作为一级书签、子标题作为二级/三级书签，页码以目录标注为准。
2. 若没有目录页，逐页寻找页首的大标题来确定章节结构；没有明显标题的页面归入最近的章节。
3. 书签层级最多 3 层。
4. 标题保留原文用词并保持简洁（不超过 30 字），不要自行编造章节。
5. 页码为整个 PDF 的绝对页码（第 1 页 = page 1），children 的页码必须大于等于父书签页码。
6. 空白页、版权页、扉页等无内容页可以跳过。
7. 只输出 JSON，不要输出任何其他文字或代码块标记。
JSON 格式：
{"bookmarks":[{"title":"大标题","page":1,"children":[{"title":"子标题","page":2,"children":[]}]}]}
""";
}
