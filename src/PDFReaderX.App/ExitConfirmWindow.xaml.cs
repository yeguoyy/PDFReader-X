using System.Windows;

namespace PDFReaderX.App;

public partial class ExitConfirmWindow : Window
{
    public ExitConfirmWindow()
    {
        InitializeComponent();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
