using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Ari61850Bridge;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            TryRouteException(args.Exception, "Unhandled", "Application-level UI exception captured");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Dispatcher.BeginInvoke(new Action(() => TryRouteException(ex, "Unhandled", "Application-level AppDomain exception captured")));
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            TryRouteException(args.Exception, "Unhandled", "Application-level background task exception captured");
            args.SetObserved();
        };
    }

    private void TryRouteException(Exception exception, string source, string context)
    {
        try
        {
            if (Current?.MainWindow is MainWindow mainWindow)
            {
                mainWindow.HandleGlobalException(exception, source, context);
                return;
            }

            System.Diagnostics.Debug.WriteLine($"{context}: {exception}");
        }
        catch
        {
            // Last-resort guard. Exception routing must never throw.
        }
    }
}
