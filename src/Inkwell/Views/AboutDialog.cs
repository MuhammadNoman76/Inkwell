using System.Text;
using System.Windows;
using System.Windows.Controls;
using Inkwell.Controls;
using Inkwell.Services;

namespace Inkwell.Views;

/// <summary>About Inkwell: version, edition, credits and the full third-party licence texts.</summary>
public sealed class AboutDialog : FluentWindow
{
    private AboutDialog()
    {
        Title = "About Inkwell";
        Width = 680;
        Height = 640;
        MinWidth = 480;
        MinHeight = 400;
        IsDialog = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(28, 22, 28, 20) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var logo = new Image { Width = 64, Height = 64, Margin = new Thickness(0, 0, 18, 0) };
        logo.SetResourceReference(Image.SourceProperty, "Logo.Mark");
        DockPanel.SetDock(logo, Dock.Left);
        header.Children.Add(logo);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = "Inkwell", FontSize = 26 };
        name.SetResourceReference(StyleProperty, "Text.Title");
        titles.Children.Add(name);
        var tagline = new TextBlock { Text = "Write beautifully. Without fighting your document." };
        tagline.SetResourceReference(StyleProperty, "Text.BodySecondary");
        titles.Children.Add(tagline);
        var version = new TextBlock { Text = $"Version {Updater.CurrentVersionText} · {UpdateService.EditionName(Updater.Edition)} · © 2026 Muhammad Noman", Margin = new Thickness(0, 4, 0, 0) };
        version.SetResourceReference(StyleProperty, "Text.Caption");
        titles.Children.Add(version);
        header.Children.Add(titles);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var credits = new TextBlock
        {
            Text = "Inkwell uses Microsoft's Open XML SDK (MIT) for Word documents, PDFsharp (MIT) for PDF export, Markdig (BSD-2-Clause) for Markdown, Fluent UI System Icons (MIT), " +
                   "and fonts under the SIL Open Font License: Literata, Inter, Source Serif 4, Playfair Display, DM Serif Display, DM Sans, Plus Jakarta Sans, Space Grotesk, JetBrains Mono, Noto Naskh Arabic and Noto Nastaliq Urdu. " +
                   "Inkwell is independent and not affiliated with Microsoft; Word is a trademark of Microsoft Corporation.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        credits.SetResourceReference(StyleProperty, "Text.Body");
        DockPanel.SetDock(credits, Dock.Top);
        root.Children.Add(credits);

        var close = new Button { Content = "Close", MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true, IsDefault = true, Margin = new Thickness(0, 14, 0, 0) };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);

        var notices = new TextBox
        {
            Text = ReadNotices(),
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(12, 10, 12, 10),
        };
        System.Windows.Automation.AutomationProperties.SetName(notices, "Third-party notices");
        root.Children.Add(notices);
        Content = root;
    }

    private static string ReadNotices()
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "THIRD-PARTY-NOTICES.txt is missing from the program folder.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static void Open(Window owner)
    {
        var dialog = new AboutDialog();
        if (owner.IsVisible) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
    }
}
