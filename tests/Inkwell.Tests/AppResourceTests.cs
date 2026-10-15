using System.Text.RegularExpressions;

namespace Inkwell.Tests;

/// <summary>The app's resources: every icon the code asks for exists (a missing one shows nothing, silently).</summary>
public class AppResourceTests
{
    [Fact]
    public void EveryIconUsed_IsDefined()
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in RepoFiles.Files("src/Inkwell/Themes", "*.xaml"))
            foreach (Match m in Regex.Matches(RepoFiles.Read(RepoFiles.Relative(file)), @"x:Key=""(Icon\.[A-Za-z0-9]+)"""))
                defined.Add(m.Groups[1].Value);
        Assert.True(defined.Count > 50, "No icons found");

        var missing = new List<string>();
        foreach (var file in RepoFiles.Files("src/Inkwell", "*.cs", "*.xaml"))
        {
            var name = RepoFiles.Relative(file);
            foreach (Match m in Regex.Matches(RepoFiles.Read(name), @"[""\s](Icon\.[A-Za-z0-9]+)[""}]"))
                if (!defined.Contains(m.Groups[1].Value)) missing.Add(name + ": " + m.Groups[1].Value);
        }
        Assert.True(missing.Count == 0, "Icons that don't exist: " + string.Join(", ", missing.Distinct()));
    }
}
