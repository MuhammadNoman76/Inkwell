using System.Text;

namespace Inkwell.Tests;

/// <summary>The repository's own files (the installer script, the README, the app's sources), for consistency checks.</summary>
internal static class RepoFiles
{
    /// <summary>The repository folder: the first folder above the test assembly that holds installer\Inkwell.iss.</summary>
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "installer", "Inkwell.iss"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No folder above " + AppContext.BaseDirectory + " holds installer\\Inkwell.iss.");
    }

    public static string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public static string Read(string relative) => File.ReadAllText(PathOf(relative), Encoding.UTF8);

    /// <summary>Source files under a folder of the repository (build output left out).</summary>
    public static IEnumerable<string> Files(string folder, params string[] patterns)
    {
        var root = PathOf(folder);
        foreach (var pattern in patterns)
            foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                var relative = file.Substring(Root.Length).TrimStart(Path.DirectorySeparatorChar);
                var parts = relative.Split(Path.DirectorySeparatorChar);
                if (parts.Any(p => p is "bin" or "obj")) continue;
                yield return file;
            }
    }

    public static string Relative(string file) => file.Substring(Root.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
}
