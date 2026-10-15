using System.Text.RegularExpressions;
using Inkwell.Services;
using Microsoft.Win32;

namespace Inkwell.Tests;

/// <summary>
/// The updater's link to the installer: the AppId it looks for is the installer's, it tells "only for me" from
/// "everyone" installs in default and custom folders, and the sources carry no other app's identifiers.
/// </summary>
public class UpdaterTests
{
    [Fact]
    public void InstallerAppId_IsTheOneInTheInstallerScript()
    {
        var iss = RepoFiles.Read("installer/Inkwell.iss");
        // Inno Setup writes a literal "{" as "{{": AppId={{B1102FC4-...}
        var m = Regex.Match(iss, @"^AppId=\{(\{[0-9A-Fa-f-]{36}\})\s*$", RegexOptions.Multiline);
        Assert.True(m.Success, "No AppId line in installer\\Inkwell.iss");
        Assert.Equal(Updater.InstallerAppId, m.Groups[1].Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Updater.UninstallKey, @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + m.Groups[1].Value + "_is1", StringComparer.OrdinalIgnoreCase);
        Assert.Single(Regex.Matches(iss, @"^AppId=", RegexOptions.Multiline).Cast<Match>()); // one AppId for every edition
    }

    /// <summary>Inkwell's version is the same in both projects and in the build and installer defaults.</summary>
    [Fact]
    public void Version_IsTheSameEverywhere()
    {
        string Find(string file, string pattern)
        {
            var m = Regex.Match(RepoFiles.Read(file), pattern, RegexOptions.Multiline);
            Assert.True(m.Success, "No version in " + file);
            return m.Groups[1].Value;
        }
        var app = Find("src/Inkwell/Inkwell.csproj", @"<Version>([0-9.]+)</Version>");
        Assert.Equal(app, Find("src/Inkwell.Core/Inkwell.Core.csproj", @"<Version>([0-9.]+)</Version>"));
        Assert.Equal(app, Find("build.ps1", @"\[string\]\$Version = ""([0-9.]+)"""));
        Assert.Equal(app, Find("installer/Inkwell.iss", @"#define AppVersion ""([0-9.]+)"""));
    }

    private const string LocalPrograms = @"C:\Users\Ana\AppData\Local\Programs";
    private static readonly string?[] ProgramFolders = { @"C:\Program Files", @"C:\Program Files", @"C:\Program Files (x86)" };

    /// <summary>Runs the decision with a made-up registry: (hive, view) → InstallLocation of Inkwell's uninstall entry.</summary>
    private static InstallScope Detect(string appDir, params (RegistryHive Hive, RegistryView View, string Location)[] entries) =>
        Updater.DetectInstallScope(appDir, (hive, view) => entries.Where(e => e.Hive == hive && e.View == view).Select(e => e.Location).FirstOrDefault(), LocalPrograms, ProgramFolders);

    [Fact]
    public void InstallScope_OnlyForMe_DefaultAndCustomFolders()
    {
        // Inno Setup writes InstallLocation with a trailing backslash.
        Assert.Equal(InstallScope.CurrentUser, Detect(LocalPrograms + @"\Inkwell", (RegistryHive.CurrentUser, RegistryView.Default, LocalPrograms + @"\Inkwell\")));
        Assert.Equal(InstallScope.CurrentUser, Detect(@"D:\Tools\Inkwell", (RegistryHive.CurrentUser, RegistryView.Default, @"d:\tools\INKWELL\")));
    }

    [Fact]
    public void InstallScope_ForEveryone_DefaultAndCustomFolders_64And32BitViews()
    {
        Assert.Equal(InstallScope.AllUsers, Detect(@"C:\Program Files\Inkwell", (RegistryHive.LocalMachine, RegistryView.Registry64, @"C:\Program Files\Inkwell\")));
        // The x86 edition on 64-bit Windows: the entry is in the 32-bit view.
        Assert.Equal(InstallScope.AllUsers, Detect(@"C:\Program Files (x86)\Inkwell", (RegistryHive.LocalMachine, RegistryView.Registry32, @"C:\Program Files (x86)\Inkwell\")));
        Assert.Equal(InstallScope.AllUsers, Detect(@"E:\Apps\Inkwell", (RegistryHive.LocalMachine, RegistryView.Registry64, @"E:\Apps\Inkwell\")));
    }

    [Fact]
    public void InstallScope_AnotherInstalledCopy_DoesNotMakeThisOneInstalled()
    {
        // A copy unpacked in Downloads while the installed one lives in Program Files: the installer can't update it.
        Assert.Equal(InstallScope.NotInstalled, Detect(@"C:\Users\Ana\Downloads\Inkwell", (RegistryHive.LocalMachine, RegistryView.Registry64, @"C:\Program Files\Inkwell\")));
        Assert.Equal(InstallScope.NotInstalled, Detect(@"C:\Users\Ana\Downloads\Inkwell"));
    }

    [Fact]
    public void InstallScope_WithoutAnEntry_GoesByTheFolder()
    {
        Assert.Equal(InstallScope.CurrentUser, Detect(LocalPrograms + @"\Inkwell"));
        Assert.Equal(InstallScope.AllUsers, Detect(@"C:\Program Files\Inkwell"));
        Assert.Equal(InstallScope.AllUsers, Detect(@"C:\Program Files (x86)\Inkwell"));
        Assert.Equal(InstallScope.NotInstalled, Detect(@"C:\Program FilesX\Inkwell"));
    }

    [Fact]
    public void InstallerArguments_MatchTheScope()
    {
        Assert.Contains("/CURRENTUSER", Updater.InstallerArguments(InstallScope.CurrentUser));
        Assert.Contains("/ALLUSERS", Updater.InstallerArguments(InstallScope.AllUsers));
        Assert.Contains("/RELAUNCH=1", Updater.InstallerArguments(InstallScope.AllUsers));
        Assert.Contains("/DIR=\"" + Updater.AppDirectory + "\"", Updater.InstallerArguments(InstallScope.CurrentUser));
    }

    /// <summary>
    /// Inkwell was started from the code of the owner's other apps: none of their names or identifiers (AppIds,
    /// pipes, folders, feeds…) may be left in Inkwell's own code, installer or build scripts.
    /// </summary>
    [Fact]
    public void Sources_HaveNoOtherAppsIdentifiers()
    {
        var foreign = new Regex(@"Stagecraft|Trove|PawSip|VibePlayer|B522CE93-BE8D-4A68-A206-8B1ED3ED7489", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var files = RepoFiles.Files("src", "*.cs", "*.xaml", "*.csproj", "*.manifest")
            .Concat(RepoFiles.Files("installer", "*.iss"))
            .Concat(RepoFiles.Files("tools", "*.ps1", "*.mjs"))
            .Concat(new[] { RepoFiles.PathOf("build.ps1") });
        var found = new List<string>();
        foreach (var file in files)
        {
            var lines = RepoFiles.Read(RepoFiles.Relative(file)).Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (foreign.IsMatch(lines[i])) found.Add($"{RepoFiles.Relative(file)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(found.Count == 0, string.Join("\n", found));
    }
}
