using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Inkwell.Core.Editing;
using Inkwell.Core.Model;
using Inkwell.Core.Layout;

namespace Inkwell.Views;

/// <summary>The status bar: live word counts (document and selection), reading time and the word goal ring.</summary>
public partial class EditorView
{
    private const string GoalVariable = "InkwellWordGoal";
    private DocumentStats? _stats;
    private int _lastCountVersion = -1;
    private DocumentStatistics? _lastCount;

    /// <summary>Counting with a per-paragraph cache (only changed paragraphs are counted again).</summary>
    public DocumentStats Stats => _stats ??= new DocumentStats();

    private DocumentStatistics CountDocument(bool includeNotes = true)
    {
        if (includeNotes && _lastCount != null && _lastCountVersion == Editor.History.Version) return _lastCount;
        var count = Stats.Count(Session.Document, new StatsOptions { IncludeNotes = includeNotes, Pages = Layout.View.Mode == Core.Layout.ViewMode.Pages ? Layout.Pages.Count : null });
        if (includeNotes)
        {
            _lastCount = count;
            _lastCountVersion = Editor.History.Version;
        }
        return count;
    }

    private DocumentStatistics? CountSelection()
    {
        if (Editor.Selection.IsEmpty) return null;
        var ranges = Editor.SelectedRanges().Select(r => new StatsRange(r.Para, r.Start, r.End)).ToList();
        return Stats.Count(ranges, new StatsOptions { IncludeNotes = false });
    }

    public int? WordGoal
    {
        get
        {
            var value = Session.Document.Settings.Variable(GoalVariable);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
        }
    }

    private void UpdateWordStatus()
    {
        var doc = CountDocument();
        var sel = CountSelection();
        var words = doc.Words.ToString("N0", CultureInfo.CurrentCulture);
        WordStatus.Content = sel != null
            ? $"{sel.Words.ToString("N0", CultureInfo.CurrentCulture)} of {words} words"
            : $"{words} {(doc.Words == 1 ? "word" : "words")}";
        var reading = doc.ReadingTime;
        WordStatus.ToolTip = $"{doc.Characters.ToString("N0", CultureInfo.CurrentCulture)} characters · about {Minutes(reading)} to read. Click for details (Ctrl+Shift+G).";
        UpdateGoal(doc.Words);
    }

    private static string Minutes(TimeSpan time)
    {
        var minutes = (int)Math.Ceiling(time.TotalMinutes);
        if (time.TotalSeconds < 1) return "no time";
        return minutes <= 1 ? "1 minute" : minutes.ToString(CultureInfo.CurrentCulture) + " minutes";
    }

