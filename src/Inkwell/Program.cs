using System.Runtime.CompilerServices;
using Inkwell.Interop;
using Inkwell.Services;

namespace Inkwell;

internal static class Program
{
    /// <summary>
    /// Entry point. A second launch (a double-clicked document, the Start menu) hands its command line to the
    /// Inkwell that is already running and exits before any window code is loaded.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        var command = Command.Parse(args);
        if (command.CanHandOver && !Views.MainWindow.IsTestOffscreen)
        {
            // Let the running Inkwell bring its window to the front for us.
            Native.AllowAnyForeground();
            if (!InstanceManager.TryBecomePrimary(command.ToArgs())) return 0;
        }
        try
        {
            return RunApp(command);
        }
        finally
        {
            InstanceManager.Stop();
        }
    }

    // Kept out of Main so a process that hands over exits without loading WPF.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp(Command command)
    {
        var app = new App(command);
        app.InitializeComponent();
        return app.Run();
    }
}
