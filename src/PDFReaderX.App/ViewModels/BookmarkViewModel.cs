using System.Collections.ObjectModel;
using PDFReaderX.Core.Services;

namespace PDFReaderX.App.ViewModels;

/// <summary>
/// 书签树节点，对应 PDF Outline 的一项，供侧边栏 TreeView 展示与跳转。
/// </summary>
public sealed class BookmarkViewModel : ViewModelBase
{
    public BookmarkViewModel(string title, int pageIndex)
    {
        Title = title;
        PageIndex = pageIndex;
    }

    /// <summary>书签标题。</summary>
    public string Title { get; }

    /// <summary>目标页索引（0 基）；-1 表示无有效目标。</summary>
    public int PageIndex { get; }

    public bool CanNavigate => PageIndex >= 0;

    public string PageLabel => PageIndex >= 0 ? $"第 {PageIndex + 1} 页" : string.Empty;

    private bool _isCurrent;

    /// <summary>是否对应阅读区当前页（由侧边栏联动设置）。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
            {
                return;
            }

            _isCurrent = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<BookmarkViewModel> Children { get; } = new();

    public static BookmarkViewModel FromCore(BookmarkNode node)
    {
        var viewModel = new BookmarkViewModel(node.Title, node.PageIndex);
        foreach (var child in node.Children)
        {
            viewModel.Children.Add(FromCore(child));
        }
        return viewModel;
    }
}