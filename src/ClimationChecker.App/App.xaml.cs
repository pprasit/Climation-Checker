using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ClimationChecker.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogFatalException("Dispatcher unhandled exception", e.Exception);
        MessageBox.Show(
            $"DonutScope detected an error and kept the app open.\n\n{e.Exception.Message}\n\nA log was written to output\\logs\\donutscope-crash.log.",
            "DonutScope Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogFatalException("AppDomain unhandled exception", exception);
        }
        else
        {
            LogFatalMessage("AppDomain unhandled exception", e.ExceptionObject?.ToString() ?? "Unknown fatal exception");
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogFatalException("Unobserved task exception", e.Exception);
        e.SetObserved();
    }

    private static void LogFatalException(string context, Exception exception)
    {
        LogFatalMessage(context, exception.ToString());
    }

    private static void LogFatalMessage(string context, string message)
    {
        try
        {
            var root = FindRepositoryRoot(AppContext.BaseDirectory);
            var logDirectory = Path.Combine(root, "output", "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "donutscope-crash.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {context}{Environment.NewLine}{message}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static string FindRepositoryRoot(string startDirectory)
    {
        var current = startDirectory;
        for (var i = 0; i < 8; i++)
        {
            if (Directory.Exists(Path.Combine(current, "Image")) &&
                Directory.Exists(Path.Combine(current, "src", "climation_checker")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        return AppContext.BaseDirectory;
    }
}
