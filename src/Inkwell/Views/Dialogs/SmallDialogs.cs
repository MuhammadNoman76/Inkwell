using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Inkwell.Core.Model;

namespace Inkwell.Views.Dialogs;

/// <summary>Word count details.</summary>
public static class StatsDialog
{
    public static InkDialog Create(IReadOnlyList<(string Label, string Value)> rows)
    {
        var dialog = new InkDialog("Word count", null, 420);
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Label, Margin = new Thickness(0, 5, 16, 5) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            Grid.SetRow(label, i);
            grid.Children.Add(label);
            var value = new TextBlock { Text = rows[i].Value, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 5), HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }
        AutomationProperties.SetName(grid, "Counts");
        dialog.Body.Children.Add(grid);
        var note = new TextBlock { Text = "Counted like Word: words are separated by spaces; footnotes, endnotes and text boxes are included unless stated.", FontSize = 12, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        dialog.Body.Children.Add(note);
        return dialog;
    }

    public static void Show(Window owner, IReadOnlyList<(string Label, string Value)> rows, Action setGoal)
    {
        var dialog = Create(rows);
        dialog.AddButton("Set a word goal…");
        dialog.AddButton("Close", primary: true, isCancel: true);
        if (dialog.Run(owner) == "Set a word goal…") setGoal();
    }
}

/// <summary>What the link dialog decided.</summary>
public sealed class LinkResult
{
    public string? Url { get; init; }
    public string? Anchor { get; init; }
    public Paragraph? HeadingTarget { get; init; }
    public string? Text { get; init; }
    public string? Tooltip { get; init; }
    public bool Remove { get; init; }
}

/// <summary>A link to a web address, a heading or a bookmark.</summary>
public static class LinkDialog
{
    public static LinkResult? Show(Window owner, Hyperlink? existing, string selectedText, IReadOnlyList<(string Text, Paragraph Para)> headings, IReadOnlyList<string> bookmarks)
    {
        var dialog = new InkDialog(existing != null ? "Edit link" : "Insert link", "Link to a web page or e-mail address, or to a heading or bookmark in this document.", 500);
        var web = new RadioButton { Content = "Web page or e-mail", IsChecked = existing?.Anchor == null, GroupName = "kind", Margin = new Thickness(0, 4, 18, 0) };
        var place = new RadioButton { Content = "Place in this document", IsChecked = existing?.Anchor != null, GroupName = "kind", Margin = new Thickness(0, 4, 0, 0) };
        var kinds = new StackPanel { Orientation = Orientation.Horizontal };
        kinds.Children.Add(web);
        kinds.Children.Add(place);
        dialog.Body.Children.Add(kinds);

        var url = dialog.Field("Address", existing?.Url ?? "", "https://example.com or name@example.com");
        var targets = new List<(string Label, string? Bookmark, Paragraph? Heading)>();
        foreach (var h in headings) targets.Add(("Heading: " + Trim(h.Text), null, h.Para));
        foreach (var b in bookmarks) targets.Add(("Bookmark: " + b, b, null));
        var selectedTarget = existing?.Anchor != null ? Math.Max(0, targets.FindIndex(t => t.Bookmark == existing.Anchor)) : 0;
        var target = dialog.Choices("Place", targets.Select(t => t.Label).DefaultIfEmpty("(no headings or bookmarks yet)"), selectedTarget);
        TextBox? text = null;
        if (existing == null && selectedText.Length == 0) text = dialog.Field("Text to show", "", "Leave empty to show the address");
        var tip = dialog.Field("Tooltip (optional)", existing?.Tooltip ?? "");
        void Sync()
        {
            url.IsEnabled = web.IsChecked == true;
            target.IsEnabled = place.IsChecked == true && targets.Count > 0;
        }
        web.Checked += (_, _) => Sync();
        place.Checked += (_, _) => Sync();
        Sync();
        LinkResult? result = null;
        dialog.AddButton(existing != null ? "Save" : "Insert", primary: true, onClick: () =>
        {
            if (web.IsChecked == true)
            {
                var address = url.Text.Trim();
                if (address.Length == 0)
                {
                    url.Focus();
                    return false;
                }
                if (address.Contains('@') && !address.Contains(':')) address = "mailto:" + address;
                else if (!address.Contains("://") && !address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) address = "https://" + address;
                result = new LinkResult { Url = address, Text = Text(text, address), Tooltip = Empty(tip.Text) };
                return true;
            }
            if (targets.Count == 0 || target.SelectedIndex < 0) return false;
            var t = targets[target.SelectedIndex];
            result = new LinkResult { Anchor = t.Bookmark, HeadingTarget = t.Heading, Text = Text(text, StripPrefix(t.Label)), Tooltip = Empty(tip.Text) };
            return true;
        });
        if (existing != null)
            dialog.AddButton("Remove link", onClick: () =>
            {
                result = new LinkResult { Remove = true };
                return true;
            });
        dialog.AddButton("Cancel", isCancel: true);
        dialog.Loaded += (_, _) => (web.IsChecked == true ? url : (Control)target).Focus();
        dialog.Run(owner);
        return result;
    }

    private static string Trim(string s) => s.Length > 60 ? s.Substring(0, 57) + "…" : s;

