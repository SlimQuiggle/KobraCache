using System.Windows;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;

namespace KobraCache.Desktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppLogger.Initialize();
        AppLogger.Info("Application startup requested.");
        RegisterExceptionHandlers();

        try
        {
            base.OnStartup(e);
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
            AppLogger.Info("Main window shown.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Startup failed before the main window could be shown.", ex);
            MessageBox.Show(
                $"KobraCache could not start. A diagnostic log was written to:{Environment.NewLine}{AppLogger.CurrentLogPath}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "KobraCache startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Info($"Application exiting with code {e.ApplicationExitCode}.");
        base.OnExit(e);
    }

    private void RegisterExceptionHandlers()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Unhandled UI exception.", e.Exception);
        MessageBox.Show(
            $"KobraCache hit an unexpected error. Details were written to:{Environment.NewLine}{AppLogger.CurrentLogPath}{Environment.NewLine}{Environment.NewLine}{e.Exception.Message}",
            "KobraCache error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            AppLogger.Error("Unhandled application domain exception.", ex);
        }
        else
        {
            AppLogger.Error($"Unhandled application domain exception: {e.ExceptionObject}");
        }
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Error("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}
