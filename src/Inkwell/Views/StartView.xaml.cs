using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Inkwell.Controls;
using Inkwell.Core.Editing;
using Inkwell.Core.IO;
using Inkwell.Core.Templates;
using Inkwell.Services;

namespace Inkwell.Views;

/// <summary>The start screen: templates, recent documents with thumbnails, recovered work and the tour of what Inkwell fixes.</summary>
public partial class StartView
{
    private readonly MainWindow _window;
    private static readonly Dictionary<string, BitmapSource> s_templatePreviews = new();
    private const double PageRatio = 0.7727; // US Letter / A4 portrait, close enough for cards

    public StartView(MainWindow window)
    {
        _window = window;
        InitializeComponent();
        BuildTemplates();
        BuildTour();
        PreviewKeyDown += OnKey;
    }

    public void FocusFirst()
    {
        if (TemplateCards.Items.Count > 0 && TemplateCards.Items[0] is Button b && !MainWindow.IsTestOffscreen) b.Focus();
    }

    public void Refresh()
    {
        BuildRecent();
        BuildRecovery();
        var hidden = AppSettings.Current.TourDismissed;
        TourHeader.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        TourCards.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (e.Key == Key.O)
        {
            OnOpen(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.N)
        {
            NewFromTemplate(TemplateCatalog.All[0]);
            e.Handled = true;
        }
        else if (e.Key == Key.OemComma)
        {
            OnSettings(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ cards

    /// <summary>A card: a page picture with a shadow, a title and a caption.</summary>
    public static Button MakeCard(ImageSource? image, string title, string caption, string automationName, out Border picture)
    {
        var frame = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
        };
        frame.SetResourceReference(Border.BorderBrushProperty, "Thumb.Stroke");
        if (image != null) SetCardImage(frame, image);
        picture = frame;
        var shadow = new Border
        {
            CornerRadius = new CornerRadius(6),
            Child = new AspectPanel { Ratio = PageRatio, Children = { frame } },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = 0.14 },
        };
        var name = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 10, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        var sub = new TextBlock { Text = caption, FontSize = 12, Margin = new Thickness(2, 2, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        var stack = new StackPanel();
        stack.Children.Add(shadow);
        stack.Children.Add(name);
        if (caption.Length > 0) stack.Children.Add(sub);
        var button = new Button
        {
            Content = stack,
            Padding = new Thickness(8, 8, 8, 10),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            ToolTip = automationName,
        };
        button.SetResourceReference(StyleProperty, "CardButton");
        System.Windows.Automation.AutomationProperties.SetName(button, automationName);
        return button;
    }

    public static void SetCardImage(Border frame, ImageSource image)
    {
        var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        brush.Freeze();
        frame.Background = brush;
    }

    private static bool UseLetter()
    {
        var paper = AppSettings.Current.DefaultPaper;
        return paper == "letter" || (paper == "auto" && TemplateCatalog.RegionUsesLetter());
    }

    private void BuildTemplates()
    {
        TemplateCards.Items.Clear();
        foreach (var template in TemplateCatalog.All)
        {
            var card = MakeCard(null, template.Name, template.Description, "New " + template.Name.ToLowerInvariant(), out var frame);
            card.Click += (_, _) => NewFromTemplate(template);
            TemplateCards.Items.Add(card);
            var t = template;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!s_templatePreviews.TryGetValue(t.Id, out var preview))
                    {
                        var design = t.Id == "blank" ? DocumentDesigns.Find(AppSettings.Current.DefaultDesign) : null;
                        preview = Thumbnails.RenderFirstPage(t.Build(false, design), 360);
                        s_templatePreviews[t.Id] = preview;
                    }
                    SetCardImage(frame, preview);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Template preview " + t.Id);
                }
            }), DispatcherPriority.Background);
        }
    }

    private void NewFromTemplate(DocumentTemplate template)
    {
        var design = template.Id == "blank" ? DocumentDesigns.Find(AppSettings.Current.DefaultDesign) : null;
        var doc = template.Build(UseLetter(), design);
        doc.Properties.Creator = AppSettings.Current.Author;
        var session = new DocumentSession(doc);
        // Templates start with the caret at the end of the title; the blank page in its first paragraph.
        App.Current.OpenSession(session, _window);
    }

    private void BuildRecent()
    {
        RecentCards.Items.Clear();
        var recent = AppSettings.Current.Recent.OrderByDescending(r => r.Pinned).ThenByDescending(r => r.LastOpened).Take(18).ToList();
        NoRecent.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in recent)
        {
            var path = item.Path;
            var exists = File.Exists(path);
            var caption = (item.Pinned ? "Pinned · " : "") + When(item.LastOpened);
            var card = MakeCard(null, Path.GetFileNameWithoutExtension(path), exists ? caption : "Not found", "Open " + Path.GetFileName(path), out var image);
            card.ToolTip = path;
            card.Opacity = exists ? 1 : 0.55;
            card.Click += (_, _) => App.Current.OpenPath(path, _window);
            card.ContextMenu = RecentMenu(item);
            RecentCards.Items.Add(card);
            if (exists) LoadThumbnail(path, image);
        }
    }

    private void LoadThumbnail(string path, Border target)
    {
        var cached = Thumbnails.Cached(path);
        if (cached != null)
        {
            SetCardImage(target, cached);
            return;
        }
        // Render in the background order: read the file on a worker thread, lay out the first page when idle.
        Task.Run(() =>
        {
            try
            {
                return DocumentFiles.Open(path).Document;
            }
            catch
            {
                return null;
            }
        }).ContinueWith(t =>
        {
            if (t.Result == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var bitmap = Thumbnails.RenderFirstPage(t.Result, 360);
                    Thumbnails.Store(path, bitmap);
                    SetCardImage(target, bitmap);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Thumbnail " + path);
                }
            }), DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private ContextMenu RecentMenu(RecentDocument item)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, string icon, Action action)
        {
            var mi = new MenuItem { Header = header };
            if (TryFindResource(icon) is Geometry g) Ui.SetIcon(mi, g);
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
            return mi;
        }
        Item("Open", "Icon.Open", () => App.Current.OpenPath(item.Path, _window));
        Item("Open in a new window", "Icon.WindowNew", () => App.Current.OpenPath(item.Path, null));
        Item("Show in folder", "Icon.FolderOpen", () => ShowInFolder(item.Path));
        menu.Items.Add(new Separator());
        Item(item.Pinned ? "Unpin" : "Pin to the top", item.Pinned ? "Icon.PinOff" : "Icon.Pin", () =>
        {
            AppSettings.Update(s =>
            {
                var r = s.Recent.FirstOrDefault(x => string.Equals(x.Path, item.Path, StringComparison.OrdinalIgnoreCase));
                if (r != null) r.Pinned = !r.Pinned;
            });
            BuildRecent();
        });
        Item("Remove from this list", "Icon.Dismiss", () =>
        {
            AppSettings.RemoveRecent(item.Path);
            BuildRecent();
        });
        return menu;
    }

    public static void ShowInFolder(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Show in folder");
        }
    }

    public static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var days = (DateTime.Now.Date - local.Date).TotalDays;
        if (days < 1) return "Today " + local.ToString("t", CultureInfo.CurrentCulture);
        if (days < 2) return "Yesterday";
        if (days < 7) return local.ToString("dddd", CultureInfo.CurrentCulture);
        return local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    // ------------------------------------------------------------------ recovery

    private void BuildRecovery()
    {
        RecoveryPanel.Children.Clear();
        var orphans = Recovery.FindOrphans()
            .Where(o => !App.Current.DocumentWindows.Any(w => w.Session?.RecoveryKey == o.Key))
            .ToList();
        RecoveryPanel.Visibility = orphans.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var entry in orphans.Take(4))
        {
            var card = new Border { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(16, 12, 12, 12), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "Status.CautionBackground");
            card.SetResourceReference(Border.BorderBrushProperty, "Card.Stroke");
            var dock = new DockPanel();
            var icon = new IconView { Width = 18, Height = 18, Margin = new Thickness(0, 1, 14, 0), VerticalAlignment = VerticalAlignment.Top, Data = (Geometry)FindResource("Icon.History") };
            icon.SetResourceReference(IconView.ForegroundProperty, "Status.Caution");
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(icon);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(buttons, Dock.Right);
            var open = new Button { Content = "Recover", MinWidth = 96 };
            open.SetResourceReference(StyleProperty, "AccentButton");
            var discard = new Button { Content = "Discard", Margin = new Thickness(8, 0, 0, 0), MinWidth = 88 };
            buttons.Children.Add(open);
            buttons.Children.Add(discard);
            dock.Children.Add(buttons);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = $"Unsaved changes to “{entry.Title}” were kept", FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock
            {
                Text = $"Inkwell closed unexpectedly. A copy was saved {When(entry.SavedUtc).ToLowerInvariant()}.",
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
            });
            dock.Children.Add(text);
            card.Child = dock;
            open.Click += (_, _) => Recover(entry);
            discard.Click += (_, _) =>
            {
                Recovery.Discard(entry.Key);
                BuildRecovery();
            };
            RecoveryPanel.Children.Add(card);
        }
    }

    private async void Recover(RecoveryEntry entry)
    {
        if (!Recovery.Claim(entry.Key))
        {
            BuildRecovery();
            return;
        }
        var result = await _window.RunBusyAsync("Recovering “" + entry.Title + "”…", "", () => DocumentFiles.Open(entry.DocumentPath));
        if (result == null) return;
        var path = entry.OriginalPath != null && File.Exists(entry.OriginalPath) && DocumentFiles.WordExtensions.Contains(Path.GetExtension(entry.OriginalPath).ToLowerInvariant())
            ? entry.OriginalPath : null;
        var session = new DocumentSession(result.Document, path, "docx") { RecoveryKey = entry.Key };
        session.MarkDirty();
        App.Current.OpenSession(session, _window, "Recovered. Save to keep these changes.");
    }

    // ------------------------------------------------------------------ the tour

    private static readonly (string Icon, string Title, string Text)[] Fixes =
    {
        ("Icon.Image", "Pictures stay where you put them", "Clear wrap choices with a live preview, alignment guides, a visible anchor and Lock position. Pictures you move keep their place on the page when the text above them changes."),
        ("Icon.TextNumberListLtr", "Lists number the way you expect", "A list continues only from the list right above it. \"Restart at 1\" and \"Continue from previous\" are right there in the panel, and indents stay consistent."),
        ("Icon.ClipboardPaste", "Paste without the mess", "Pasted text takes your document's look and keeps bold, italic, links and lists. One click on the paste chip keeps the original formatting; Ctrl+Shift+V pastes plain text."),
        ("Icon.TextEffects", "See why text looks the way it does", "Reveal formatting shows the style, the direct formatting and where each setting comes from. Clear formatting is one click, and a consistency check finds stray formatting."),
        ("Icon.DocumentPageBreak", "No mystery blank pages", "Page, section and column breaks are visible, labelled and selectable. An empty paragraph after a final table or break never makes an extra page."),
        ("Icon.Table", "Tables that cooperate", "Drag column borders, repeat header rows on every page, keep rows from splitting, add rows and columns with one click; Tab in the last cell adds a row."),
        ("Icon.Wand", "Autocorrect you can trust", "Every automatic change shows a small chip and goes away with one Ctrl+Z. No capital letters after i.e., e.g. or etc., and every rule can be turned off."),
        ("Icon.History", "Never lose work", "Documents with a file save themselves as you write, safely. Recovery copies cover new documents, and version history keeps earlier versions you can browse and restore."),
        ("Icon.DocumentLandscape", "One landscape page, one click", "\"Make this page landscape\" adds the section breaks for you. Different margins for one page work the same way."),
        ("Icon.DocumentHeaderFooter", "Headers and footers without fiddling", "Click a header to edit it. First-page and odd/even options are obvious toggles, and page numbers are one click."),
        ("Icon.TextEditStyle", "Styles that make sense", "A small, clear set of styles that follow the document's design, and \"Update style to match selection\" when you like what you made."),
        ("Icon.TextWordCount", "Counts you can rely on", "Live word and character counts for the document or the selection, with or without footnotes, and reading time."),
    };

    private void BuildTour()
    {
        TourCards.Items.Clear();
        foreach (var (icon, title, text) in Fixes)
        {
            var card = new Border { Padding = new Thickness(16, 14, 16, 16), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0) };
            card.SetResourceReference(Border.BackgroundProperty, "Card.Background");
            card.SetResourceReference(Border.BorderBrushProperty, "Card.Stroke");
            var dock = new DockPanel();
            var badge = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
            badge.SetResourceReference(Border.BackgroundProperty, "Accent.Selection");
            var iv = new IconView { Width = 20, Height = 20, Data = TryFindResource(icon) as Geometry ?? (Geometry)FindResource("Icon.Sparkle") };
            iv.SetResourceReference(IconView.ForegroundProperty, "Text.Accent");
            badge.Child = iv;
            DockPanel.SetDock(badge, Dock.Left);
            dock.Children.Add(badge);
            var stack = new StackPanel();
            var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            var d = new TextBlock { Text = text, FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
            d.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            stack.Children.Add(t);
            stack.Children.Add(d);
            dock.Children.Add(stack);
            card.Child = dock;
            TourCards.Items.Add(card);
        }
    }

    private void OnHideTour(object sender, RoutedEventArgs e)
    {
        AppSettings.Update(s => s.TourDismissed = true);
        Refresh();
    }

    // ------------------------------------------------------------------ buttons

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a document",
            Filter = DocumentFiles.OpenFilter,
            CheckFileExists = true,
        };
        if (AppSettings.Current.LastFolder is { } folder && Directory.Exists(folder)) dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(_window) != true) return;
        AppSettings.Update(s => s.LastFolder = Path.GetDirectoryName(dialog.FileName));
        App.Current.OpenPath(dialog.FileName, _window);
    }

    private void OnSettings(object sender, RoutedEventArgs e) => SettingsWindow.ShowSingle(_window);

    private void OnAbout(object sender, RoutedEventArgs e) => AboutDialog.Open(_window);
}
