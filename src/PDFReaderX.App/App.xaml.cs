using System.IO;
using System.Windows;
using PDFReaderX.App.Services;
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

        FileAssociationHelper.RegisterPdfrxAssociation(); // 注册 .pdfrx 打开方式（HKCU，免安装版同样生效）

        var window = new MainWindow
        {
            DataContext = new MainWindowViewModel(),
        };
        MainWindow = window;
        window.Show();

        if (e.Args.Length > 0 && File.Exists(e.Args[0]) && window.DataContext is MainWindowViewModel viewModel)
        {
            if (Path.GetExtension(e.Args[0]).Equals(".pdfrx", StringComparison.OrdinalIgnoreCase))
            {
                _ = viewModel.OpenPdfrxAsync(e.Args[0]);
            }
            else
            {
                _ = viewModel.OpenFileAsync(e.Args[0]);
            }
        }
    }
}
