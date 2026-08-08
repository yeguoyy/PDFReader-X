using System.Windows;
using PDFReaderX.App.ViewModels;
using PDFReaderX.LLM;

namespace PDFReaderX.App;

/// <summary>
/// AI 生成书签的预览与编辑窗口：确认后把编辑结果写回侧边栏书签树。
/// </summary>
public partial class BookmarkPreviewWindow : Window
{
    private readonly BookmarkPreviewViewModel _viewModel;

    public BookmarkPreviewWindow(IReadOnlyList<LlmBookmark> bookmarks, int pageCount)
    {
        InitializeComponent();
        _viewModel = new BookmarkPreviewViewModel(bookmarks, pageCount);
        DataContext = _viewModel;
    }

    /// <summary>点击“应用”后的最终书签树；取消时为 null。</summary>
    public List<BookmarkViewModel>? Result { get; private set; }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _viewModel.Selected = e.NewValue as EditableBookmarkViewModel;
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        _viewModel.DeleteSelected();
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        Result = _viewModel.BuildResult();
        DialogResult = true;
    }
}
