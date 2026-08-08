using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PDFReaderX.LLM;

namespace PDFReaderX.App.ViewModels;

/// <summary>
/// AI 生成书签的预览窗口 ViewModel：支持编辑标题/页码、删除节点，应用后转回侧边栏书签树。
/// </summary>
public sealed partial class BookmarkPreviewViewModel : ViewModelBase
{
    public ObservableCollection<EditableBookmarkViewModel> Roots { get; } = new();

    [ObservableProperty]
    private EditableBookmarkViewModel? _selected;

    [ObservableProperty]
    private string _hint;

    public int PageCount { get; }

    public BookmarkPreviewViewModel(IReadOnlyList<LlmBookmark> bookmarks, int pageCount)
    {
        PageCount = pageCount;
        foreach (var bookmark in bookmarks)
        {
            Roots.Add(EditableBookmarkViewModel.FromLlm(bookmark));
        }
        Hint = bookmarks.Count == 0
            ? "AI 没有生成有效的书签"
            : $"AI 生成了 {bookmarks.Count} 个顶层书签，可编辑后点击“应用”";
    }

    public void DeleteSelected()
    {
        if (Selected is null)
        {
            return;
        }
        RemoveFrom(Roots, Selected);
        Selected = null;
    }

    public List<BookmarkViewModel> BuildResult()
    {
        var result = new List<BookmarkViewModel>();
        foreach (var root in Roots)
        {
            result.Add(ToBookmark(root));
        }
        return result;
    }

    private static bool RemoveFrom(
        ObservableCollection<EditableBookmarkViewModel> collection,
        EditableBookmarkViewModel target)
    {
        if (collection.Remove(target))
        {
            return true;
        }
        foreach (var item in collection)
        {
            if (RemoveFrom(item.Children, target))
            {
                return true;
            }
        }
        return false;
    }

    private static BookmarkViewModel ToBookmark(EditableBookmarkViewModel viewModel)
    {
        var pageIndex = Math.Max(0, viewModel.PageNumber - 1);
        var bookmark = new BookmarkViewModel(viewModel.Title, pageIndex);
        foreach (var child in viewModel.Children)
        {
            bookmark.Children.Add(ToBookmark(child));
        }
        return bookmark;
    }
}
