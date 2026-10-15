namespace Inkwell.Services;

/// <summary>--capture &lt;folder&gt;: screenshots of Inkwell's own windows for the documentation (filled in below).</summary>
internal static partial class CaptureRunner
{
    public static bool Running { get; private set; }

    public static void Run(string folder)
    {
        Running = true;
        RunCore(folder);
    }

    static partial void RunCore(string folder);
}
