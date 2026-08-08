namespace PDFReaderX.LLM;

/// <summary>
/// 书签页码定位工具：
/// 在 PDF 页面文本中搜索目录标题，确认"书本页码 → PDF 页码"的偏移量。
/// </summary>
public static class BookmarkLocator
{
    /// <summary>
    /// 统计前若干个一级书签标题在正文中的首次出现页，计算候选偏移（PDF 页 - 书本页）。
    /// 出现次数最多且 ≥2 的偏移才确认；searchStartPage 用于跳过目录页，
    /// 避免目录页里同名标题干扰定位（目录页通常在已读过的前几页）。
    /// </summary>
    public static int? ConfirmOffset(
        IReadOnlyList<LlmBookmark> bookmarks,
        IReadOnlyList<string> pageTexts,
        int searchStartPage = 0)
    {
        var counts = new Dictionary<int, int>();
        var checkedTitles = 0;
        foreach (var bookmark in bookmarks)
        {
            if (checkedTitles >= 10)
            {
                break;
            }
            var normalizedTitle = Normalize(bookmark.Title);
            if (normalizedTitle.Length == 0)
            {
                continue;
            }
            checkedTitles++;
            for (var page = Math.Max(0, searchStartPage); page < pageTexts.Count; page++)
            {
                if (Normalize(pageTexts[page]).Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase))
                {
                    var offset = page - bookmark.PageIndex;
                    counts[offset] = counts.GetValueOrDefault(offset) + 1;
                    break; // 只取首次出现，避免正文中反复出现的同名标题
                }
            }
        }

        if (counts.Count == 0)
        {
            return null;
        }
        var ranked = counts.OrderByDescending(pair => pair.Value).ToList();
        var best = ranked[0];
        if (best.Value < 2)
        {
            return null;
        }
        if (ranked.Count > 1 && ranked[1].Value == best.Value)
        {
            return null; // 多个偏移票数并列，无法确认
        }
        return best.Key;
    }

    /// <summary>就地平移书签子树页码（修复模型页码偏移时使用）。</summary>
    public static void ShiftSubtree(LlmBookmark node, int delta)
    {
        node.PageIndex = Math.Max(0, node.PageIndex + delta);
        foreach (var child in node.Children)
        {
            ShiftSubtree(child, delta);
        }
    }

    /// <summary>把书签页码按偏移平移，钳制到合法页范围。</summary>
    public static List<LlmBookmark> ShiftPages(IReadOnlyList<LlmBookmark> bookmarks, int offset, int pageCount)
    {
        var result = new List<LlmBookmark>(bookmarks.Count);
        foreach (var bookmark in bookmarks)
        {
            result.Add(ShiftNode(bookmark, offset, pageCount));
        }
        return result;
    }

    private static LlmBookmark ShiftNode(LlmBookmark bookmark, int offset, int pageCount)
    {
        var shifted = new LlmBookmark(bookmark.Title, Math.Clamp(bookmark.PageIndex + offset, 0, Math.Max(0, pageCount - 1)));
        foreach (var child in bookmark.Children)
        {
            shifted.Children.Add(ShiftNode(child, offset, pageCount));
        }
        return shifted;
    }

    private static string Normalize(string text)
    {
        return string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }
}