    private static string StripPrefix(string label) => label.Substring(label.IndexOf(": ", StringComparison.Ordinal) + 2);

    private static string? Empty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Text(TextBox? box, string fallback) => box == null ? null : string.IsNullOrWhiteSpace(box.Text) ? fallback : box.Text.Trim();
}

/// <summary>A caption: label (Figure, Table, Equation), text and position.</summary>
public static class CaptionDialog
{
    public static (string Label, string Text, bool Above)? Show(Window owner, string label, bool inTable)
    {
        var dialog = new InkDialog("Insert caption", "Captions are numbered automatically, and cross-references and lists of figures can point to them.", 460);
        var labels = new[] { "Figure", "Table", "Equation" };
        var choice = dialog.Choices("Label", labels, Math.Max(0, Array.IndexOf(labels, label)));
        var text = dialog.Field("Caption text", "", "e.g. Sales by region, 2026");
        var position = dialog.Choices("Position", new[] { "Below the selection", "Above the selection" }, inTable ? 1 : 0);
        (string, string, bool)? result = null;
        dialog.AddButton("Insert", primary: true, onClick: () =>
        {
            result = ((string)choice.SelectedItem, text.Text.Trim(), position.SelectedIndex == 1);
            return true;
        });
        dialog.AddButton("Cancel", isCancel: true);
        dialog.Loaded += (_, _) => text.Focus();
        dialog.Run(owner);
        return result;
    }
}

/// <summary>A cross-reference to a heading or caption: its text, its number, its page or "above / below".</summary>
public static class CrossReferenceDialog
{
    public static (Paragraph Target, string Show)? Show(Window owner, IReadOnlyList<(string Kind, string Text, Paragraph Para)> targets)
    {
        var dialog = new InkDialog("Insert cross-reference", "The reference updates when the target moves or is renumbered (F9 updates every field).", 520);
        dialog.Label("Refer to", 4);
        var list = new ListBox { Height = 220 };
        AutomationProperties.SetName(list, "Targets");
        foreach (var t in targets)
        {
            var row = new DockPanel { Margin = new Thickness(2, 3, 2, 3) };
            var kind = new TextBlock { Text = t.Kind, Width = 80 };
            kind.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
            row.Children.Add(kind);
            row.Children.Add(new TextBlock { Text = t.Text, TextTrimming = TextTrimming.CharacterEllipsis });
            list.Items.Add(new ListBoxItem { Content = row, Tag = t.Para });
        }
        list.SelectedIndex = 0;
        dialog.Body.Children.Add(list);
        var show = dialog.Choices("Show", new[] { "Its text (or label and number)", "Its page number", "“above” or “below”" });
        (Paragraph, string)? result = null;
        dialog.AddButton("Insert", primary: true, onClick: () =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: Paragraph p }) return false;
            result = (p, show.SelectedIndex switch { 1 => "page", 2 => "position", _ => "number" });
            return true;
        });
        dialog.AddButton("Cancel", isCancel: true);
        dialog.Loaded += (_, _) => list.Focus();
        dialog.Run(owner);
        return result;
    }
}

/// <summary>Document properties: title, subject, author, keywords, description, and file details.</summary>
public static class PropertiesDialog
{
    public static void Show(EditorView view)
    {
        var doc = view.Session.Document;
        var p = doc.Properties;
        var dialog = new InkDialog("Document properties", "These are saved in the file. Windows search and other programs show them.", 500);
        var title = dialog.Field("Title", p.Title ?? "", null, 4);
        var subject = dialog.Field("Subject", p.Subject ?? "");
        var author = dialog.Field("Author", p.Creator ?? "");
        var keywords = dialog.Field("Keywords", p.Keywords ?? "", "Separated by commas");
        var description = dialog.Field("Description", p.Description ?? "");
        description.AcceptsReturn = true;
        description.TextWrapping = TextWrapping.Wrap;
        description.MinHeight = 64;
        var info = new TextBlock { Margin = new Thickness(0, 16, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        info.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        var lines = new List<string>();
        if (view.Session.FilePath is { } path)
        {
            lines.Add(path);
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists) lines.Add($"{(fi.Length / 1024.0).ToString("N0", CultureInfo.CurrentCulture)} KB · saved {fi.LastWriteTime.ToString("g", CultureInfo.CurrentCulture)}");
            }
            catch (IOException)
            {
                // no details
            }
        }
        if (p.Created is { } created) lines.Add("Created " + created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + (p.Creator != null ? " by " + p.Creator : ""));
        if (p.LastModifiedBy != null) lines.Add("Last changed by " + p.LastModifiedBy);
        info.Text = string.Join("\n", lines);
        dialog.Body.Children.Add(info);
        dialog.AddButton("Save", primary: true, onClick: () =>
        {
            static string? N(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            view.Run(() => view.Editor.Edit("Document properties", t =>
            {
                t.TouchProperties();
                p.Title = N(title.Text);
                p.Subject = N(subject.Text);
                p.Creator = N(author.Text);
                p.Keywords = N(keywords.Text);
                p.Description = N(description.Text);
                return null;
            }));
            return true;
        });
        dialog.AddButton("Cancel", isCancel: true);
        dialog.Loaded += (_, _) => title.Focus();
        dialog.Run(view.Window);
    }
}
