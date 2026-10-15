using Inkwell.Core.Model;

namespace Inkwell.Core.Editing;

public sealed partial class DocEditor
{
    /// <summary>Applies autocorrect replacements in the caret's paragraph as one automatic step (one Ctrl+Z undoes it).</summary>
    public bool ApplyAutoCorrect(IReadOnlyList<AutoCorrectEdit> edits)
    {
        if (edits.Count == 0) return false;
        var caret = Selection.Active;
        var p = caret.Para;
        return Edit("AutoCorrect: " + edits[0].RuleName, t =>
        {
            t.Touch(p);
            var offset = caret.Offset;
            foreach (var e in edits.OrderByDescending(e => e.Start))
            {
                if (e.Kind == AutoCorrectEditKind.Superscript)
                {
                    var first = p.SplitAt(e.Start);
                    var last = p.SplitAt(e.End);
                    for (var i = first; i < last; i++) p.Inlines[i].Props.VertAlign = "superscript";
                    p.Normalize();
                    continue;
                }
                var props = p.InlineAt(e.Start)?.Props.Clone() ?? p.PropsForTypingAt(e.Start);
                var link = p.InlineAt(e.Start)?.Link;
                p.RemoveRange(e.Start, e.End);
                if (e.Replacement.Length > 0) p.InsertText(e.Start, e.Replacement, props, link, TrackChanges ? NewRevision() : null);
                if (e.End <= offset) offset += e.Replacement.Length - e.Length;
                else if (e.Start < offset) offset = e.Start + e.Replacement.Length;
            }
            return new Selection(new DocPos(p, MathEx.Clamp(offset, 0, p.Length)));
        }, automatic: true);
    }

    /// <summary>A Markdown paragraph shortcut ("# ", "- ", "1. ", "> ", "```", "---") as one automatic step.</summary>
    public bool ApplyBlockShortcut(BlockShortcut shortcut)
    {
        var caret = Selection.Active;
        var p = caret.Para;
        if (Nav.IsReadOnly(p)) return false;
        return Edit("Markdown: " + shortcut.Description, t =>
        {
            t.Touch(p);
            var remove = Math.Min(shortcut.RemoveLength, p.Length);
            if (remove > 0) p.RemoveRange(0, remove);
            switch (shortcut.Kind)
            {
                case BlockShortcutKind.Heading:
                    p.Props.StyleId = "Heading" + MathEx.Clamp(shortcut.Level, 1, 3);
                    p.Props.Remove("numPr");
                    break;
                case BlockShortcutKind.Quote:
                    p.Props.StyleId = "Quote";
                    break;
                case BlockShortcutKind.CodeBlock:
                    p.Props.StyleId = Doc.Styles.Get("Code") != null ? "Code" : null;
                    break;
                case BlockShortcutKind.BulletList:
                case BlockShortcutKind.NumberedList:
                {
                    var bullets = shortcut.Kind == BlockShortcutKind.BulletList;
                    var kind = bullets ? "bullet" : "number";
                    var above = Nav.Prev(p);
                    int numId;
                    if (above != null && ListKindOf(above) == kind && ReferenceEquals(above.Parent, p.Parent) && (bullets || shortcut.StartNumber != 1))
                        numId = Styles.ResolvePara(above).NumId;
                    else if (above != null && ListKindOf(above) == kind && ReferenceEquals(above.Parent, p.Parent) && !bullets)
                        numId = Styles.ResolvePara(above).NumId;
                    else
                    {
                        t.TouchNumbering();
                        var abs = Doc.Numbering.AddAbstract(Numbering.StandardDefinition(bullets));
                        numId = Doc.Numbering.AddNum(abs, !bullets && shortcut.StartNumber != 1 ? shortcut.StartNumber : null);
                    }
                    p.Props.SetNumbering(numId, 0);
                    if (p.Props.StyleId == null || p.Props.StyleId == Doc.Styles.DefaultParagraphStyleId) p.Props.StyleId = "ListParagraph";
                    break;
                }
                case BlockShortcutKind.Divider:
                {
                    p.Inlines.RemoveAll(i => i.Length > 0);
                    p.Props.StyleId = null;
                    p.Props.SetBorder("bottom", new BorderDef("single", 6, 1, "BFBCB4"));
                    p.Props.SpacingAfter = 240;
                    var next = SplitParagraph(t, new DocPos(p, p.Length), keepStyle: true);
                    t.Touch(next.Para);
                    next.Para.Props.SetBorder("bottom", null);
                    next.Para.Props.SpacingAfter = null;
                    return new Selection(next);
                }
            }
            if (shortcut.ByEnter)
            {
                // Typed by Enter (``` or ---): start the next line inside the new block.
                return new Selection(SplitParagraph(t, new DocPos(p, p.Length), keepStyle: true));
            }
            return new Selection(new DocPos(p, Math.Max(0, caret.Offset - remove)));
        }, automatic: true);
    }

