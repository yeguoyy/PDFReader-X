using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PDFReaderX.LLM;

namespace PDFReaderX.App.ViewModels;

/// <summary>
/// 书签预览窗口中的可编辑节点：标题与页码均可修改。
/// PageNumber 为用户可见的 1 基页码。
/// </summary>
public sealed partial class EditableBookmarkViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private int _pageNumber = 1;

    public ObservableCollection<EditableBookmarkViewModel> Children { get; } = new();

    public string PageLabel => $"第 {PageNumber} 页";

    partial void OnPageNumberChanged(int value)
    {
        OnPropertyChanged(nameof(PageLabel));
    }

    public static EditableBookmarkViewModel FromLlm(LlmBookmark node)
    {
        var viewModel = new EditableBookmarkViewModel
        {
            Title = node.Title,
            PageNumber = Math.Max(1, node.PageIndex + 1),
        };
        foreach (var child in node.Children)
        {
            viewModel.Children.Add(FromLlm(child));
        }
        return viewModel;
    }
}
