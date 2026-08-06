using System.Windows;
using PDFReaderX.App.ViewModels;

namespace PDFReaderX.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}
