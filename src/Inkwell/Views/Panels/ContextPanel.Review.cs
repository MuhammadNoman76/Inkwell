using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Inkwell.Core.Editing;
using Inkwell.Core.Layout;
using Inkwell.Core.Model;
using Inkwell.Core.Styles;
using Inkwell.Controls;

namespace Inkwell.Views.Panels;

/// <summary>Review (tracked changes and comments) and Reveal formatting with the style consistency check.</summary>
public sealed partial class ContextPanel
{
    private Comment? _editingComment;
    private bool _editingIsNew;
    private bool _showResolved;
    private List<ConsistencyIssue>? _issues;
    private int _issueVersion = -1;

    /// <summary>Opens a comment for typing in the review panel (a new comment, or editing one).</summary>
    public void EditComment(Comment comment)
    {
        _editingComment = comment;
        _editingIsNew = comment.Text.Trim().Length == 0;
        _shown = (PanelMode)(-1);
        _mode = PanelMode.Review;
        Refresh();
    }

    // ------------------------------------------------------------------ review

    private void BuildReview()
    {
        var track = Section("Track changes", "When on, every insertion, deletion and formatting change is marked with your name, for others to accept or reject.");
        track.Children.Add(Switch("Track changes", Editor.TrackChanges, on => Editor.SetTrackChanges(on), Shortcuts.TrackChanges.Text));
        var view = new ComboBox { Width = 170, HorizontalAlignment = HorizontalAlignment.Left };
        var views = new[] { (RevisionView.AllMarkup, "All markup"), (RevisionView.Simple, "Simple markup"), (RevisionView.NoMarkup, "No markup"), (RevisionView.Original, "Original") };
        foreach (var v in views) view.Items.Add(v.Item2);
        view.SelectedIndex = Math.Max(0, Array.FindIndex(views, v => v.Item1 == _editor.Layout.View.Revisions));
        view.SelectionChanged += (_, _) =>
        {
            if (view.SelectedIndex >= 0) _editor.SetRevisionView(views[view.SelectedIndex].Item1);
        };
        track.Children.Add(Row("Show", view, 64));

        var revisions = Editor.Revisions();
        var changes = Section("Changes", revisions.Count == 0 ? "No tracked changes." : $"{revisions.Count} {(revisions.Count == 1 ? "change" : "changes")} by {string.Join(", ", revisions.Select(r => r.Author).Distinct())}.");
        if (revisions.Count > 0)
        {
            var authors = revisions.Select(r => r.Author).Distinct().ToList();
            string? author = null;
            if (authors.Count > 1)
            {
                var who = new ComboBox { Width = 170, HorizontalAlignment = HorizontalAlignment.Left };
                who.Items.Add("Everyone");
                foreach (var a in authors) who.Items.Add(a);
                who.SelectedIndex = 0;
                who.SelectionChanged += (_, _) => author = who.SelectedIndex <= 0 ? null : authors[who.SelectedIndex - 1];
                changes.Children.Add(Row("From", who, 64));
            }
            changes.Children.Add(Commands(2,
                Command("Accept all", "Icon.Accept", () => Editor.AcceptOrRejectAll(true, author)),
                Command("Reject all", "Icon.Reject", () => Editor.AcceptOrRejectAll(false, author)),
                Command("Previous", "Icon.ArrowUp", () => GoToChange(-1)),
                Command("Next", "Icon.ArrowDown", () => GoToChange(1))));
            foreach (var r in revisions.Take(150)) changes.Children.Add(ChangeCard(r));
            if (revisions.Count > 150) changes.Children.Add(Note($"…and {revisions.Count - 150} more."));
        }

        var all = Doc.Comments.ToList();
        var roots = all.Where(c => c.ParentId == null || all.All(x => x.Id != c.ParentId)).ToList();
        var open = roots.Count(c => !c.Done);
        var comments = Section("Comments", roots.Count == 0 ? $"No comments. Select text and press {Shortcuts.Comment.Text} to add one." : $"{open} open, {roots.Count - open} resolved.");
        comments.Children.Add(Command("New comment", "Icon.CommentAdd", _editor.AddComment, Shortcuts.Comment.Text));
        if (roots.Any(c => c.Done))
        {
            var toggle = new CheckBox { Content = "Show resolved comments", IsChecked = _showResolved, Margin = new Thickness(0, 6, 0, 6) };
            toggle.Click += (_, _) =>
            {
                _showResolved = toggle.IsChecked == true;
                _shown = (PanelMode)(-1);
                Refresh();
            };
            comments.Children.Add(toggle);
        }
        foreach (var c in roots.Where(c => !c.Done || _showResolved || ReferenceEquals(c, _editingComment)))
            comments.Children.Add(CommentCard(c, all.Where(x => x.ParentId == c.Id).OrderBy(x => x.DateUtc ?? x.Date).ToList()));
    }

