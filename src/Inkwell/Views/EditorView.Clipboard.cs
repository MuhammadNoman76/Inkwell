using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Inkwell.Core.Editing;
using Inkwell.Core.IO;
using Inkwell.Core.Model;
using Inkwell.Core.Layout;
using Inkwell.Services;

namespace Inkwell.Views;

/// <summary>Cut, copy and paste (matching the destination, with a chip to keep the source's look or paste text only), drag and drop.</summary>
public partial class EditorView
{
    private const string FragmentFormat = "Inkwell.Fragment";

    /// <summary>The last fragment copied in this process; the clipboard holds its token so other programs' copies win.</summary>
    private static DocFragment? s_copied;
    private static string? s_copiedToken;

    private Border? _pasteChip;
    private DateTime _pasteChipShown;
    private DocFragment? _pastedFragment;
    private UndoEntry? _pasteEntry;

    public void Copy()
    {
        if (Doc.SelectedDrawing is { } drawing)
        {
            CopyDrawing(drawing);
            return;
        }
        var fragment = Editor.CopySelection();
        if (fragment == null) return;
        PutOnClipboard(fragment);
    }

    public void Cut()
    {
        if (Doc.SelectedDrawing is { } drawing)
        {
            CopyDrawing(drawing);
            Run(() => Editor.DeleteDrawing(drawing));
            Doc.SelectDrawing(null);
            return;
        }
        var fragment = Editor.CopySelection();
        if (fragment == null) return;
        if (Nav.IsReadOnly(Editor.Selection.Start.Para) || Nav.IsReadOnly(Editor.Selection.End.Para))
        {
            PutOnClipboard(fragment);
            _window.ShowMessage("Copied. This part can't be cut because it's generated or kept exactly as it was.", 4);
            return;
        }
        if (PutOnClipboard(fragment)) Run(() => Editor.DeleteSelection());
    }

    private void CopyDrawing(DrawingObject drawing)
    {
        var p = Editor.ParagraphOf(drawing);
        if (p == null) return;
        var fragment = new DocFragment { Source = Session.Document, OpenEnd = true };
        var para = new Paragraph();
        para.Inlines.Add(drawing.Clone());
        fragment.Blocks.Add(para);
        var image = drawing.MediaId != null ? Session.Document.Media.Get(drawing.MediaId) : null;
        PutOnClipboard(fragment, image?.Data);
    }

