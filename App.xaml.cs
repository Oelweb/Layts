using System.Windows;
using System.Windows.Threading;

namespace Layts;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("Unerwarteter Fehler:\n\n" + e.Exception.Message, "Layts",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
