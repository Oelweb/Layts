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

    protected override void OnExit(ExitEventArgs e)
    {
        // Never leave a key "held down" when layts closes while a wheel button is pressed.
        Layts.Core.WheelMapper.Instance.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(Layts.Core.Loc.Get("UnexpectedError") + "\n\n" + e.Exception.Message, "layts",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
