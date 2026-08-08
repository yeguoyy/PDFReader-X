using PDFReaderX.LLM;

namespace PDFReaderX.Core.Tests;

public class BookmarkTocTests
{
    [Fact]
    public void ParseToc_ReturnsBookmarks()
    {
        const string json =
            "{\"hasToc\":true,\"bookmarks\":[{\"title\":\"第一章\",\"page\":3,\"children\":[{\"title\":\"1.1 小节\",\"page\":3,\"children\":[]}]}]}";

        var result = OpenAiCompatibleService.ParseToc(json);

        Assert.True(result.HasToc);
        Assert.Single(result.Bookmarks);
        Assert.Equal("第一章", result.Bookmarks[0].Title);
        Assert.Equal(2, result.Bookmarks[0].PageIndex); // 书本页码 3 → 0 基 2
        Assert.Single(result.Bookmarks[0].Children);
    }

    [Fact]
    public void ParseToc_ParsesTocComplete()
    {
        const string json =
            "{\"hasToc\":true,\"tocComplete\":false,\"bookmarks\":[{\"title\":\"第一章\",\"page\":1,\"children\":[]}]}";

        var result = OpenAiCompatibleService.ParseToc(json);

        Assert.True(result.HasToc);
        Assert.False(result.TocComplete);
        Assert.Single(result.Bookmarks);
    }

    [Fact]
    public void TrySalvageBookmarks_RecoversPartialJson()
    {
        const string json =
            "{\"hasToc\":true,\"bookmarks\":[{\"title\":\"第一章\",\"page\":1,\"children\":[]},{\"title\":\"第二章\",\"page\":12,\"child";

        var ok = OpenAiCompatibleService.TrySalvageBookmarks(json, out var bookmarks);

        Assert.True(ok);
        Assert.Single(bookmarks);
        Assert.Equal("第一章", bookmarks[0].Title);
        Assert.Equal(0, bookmarks[0].PageIndex);
    }

    [Fact]
    public void TrySalvageBookmarks_ReturnsFalseWithoutBookmarks()
    {
        Assert.False(OpenAiCompatibleService.TrySalvageBookmarks("{\"hasToc\":true}", out _));
    }

    [Fact]
    public void ParseToc_RepairsResetChapterPages()
    {
        const string json =
            "{\"hasToc\":true,\"tocComplete\":true,\"bookmarks\":[" +
            "{\"title\":\"前言\",\"page\":3,\"children\":[]}," +
            "{\"title\":\"第1章\",\"page\":1,\"children\":[{\"title\":\"1.1\",\"page\":2,\"children\":[]}]}," +
            "{\"title\":\"第2章\",\"page\":32,\"children\":[]}]}";

        var result = OpenAiCompatibleService.ParseToc(json);

        Assert.Equal(2, result.Bookmarks[0].PageIndex); // 前言 3 → 0 基 2
        Assert.Equal(3, result.Bookmarks[1].PageIndex); // 第1章页码被重置为 1，修复为 4（0 基 3）
        Assert.Equal(4, result.Bookmarks[1].Children[0].PageIndex); // 1.1 随子树平移到 5（0 基 4）
        Assert.Equal(31, result.Bookmarks[2].PageIndex); // 第2章不受影响
    }

    [Fact]
    public void ParseToc_NoTocReturnsFalse()
    {
        var result = OpenAiCompatibleService.ParseToc("{\"hasToc\":false}");

        Assert.False(result.HasToc);
        Assert.Empty(result.Bookmarks);
    }

    [Fact]
    public void ConfirmOffset_ReturnsMajorityOffset()
    {
        var bookmarks = new List<LlmBookmark>
        {
            new("第一章", 4), // 书本 0 基第 5 页
            new("第二章", 30),
            new("第三章", 60),
        };
        var texts = new List<string>(70);
        for (var i = 0; i < 70; i++)
        {
            texts.Add($"第 {i + 1} 页正文内容。");
        }
        // 第一章出现在 PDF 第 9 页（offset=5）和第 4 页（目录页，offset=0）
        texts[4] = "目录 第一章 .... 第二章 .... 第三章";
        texts[9] = "第一章 引言内容";
        texts[35] = "第二章 方法内容";
        texts[65] = "第三章 结论内容";

        var offset = BookmarkLocator.ConfirmOffset(bookmarks, texts, searchStartPage: 5);

        Assert.Equal(5, offset); // 从目录页之后定位，3 个章节一致 offset=5
    }

    [Fact]
    public void ConfirmOffset_ReturnsNullWhenAmbiguous()
    {
        var bookmarks = new List<LlmBookmark>
        {
            new("第一章", 0),
            new("第二章", 10),
            new("第三章", 20),
            new("第四章", 30),
        };
        var texts = new List<string>(35);
        for (var i = 0; i < 35; i++)
        {
            texts.Add("正文");
        }
        texts[5] = "第一章 第二章 内容";
        texts[25] = "第三章 第四章 内容";

        var offset = BookmarkLocator.ConfirmOffset(bookmarks, texts);

        Assert.Null(offset); // offset 5 与 -5 各 2 票，并列无法确认
    }

    [Fact]
    public void ConfirmOffset_ReturnsNullWhenUncertain()
    {
        var bookmarks = new List<LlmBookmark> { new("第一章", 4) };
        var texts = new List<string> { "第一章 内容", "其他内容" };

        var offset = BookmarkLocator.ConfirmOffset(bookmarks, texts);

        Assert.Null(offset); // 只有一个命中，次数不足 2
    }

    [Fact]
    public void ShiftPages_AppliesOffsetAndClamps()
    {
        var bookmarks = new List<LlmBookmark>
        {
            new("第一章", 2) { Children = { new("1.1", 2) } },
            new("第二章", 8),
        };

        var result = BookmarkLocator.ShiftPages(bookmarks, 5, 10);

        Assert.Equal(7, result[0].PageIndex);
        Assert.Equal(7, result[0].Children[0].PageIndex);
        Assert.Equal(9, result[1].PageIndex); // 8+5=13 钳制到 9
    }
}
