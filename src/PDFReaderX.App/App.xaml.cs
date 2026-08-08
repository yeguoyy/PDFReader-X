using System.IO;
using System.Windows;
using PDFReaderX.App.ViewModels;

namespace PDFReaderX.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow
        {
            DataContext = new MainWindowViewModel(),
        };
        MainWindow = window;
        window.Show();

        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
        {
            _ = ((MainWindowViewModel)window.DataContext).OpenFileAsync(e.Args[0]);
        }
    }
}