    /// <summary>A Markdown formatting shortcut (**bold**, _italic_, `code`, ~~strike~~) as one automatic step.</summary>
    public bool ApplyInlineShortcut(InlineShortcut s)
    {
        var p = Selection.Active.Para;
        if (Nav.IsReadOnly(p)) return false;
        return Edit("Markdown: " + s.Description, t =>
        {
            t.Touch(p);
            // Remove the closing marker first (offsets of the opening one stay valid).
            p.RemoveRange(s.CloseStart, s.CloseStart + s.CloseLength);
            p.RemoveRange(s.OpenStart, s.OpenStart + s.OpenLength);
            var start = s.OpenStart;
            var end = start + s.ContentLength;
            FormatRange(t, p, start, end, r =>
            {
                switch (s.Format)
                {
                    case InlineFormat.Bold:
                        r.Bold = true;
                        r.BoldCs = true;
                        break;
                    case InlineFormat.Italic:
                        r.Italic = true;
                        r.ItalicCs = true;
                        break;
                    case InlineFormat.Strikethrough:
                        r.Strike = true;
                        break;
                    case InlineFormat.Code:
                        r.StyleId = Doc.Styles.Get("InlineCode") != null ? "InlineCode" : r.StyleId;
                        break;
                }
            });
            // Typing goes on in plain text after the formatted part.
            var plain = p.PropsForTypingAt(start);
            plain.Bold = null;
            plain.Italic = null;
            plain.BoldCs = null;
            plain.ItalicCs = null;
            plain.Strike = null;
            if (plain.StyleId == "InlineCode") plain.StyleId = null;
            PendingProps = plain;
            return new Selection(new DocPos(p, end));
        }, automatic: true);
    }

    /// <summary>Removes the "/command" text typed for the slash menu (as part of the next step).</summary>
    public void RemoveTyped(Paragraph p, int start, int end)
    {
        if (end <= start) return;
        Edit("Slash command", t =>
        {
            t.Touch(p);
            p.RemoveRange(start, Math.Min(end, p.Length));
            return new Selection(new DocPos(p, start));
        }, automatic: true);
    }

    /// <summary>
    /// Typing right-to-left text (Urdu, Arabic…) into an empty paragraph makes the paragraph right-to-left, and the
    /// text gets the language and a suitable font (Noto Nastaliq Urdu for Urdu when the document has no Urdu font).
    /// </summary>
    public RunProps? PrepareRtlTyping(string languageTag)
    {
        var caret = Selection.Active;
        var p = caret.Para;
        var props = (PendingProps ?? p.PropsForTypingAt(caret.Offset)).Clone();
        var resolved = Styles.ResolveRun(props, p);
        var changed = false;
        if (props.Rtl != true)
        {
            props.Rtl = true;
            changed = true;
        }
        if (!string.Equals(resolved.LangBidi, languageTag, StringComparison.OrdinalIgnoreCase))
        {
            props.LangBidi = languageTag;
            changed = true;
        }
        var urdu = languageTag.StartsWith("ur", StringComparison.OrdinalIgnoreCase);
        var csFont = resolved.CsFont;
        var nastaliq = csFont.IndexOf("Nastaliq", StringComparison.OrdinalIgnoreCase) >= 0 || csFont.IndexOf("Nastaleeq", StringComparison.OrdinalIgnoreCase) >= 0 || csFont.IndexOf("Urdu", StringComparison.OrdinalIgnoreCase) >= 0;
        if (urdu && !nastaliq)
        {
            props.SetComplexFont("Noto Nastaliq Urdu");
            changed = true;
        }
        else if (!urdu && csFont == "Times New Roman" && Rendering.FontCatalog.IsBundled("Noto Naskh Arabic"))
        {
            props.SetComplexFont("Noto Naskh Arabic");
            changed = true;
        }
        if (p.IsEmpty && !Styles.ResolvePara(p).Bidi && !Nav.IsReadOnly(p))
        {
            Edit("Right-to-left paragraph", t =>
            {
                t.Touch(p);
                p.Props.Bidi = true;
                return null;
            }, automatic: true);
        }
        return changed ? props : null;
    }
}