    private void GoToChange(int direction)
    {
        var change = Editor.FindChange(direction);
        if (change != null) _editor.GoTo(new Selection(new DocPos(change.Paragraph, change.Start), new DocPos(change.Paragraph, change.End)));
    }

    private Border Card(UIElement content, bool highlight = false)
    {
        var card = new Border { Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 6, 0, 0), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(highlight ? 2 : 1), Child = content };
        card.SetResourceReference(BackgroundProperty, "Card.Background");
        card.SetResourceReference(BorderBrushProperty, highlight ? "Accent.Fill" : "Card.Stroke");
        return card;
    }

    private FrameworkElement ChangeCard(RevisionItem r)
    {
        var stack = new StackPanel();
        var head = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(SmallIcon("Icon.Accept", "Accept", () => ResolveChange(r, true)));
        buttons.Children.Add(SmallIcon("Icon.Reject", "Reject", () => ResolveChange(r, false)));
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        var title = new TextBlock { Text = r.Kind, FontWeight = FontWeights.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(title);
        stack.Children.Add(head);
        var who = new TextBlock { Text = r.Author + (r.Date is { } d ? " · " + When(d) : ""), FontSize = 11.5 };
        who.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        stack.Children.Add(who);
        if (r.Text.Trim().Length > 0)
        {
            var text = new TextBlock { Text = r.Text.Length > 140 ? r.Text.Substring(0, 137) + "…" : r.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontSize = 12.5 };
            if (r.Kind.StartsWith("Deleted", StringComparison.Ordinal) || r.Kind.StartsWith("Moved from", StringComparison.Ordinal)) text.TextDecorations = TextDecorations.Strikethrough;
            stack.Children.Add(text);
        }
        var card = Card(stack);
        card.Cursor = Cursors.Hand;
        AutomationProperties.SetName(card, $"{r.Kind} by {r.Author}: {r.Text}");
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject o && FindParent<Button>(o) != null) return;
            if (r.Paragraph.Parent != null) _editor.GoTo(new Selection(new DocPos(r.Paragraph, Math.Min(r.Start, r.Paragraph.Length)), new DocPos(r.Paragraph, Math.Min(r.End, r.Paragraph.Length))));
        };
        return card;
    }

    private void ResolveChange(RevisionItem r, bool accept)
    {
        if (r.Paragraph.Parent == null) return;
        var p = r.Paragraph;
        Editor.Selection = r.Start == r.End ? new Selection(new DocPos(p, Math.Min(r.Start, p.Length))) : new Selection(new DocPos(p, r.Start), new DocPos(p, Math.Min(r.End, p.Length)));
        _editor.Run(() => Editor.AcceptOrRejectAtSelection(accept));
        _shown = (PanelMode)(-1);
        Refresh();
    }

    private Button SmallIcon(string icon, string tooltip, Action action)
    {
        var b = new Button { Width = 28, Height = 28, ToolTip = tooltip };
        b.SetResourceReference(StyleProperty, "IconButton");
        if (TryFindResource(icon) is Geometry g) Ui.SetIcon(b, g);
        AutomationProperties.SetName(b, tooltip);
        b.Click += (_, _) => action();
        return b;
    }

    private static string When(DateTime date)
    {
        var local = date.Kind == DateTimeKind.Utc ? date.ToLocalTime() : date;
        var span = DateTime.Now - local;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return (int)span.TotalMinutes + " min ago";
        if (local.Date == DateTime.Today) return local.ToString("t", CultureInfo.CurrentCulture);
        return local.ToString(local.Year == DateTime.Now.Year ? "d MMM, t" : "d MMM yyyy", CultureInfo.CurrentCulture);
    }

    private FrameworkElement CommentCard(Comment comment, List<Comment> replies)
    {
        var stack = new StackPanel();
        AddCommentBody(stack, comment, isReply: false);
        foreach (var reply in replies)
        {
            var r = new StackPanel { Margin = new Thickness(14, 10, 0, 0) };
            AddCommentBody(r, reply, isReply: true);
            stack.Children.Add(r);
        }
        // Reply box.
        if (!comment.Done && !ReferenceEquals(_editingComment, comment))
        {
            var reply = new TextBox { Margin = new Thickness(0, 10, 0, 0), AcceptsReturn = false };
            Ui.SetPlaceholder(reply, "Reply");
            AutomationProperties.SetName(reply, "Reply to " + comment.Author);
            reply.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || reply.Text.Trim().Length == 0) return;
                var text = reply.Text.Trim();
                e.Handled = true;
                _editor.Run(() => Editor.AddComment(text, comment));
                _shown = (PanelMode)(-1);
                Dispatcher.BeginInvoke(new Action(Refresh), System.Windows.Threading.DispatcherPriority.Background);
            };
            stack.Children.Add(reply);
        }
        var card = Card(stack, ReferenceEquals(_editingComment, comment));
        if (comment.Done) card.Opacity = 0.7;
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject o && (FindParent<Button>(o) != null || FindParent<TextBox>(o) != null)) return;
            if (Editor.FindCommentRange(comment.Id) is { } range) _editor.GoTo(new Selection(range.Start, range.End));
        };
        return card;
    }

    private void AddCommentBody(StackPanel stack, Comment comment, bool isReply)
    {
        var head = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (!isReply)
            buttons.Children.Add(SmallIcon(comment.Done ? "Icon.ArrowUndo" : "Icon.Accept", comment.Done ? "Reopen" : "Resolve", () =>
            {
                _editor.Run(() => Editor.ResolveComment(comment, !comment.Done));
                _shown = (PanelMode)(-1);
                Refresh();
            }));
        buttons.Children.Add(SmallIcon("Icon.Edit", "Edit", () => EditComment(comment)));
        buttons.Children.Add(SmallIcon("Icon.Delete", isReply ? "Delete reply" : "Delete comment and replies", () =>
        {
            _editor.Run(() => Editor.DeleteComment(comment));
            _shown = (PanelMode)(-1);
            Refresh();
        }));
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        var avatar = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        avatar.Background = new SolidColorBrush(AuthorColor(comment.Author));
        avatar.Child = new TextBlock { Text = Initials(comment), Foreground = Brushes.White, FontSize = 10.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(avatar);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = comment.Author.Length > 0 ? comment.Author : "Unknown", FontWeight = FontWeights.SemiBold, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
        var when = new TextBlock { Text = (comment.DateUtc ?? comment.Date) is { } d ? When(d) + (comment.Done ? " · Resolved" : "") : comment.Done ? "Resolved" : "", FontSize = 11 };
        when.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        names.Children.Add(when);
        head.Children.Add(names);
        stack.Children.Add(head);

        if (ReferenceEquals(_editingComment, comment))
        {
            var box = new TextBox { Text = comment.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, Margin = new Thickness(0, 8, 0, 0) };
            Ui.SetPlaceholder(box, $"Write a comment… ({Shortcuts.Local.PostComment.Text} to post)");
            AutomationProperties.SetName(box, "Comment text");
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            var post = new Button { Content = "Post", Padding = new Thickness(14, 3, 14, 4) };
            post.SetResourceReference(StyleProperty, "AccentButton");
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 3, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
            void Finish(bool save)
            {
                var text = box.Text.Trim();
                var isNew = _editingIsNew;
                _editingComment = null;
                if (save && text.Length > 0) _editor.Run(() => Editor.SetCommentText(comment, text));
                else if (isNew && text.Length == 0) _editor.Run(() => Editor.DeleteComment(comment));
                _shown = (PanelMode)(-1);
                Refresh();
                _editor.FocusDocument();
            }
            post.Click += (_, _) => Finish(true);
            cancel.Click += (_, _) => Finish(false);
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    Finish(true);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    Finish(false);
                    e.Handled = true;
                }
            };
            row.Children.Add(post);
            row.Children.Add(cancel);
            stack.Children.Add(box);
            stack.Children.Add(row);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                box.Focus();
                box.CaretIndex = box.Text.Length;
            }), System.Windows.Threading.DispatcherPriority.Input);
            return;
        }
        var body = new TextBlock { Text = comment.Text.Trim(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(34, 4, 0, 0), FontSize = 13 };
        stack.Children.Add(body);
    }

    private static string Initials(Comment c)
    {
        if (!string.IsNullOrWhiteSpace(c.Initials)) return c.Initials.Length > 3 ? c.Initials.Substring(0, 3) : c.Initials;
        var parts = c.Author.Split(new[] { ' ', '.', '_' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "?" : new string(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])).ToArray());
    }

    /// <summary>A stable colour per author (the same in every document).</summary>
    private static Color AuthorColor(string author)
    {
        Color[] palette = { Color.FromRgb(0x2F, 0x5D, 0xA8), Color.FromRgb(0x8E, 0x44, 0xAD), Color.FromRgb(0x1E, 0x84, 0x49), Color.FromRgb(0xC0, 0x56, 0x1B), Color.FromRgb(0xB0, 0x30, 0x60), Color.FromRgb(0x0E, 0x7C, 0x86) };
        var h = 0;
        foreach (var ch in author) h = unchecked(h * 31 + ch);
        return palette[Math.Abs(h % palette.Length)];
    }

    private static T? FindParent<T>(DependencyObject? o) where T : DependencyObject
    {
        while (o != null)
        {
            if (o is T t) return t;
            o = o is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o);
        }
        return null;
    }

    // ------------------------------------------------------------------ reveal formatting

    private void BuildReveal()
    {
        var pos = Editor.Selection.Active;
        var items = RevealFormatting.Explain(Editor, pos);
        var directCount = items.Count(i => i.IsDirect);
        var intro = Section("Reveal formatting", "Why the text at the caret looks the way it does: each value and where it comes from. Accent lines are direct formatting (set on the text itself, not by the style).");
        foreach (var group in new[] { "Paragraph", "Text" })
        {
            var g = Section(group == "Text" ? "Text" : "Paragraph");
            foreach (var item in items.Where(i => i.Group == group)) g.Children.Add(RevealRow(item));
        }
        var actions = Section("Tidy up", directCount == 0 ? "No direct formatting here: it all comes from the style." : null);
        actions.Children.Add(Commands(1,
            Command("Clear character formatting", "Icon.TextClearFormatting", Editor.ClearCharacterFormatting, Shortcuts.ClearCharacterFormatting.Text),
            Command("Clear all formatting (back to the style)", "Icon.TextClearFormatting", Editor.ClearFormatting),
            Command("Update the style to match this", "Icon.TextEditStyle", Editor.UpdateStyleFromSelection)));

        var check = Section("Style consistency", "Formatting that fights the styles across the whole document.");
        if (_issues == null || _issueVersion != Editor.History.Version)
        {
            try
            {
                _issues = StyleConsistency.Check(Editor);
            }
            catch (Exception ex)
            {
                Services.Log.Error(ex, "Style consistency");
                _issues = new List<ConsistencyIssue>();
            }
            _issueVersion = Editor.History.Version;
        }
        if (_issues.Count == 0) check.Children.Add(Note("✓ Everything follows the styles."));
        foreach (var issue in _issues)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = issue.Title, FontWeight = FontWeights.SemiBold, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
            var detail = new TextBlock { Text = issue.Detail, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 6) };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            stack.Children.Add(detail);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var show = new Button { Content = "Show", Padding = new Thickness(10, 2, 10, 3) };
            var shown = 0;
            show.Click += (_, _) =>
            {
                var live = issue.Paragraphs.Where(p => p.Parent != null).ToList();
                if (live.Count == 0) return;
                var p = live[shown++ % live.Count];
                _editor.GoTo(new Selection(new DocPos(p, 0), new DocPos(p, p.Length)));
            };
            var fix = new Button { Content = issue.FixLabel, Padding = new Thickness(10, 2, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            fix.SetResourceReference(StyleProperty, "AccentButton");
            fix.Click += (_, _) =>
            {
                _editor.Run(() => issue.Fix(Editor));
                _shown = (PanelMode)(-1);
                Refresh();
            };
            row.Children.Add(show);
            row.Children.Add(fix);
            stack.Children.Add(row);
            check.Children.Add(Card(stack));
        }
    }

    private FrameworkElement RevealRow(RevealItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        if (item.IsDirect)
        {
            var bar = new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 2, 0, 2) };
            bar.SetResourceReference(BackgroundProperty, "Accent.Fill");
            grid.Children.Add(bar);
        }
        var name = new TextBlock { Text = item.Name, Margin = new Thickness(8, 0, 6, 0), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var values = new StackPanel();
        values.Children.Add(new TextBlock { Text = item.Value, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
        var source = new TextBlock { Text = item.Source, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        source.SetResourceReference(TextBlock.ForegroundProperty, item.IsDirect ? "Text.Accent" : "Text.Tertiary");
        values.Children.Add(source);
        Grid.SetColumn(values, 2);
        grid.Children.Add(values);
        AutomationProperties.SetName(grid, $"{item.Name}: {item.Value}, from {item.Source}");
        return grid;
    }
}