    private bool PutOnClipboard(DocFragment fragment, byte[]? picture = null)
    {
        var token = Guid.NewGuid().ToString("N");
        var data = new DataObject();
        var text = fragment.Text;
        data.SetData(DataFormats.UnicodeText, text);
        data.SetData(DataFormats.Text, text);
        data.SetData(FragmentFormat, token);
        try
        {
            var html = DocumentFiles.FragmentHtml(fragment);
            if (html is { Length: > 0 }) data.SetData(DataFormats.Html, new MemoryStream(html));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Copy as HTML");
        }
        if (picture != null && Core.Rendering.MediaImages.Decode(picture) is { } bitmap) data.SetImage(bitmap);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                s_copied = fragment;
                s_copiedToken = token;
                return true;
            }
            catch (COMException)
            {
                // Another program has the clipboard open: try again shortly.
                System.Threading.Thread.Sleep(40);
            }
            catch (ExternalException)
            {
                System.Threading.Thread.Sleep(40);
            }
        }
        _window.ShowMessage("The clipboard is busy (another program is using it). Try again.", 4, warning: true);
        return false;
    }

    /// <summary>Pastes what's on the clipboard. Default matches the destination and offers the other choices on a chip.</summary>
    public void Paste(PasteMode mode)
    {
        if (AppSettings.Current.PasteMatchesDestination == false && mode == PasteMode.Default) mode = PasteMode.KeepSource;
        IDataObject? data;
        try
        {
            data = Clipboard.GetDataObject();
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            _window.ShowMessage("The clipboard is busy (another program is using it). Try again.", 4, warning: true);
            return;
        }
        if (data == null) return;
        try
        {
            PasteData(data, mode, offerChoices: true);
        }
        catch (EditRefusedException ex)
        {
            _window.ShowMessage(ex.Message, 5, warning: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Paste");
            _window.ShowMessage("That couldn't be pasted: " + ex.Message, 6, warning: true);
        }
    }

    private void PasteData(IDataObject data, PasteMode mode, bool offerChoices)
    {
        Doc.SelectDrawing(null);
        var fragment = FragmentFrom(data, mode, out var picture);
        if (picture != null)
        {
            Editor.InsertPicture(picture, null);
            FocusDocument();
            return;
        }
        if (fragment == null || fragment.IsEmpty) return;
        Editor.Paste(fragment, mode);
        Doc.ScrollToCaret();
        FocusDocument();
        // Only content that brings its own formatting from elsewhere gets the choices.
        if (offerChoices && mode != PasteMode.TextOnly && fragment.Source != null && !ReferenceEquals(fragment.Source, Session.Document))
            ShowPasteChip(fragment, mode);
    }

    /// <summary>What the clipboard holds, best format first: Inkwell's own, Word / web formatting, a picture, files, text.</summary>
    private DocFragment? FragmentFrom(IDataObject data, PasteMode mode, out byte[]? picture)
    {
        picture = null;
        if (data.GetDataPresent(FragmentFormat) && data.GetData(FragmentFormat) is string token && token == s_copiedToken && s_copied != null)
            return s_copied;
        if (mode != PasteMode.TextOnly)
        {
            if (data.GetDataPresent(DataFormats.Html))
            {
                // Read the raw bytes (UTF-8 by the CF_HTML rules): WPF's string conversion can garble non-ASCII text.
                var raw = data.GetData(DataFormats.Html, false);
                Document? doc = raw switch
                {
                    MemoryStream ms => TryImport(() => DocumentFiles.ImportClipboardHtml(ms.ToArray())),
                    string s when s.Length > 0 => TryImport(() => DocumentFiles.ImportClipboardHtml(s)),
                    _ => null,
                };
                if (doc != null) return DocFragment.FromDocument(doc);
            }
            if (data.GetDataPresent(DataFormats.Rtf) && data.GetData(DataFormats.Rtf) is string rtf && rtf.Length > 0)
            {
                var doc = TryImport(() => DocumentFiles.ImportClipboardRtf(rtf));
                if (doc != null) return DocFragment.FromDocument(doc);
            }
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                var image = files.FirstOrDefault(IsPictureFile);
                if (image != null)
                {
                    picture = File.ReadAllBytes(image);
                    return null;
                }
            }
            if (data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent("PNG"))
            {
                picture = ClipboardPicture(data);
                if (picture != null) return null;
            }
        }
        string? text = null;
        if (data.GetDataPresent(DataFormats.UnicodeText)) text = data.GetData(DataFormats.UnicodeText) as string;
        else if (data.GetDataPresent(DataFormats.Text)) text = data.GetData(DataFormats.Text) as string;
        if (string.IsNullOrEmpty(text) && mode == PasteMode.TextOnly && data.GetDataPresent(FragmentFormat) && s_copied != null)
            text = s_copied.Text;
        return string.IsNullOrEmpty(text) ? null : DocFragment.FromText(text!);
    }

    private static Document? TryImport(Func<Document?> import)
    {
        try
        {
            return import();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Clipboard import");
            return null;
        }
    }

    private static byte[]? ClipboardPicture(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png) return png.ToArray();
            BitmapSource? bitmap = null;
            if (Clipboard.ContainsImage()) bitmap = Clipboard.GetImage();
            if (bitmap == null) return null;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Clipboard picture");
            return null;
        }
    }

    private static bool IsPictureFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff";

    // ------------------------------------------------------------------ the paste chip

    private void ShowPasteChip(DocFragment fragment, PasteMode mode)
    {
        HidePasteChip();
        _pastedFragment = fragment;
        _pasteEntry = Editor.History.Top;
        _pasteChipShown = DateTime.UtcNow;
        var chip = MakeChip(out var stack);
        AutomationProperties.SetName(chip, "Paste options");
        var icon = new Controls.IconView { Width = 14, Height = 14, Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Data = (System.Windows.Media.Geometry)FindResource("Icon.ClipboardPaste") };
        icon.SetResourceReference(Controls.IconView.ForegroundProperty, "Text.Secondary");
        stack.Children.Add(icon);
        var label = new TextBlock { Text = mode == PasteMode.KeepSource ? "Kept the original look" : "Matched this document", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0), FontSize = 12.5 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        stack.Children.Add(label);
        if (mode != PasteMode.KeepSource) stack.Children.Add(ChipButton("Keep source formatting", "Paste again with the original fonts, sizes and colours", () => RepasteAs(PasteMode.KeepSource)));
        else stack.Children.Add(ChipButton("Match this document", "Paste again in this document's styles", () => RepasteAs(PasteMode.Default)));
        stack.Children.Add(ChipButton("Text only", Shortcuts.Tip("Paste again as plain text", Shortcuts.PasteTextOnly), () => RepasteAs(PasteMode.TextOnly)));
        _pasteChip = chip;
        ChipLayer.Children.Add(chip);
        PositionChips();
        _chipTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _chipTimer.Tick -= OnChipTimer;
        _chipTimer.Tick += OnChipTimer;
        _chipTimer.Start();
    }

    private void HidePasteChip()
    {
        if (_pasteChip == null) return;
        ChipLayer.Children.Remove(_pasteChip);
        _pasteChip = null;
    }

    private void RepasteAs(PasteMode mode)
    {
        var fragment = _pastedFragment;
        var entry = _pasteEntry;
        HidePasteChip();
        if (fragment == null) return;
        // Only straight after the paste (the paste is still the last step).
        if (entry == null || !ReferenceEquals(Editor.History.Top, entry))
        {
            _window.ShowMessage($"The document changed since the paste. Undo to the paste first, or paste again with {Shortcuts.PasteTextOnly.Text}.", 5);
            return;
        }
        _suppressChipHide = true;
        try
        {
            Editor.Undo();
            Run(() => Editor.Paste(fragment, mode));
        }
        finally
        {
            _suppressChipHide = false;
        }
        if (mode != PasteMode.TextOnly) ShowPasteChip(fragment, mode);
        FocusDocument();
    }

    // ------------------------------------------------------------------ drag and drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(f => IsPictureFile(f) || DocumentFiles.CanOpen(f)))
                e.Effects = DragDropEffects.Copy;
        }
        else if (e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Html))
        {
            e.Effects = DragDropEffects.Copy;
        }
        if (e.Effects != DragDropEffects.None) MoveCaretToDrop(e);
        e.Handled = true;
    }

    private void MoveCaretToDrop(DragEventArgs e)
    {
        var point = e.GetPosition(Doc);
        var hit = Doc.PageAt(point);
        if (hit == null) return;
        var target = Layout.HitTest(hit.Value.Page, hit.Value.Point);
        if (target != null && target.Position.Para != null && !Nav.IsReadOnly(target.Position.Para)) Editor.Selection = new Selection(target.Position);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                MoveCaretToDrop(e);
                var pictures = files.Where(IsPictureFile).ToList();
                foreach (var file in pictures) Run(() => Editor.InsertPicture(File.ReadAllBytes(file), null));
                foreach (var file in files.Where(f => !IsPictureFile(f) && DocumentFiles.CanOpen(f))) App.Current.OpenPath(file, _window);
                FocusDocument();
                return;
            }
            MoveCaretToDrop(e);
            PasteData(e.Data, PasteMode.Default, offerChoices: true);
        }
        catch (EditRefusedException ex)
        {
            _window.ShowMessage(ex.Message, 5, warning: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Drop");
            _window.ShowMessage("That couldn't be dropped here: " + ex.Message, 6, warning: true);
        }
    }
}
