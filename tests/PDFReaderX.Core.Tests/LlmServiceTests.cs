using PDFReaderX.LLM;

namespace PDFReaderX.Core.Tests;

public class LlmServiceTests
{
    [Fact]
    public void ParseBookmarks_ReturnsTree()
    {
        const string json =
            "{\"bookmarks\":[{\"title\":\"第一章\",\"page\":1,\"children\":[{\"title\":\"第一节\",\"page\":2,\"children\":[]}]},{\"title\":\"第二章\",\"page\":5,\"children\":[]}]}";

        var result = OpenAiCompatibleService.ParseBookmarks(json);

        Assert.Equal(2, result.Count);
        Assert.Equal("第一章", result[0].Title);
        Assert.Equal(0, result[0].PageIndex); // 1 基转 0 基
        Assert.Single(result[0].Children);
        Assert.Equal("第一节", result[0].Children[0].Title);
        Assert.Equal(1, result[0].Children[0].PageIndex);
        Assert.Equal("第二章", result[1].Title);
        Assert.Equal(4, result[1].PageIndex);
    }

    [Fact]
    public void ParseBookmarks_StripsMarkdownFence()
    {
        const string json =
            "```json\n{\"bookmarks\":[{\"title\":\"目录\",\"page\":3,\"children\":[]}]}\n```";

        var result = OpenAiCompatibleService.ParseBookmarks(json);

        Assert.Single(result);
        Assert.Equal("目录", result[0].Title);
        Assert.Equal(2, result[0].PageIndex);
    }

    [Fact]
    public void ParseBookmarks_AcceptsTopLevelArray()
    {
        const string json = "[{\"title\":\"标题A\",\"page\":2,\"children\":[]}]";

        var result = OpenAiCompatibleService.ParseBookmarks(json);

        Assert.Single(result);
        Assert.Equal("标题A", result[0].Title);
        Assert.Equal(1, result[0].PageIndex);
    }

    [Fact]
    public void ParseBookmarks_SkipsInvalidPageAndEmptyTitle()
    {
        const string json =
            "{\"bookmarks\":[{\"title\":\"\",\"page\":1,\"children\":[]},{\"title\":\"正常\",\"page\":0,\"children\":[]}]}";

        var result = OpenAiCompatibleService.ParseBookmarks(json);

        Assert.Single(result);
        Assert.Equal("正常", result[0].Title);
        Assert.Equal(0, result[0].PageIndex); // page 0 钳制为 0
    }

    [Fact]
    public void ParseBookmarks_DetectsZeroBasedPages()
    {
        const string json =
            "{\"bookmarks\":[{\"title\":\"第一章\",\"page\":0,\"children\":[]},{\"title\":\"第二章\",\"page\":1,\"children\":[]}]}";

        var result = OpenAiCompatibleService.ParseBookmarks(json);

        Assert.Equal(0, result[0].PageIndex); // 0 基保持不变
        Assert.Equal(1, result[1].PageIndex);
    }

    [Fact]
    public void ParseBookmarks_ThrowsWhenNoJson()
    {
        Assert.Throws<InvalidOperationException>(
            () => OpenAiCompatibleService.ParseBookmarks("抱歉，我无法生成书签"));
    }
}