    private void UpdateGoal(int words)
    {
        var goal = WordGoal;
        if (goal == null)
        {
            GoalRing.Visibility = Visibility.Collapsed;
            GoalText.Visibility = Visibility.Collapsed;
            return;
        }
        GoalRing.Visibility = Visibility.Visible;
        GoalText.Visibility = Visibility.Visible;
        var fraction = Math.Min(1.0, words / (double)goal.Value);
        GoalText.Text = fraction >= 1 ? "Goal reached" : $"{Math.Floor(fraction * 100).ToString(CultureInfo.CurrentCulture)}% of {goal.Value.ToString("N0", CultureInfo.CurrentCulture)}";
        GoalRing.ToolTip = $"Word goal: {words.ToString("N0", CultureInfo.CurrentCulture)} of {goal.Value.ToString("N0", CultureInfo.CurrentCulture)} words";
        GoalArc.Data = Arc(fraction, 9, 7.75);
        GoalArc.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, fraction >= 1 ? "Status.Success" : "Accent.Fill");
    }

    /// <summary>A clockwise arc from the top for the progress ring.</summary>
    private static Geometry Arc(double fraction, double center, double radius)
    {
        if (fraction <= 0) return Geometry.Empty;
        if (fraction >= 0.9999) return new EllipseGeometry(new Point(center, center), radius, radius);
        var angle = fraction * 2 * Math.PI;
        var start = new Point(center, center - radius);
        var end = new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private void OnWordCount(object sender, RoutedEventArgs e) => ShowWordCount();

    /// <summary>The word count: with and without notes, the selection, characters, paragraphs, pages, reading and speaking time.</summary>
    public void ShowWordCount()
    {
        var all = CountDocument(includeNotes: true);
        var body = CountDocument(includeNotes: false);
        var sel = CountSelection();
        string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);
        var rows = new List<(string, string)>();
        if (sel != null)
        {
            rows.Add(("Selected words", N(sel.Words)));
            rows.Add(("Selected characters", N(sel.CharactersWithSpaces)));
        }
        rows.Add(("Words", N(all.Words)));
        if (all.Words != body.Words) rows.Add(("Words without footnotes and endnotes", N(body.Words)));
        rows.Add(("Characters (no spaces)", N(all.Characters)));
        rows.Add(("Characters (with spaces)", N(all.CharactersWithSpaces)));
        rows.Add(("Paragraphs", N(all.Paragraphs)));
        if (Layout.View.Mode == Core.Layout.ViewMode.Pages) rows.Add(("Pages", N(Layout.Pages.Count)));
        rows.Add(("Reading time", "about " + Minutes(all.ReadingTime)));
        rows.Add(("Speaking time", "about " + Minutes(all.SpeakingTime)));
        if (WordGoal is { } goal) rows.Add(("Word goal", $"{N(all.Words)} of {N(goal)}"));
        StatsDialog.Show(_window, rows, SetWordGoal);
    }

    /// <summary>A word goal for this document (kept in the document), with a progress ring in the status bar.</summary>
    public void SetWordGoal()
    {
        var current = WordGoal?.ToString(CultureInfo.CurrentCulture) ?? "1000";
        var answer = InputDialog.Ask(_window, "Word goal", "How many words are you aiming for? Leave it empty to remove the goal.", current, "Set goal");
        if (answer == null) return;
        answer = answer.Trim().Replace(",", "").Replace(" ", "");
        int? goal = null;
        if (answer.Length > 0)
        {
            if (!int.TryParse(answer, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) || n <= 0 || n > 10_000_000)
            {
                _window.ShowMessage("Type a number of words, like 1500.", 4, warning: true);
                return;
            }
            goal = n;
        }
        Run(() => Editor.Edit(goal == null ? "Remove word goal" : "Word goal", t =>
        {
            t.TouchSettings();
            Session.Document.Settings.SetVariable(GoalVariable, goal?.ToString(CultureInfo.InvariantCulture));
            return null;
        }));
        UpdateWordStatus();
    }
}

public partial class EditorView
{
    private int _commentVersion = -1;
    private RevisionView _commentView;

    /// <summary>Soft highlights on commented text (open comments), unless markup is hidden.</summary>
    private void UpdateCommentHighlights()
    {
        var view = Layout.View.Revisions;
        if (_commentVersion == Editor.History.Version && _commentView == view) return;
        _commentVersion = Editor.History.Version;
        _commentView = view;
        Doc.CommentHighlights.Clear();
        var comments = Session.Document.Comments;
        if (comments.Count == 0 || view is RevisionView.NoMarkup or RevisionView.Original)
        {
            Doc.Redraw();
            return;
        }
        var open = new HashSet<int>(comments.Where(c => !c.Done && c.ParentId == null).Select(c => c.Id));
        var starts = new Dictionary<int, DocPos>();
        var color = Color.FromArgb(0x48, 0xF5, 0xC2, 0x3B);
        foreach (var story in Session.Document.Stories)
        {
            if (story is Comment) continue;
            foreach (var p in story.Paragraphs)
            {
                var pos = 0;
                foreach (var i in p.Inlines)
                {
                    if (i is CommentMarker m && open.Contains(m.Id))
                    {
                        if (m.Kind == CommentMarkerKind.RangeStart) starts[m.Id] = new DocPos(p, pos);
                        else if (m.Kind == CommentMarkerKind.RangeEnd && starts.TryGetValue(m.Id, out var s))
                        {
                            Doc.CommentHighlights.Add(new Editing.HighlightRange(s, new DocPos(p, pos), color));
                            starts.Remove(m.Id);
                        }
                    }
                    pos += i.Length;
                }
            }
        }
        Doc.Redraw();
    }
}
