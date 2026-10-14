using System.Windows;
using System.Windows.Threading;
using Inkwell.Core.Editing;
using Inkwell.Services;
using Inkwell.Themes;
using Inkwell.Views;

namespace Inkwell;

public partial class App
{
    private readonly Command _command;

    public App(Command command)
    {
        _command = command;
    }

    public static new App Current => (App)Application.Current;

    /// <summary>Started with --updated after an automatic update.</summary>
    public bool WasUpdated => _command.Updated;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "UI thread");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error(args.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Task");
            args.SetObserved();
        };

        try
        {
            ThemeManager.Initialize(AppSettings.Load().Theme);
            switch (_command.Verb)
            {
                case Verb.ExportPdf:
                    Shutdown(HeadlessExport.Run(_command));
                    return;
                case Verb.Capture:
                    ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    Dispatcher.BeginInvoke(new Action(() => CaptureRunner.Run(_command.Output!)), DispatcherPriority.Loaded);
                    return;
                case Verb.Settings:
                    ShutdownMode = ShutdownMode.OnLastWindowClose;
                    var settings = new SettingsWindow { WindowStartupLocation = WindowStartupLocation.CenterScreen };
                    MainWindow = settings;
                    settings.Show();
                    return;
                default:
                    ShutdownMode = ShutdownMode.OnLastWindowClose;
                    InstanceManager.Listen(args => Dispatcher.BeginInvoke(new Action(() => OnRemoteCommand(args))));
                    var paths = _command.Paths;
                    if (paths.Count == 0) ShowStart(null);
                    else foreach (var p in paths.Take(8)) OpenPath(p, null);
                    UpdateService.Start();
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Startup");
            MessageBox.Show("Inkwell couldn't start:\n\n" + ex.Message, "Inkwell", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Another launch handed us its command line.</summary>
    private void OnRemoteCommand(string[] args)
    {
        var command = Command.Parse(args);
        if (command.Verb == Verb.Settings)
        {
            SettingsWindow.ShowSingle(Windows.OfType<Window>().FirstOrDefault(w => w.IsActive));
            return;
        }
        if (command.Paths.Count == 0)
        {
            ShowStart(null);
            return;
        }
        foreach (var p in command.Paths.Take(8)) OpenPath(p, null);
    }

    // ------------------------------------------------------------------ windows

    public IEnumerable<MainWindow> DocumentWindows => Windows.OfType<MainWindow>();

    /// <summary>A window with the start screen (a new one unless <paramref name="reuse"/> is given).</summary>
    public MainWindow ShowStart(MainWindow? reuse)
    {
        var window = reuse ?? CreateWindow();
        window.ShowStartView();
        if (!window.IsVisible) window.Show();
        window.BringToFront();
        return window;
    }

    private MainWindow CreateWindow()
    {
        var window = new MainWindow();
        MainWindow ??= window;
        return window;
    }

    /// <summary>
    /// Opens a document. If it's already open, that window comes forward. The start screen of
    /// <paramref name="requester"/> is reused; otherwise a new window opens.
    /// </summary>
    public async void OpenPath(string path, MainWindow? requester)
    {
        var existing = DocumentWindows.FirstOrDefault(w => w.Session?.FilePath != null && string.Equals(w.Session.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.BringToFront();
            return;
        }
        var window = requester != null && requester.Session == null ? requester : CreateWindow();
        if (!window.IsVisible)
        {
            window.ShowStartView();
            window.Show();
        }
        window.BringToFront();
        await window.OpenFileAsync(path);
    }

    /// <summary>A window for a session created in code (new document, import, recovery).</summary>
    public MainWindow OpenSession(DocumentSession session, MainWindow? requester, string? message = null)
    {
        var window = requester != null && requester.Session == null ? requester : CreateWindow();
        window.ShowEditor(session);
        if (!window.IsVisible) window.Show();
        window.BringToFront();
        if (message != null) window.ShowMessage(message);
        return window;
    }
}
