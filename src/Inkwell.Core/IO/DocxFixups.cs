using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using Inkwell.Core.Model;

namespace Inkwell.Core.IO;

/// <summary>Checks applied to every part Inkwell writes: namespace declarations, characters XML can't hold, paragraph ids.</summary>
internal static class DocxFixups
{
    private static readonly (string Prefix, string Uri)[] KnownPrefixes =
    {
        ("w", Ns.W.NamespaceName), ("r", Ns.R.NamespaceName), ("wp", Ns.WP.NamespaceName), ("a", Ns.A.NamespaceName),
        ("pic", Ns.Pic.NamespaceName), ("mc", Ns.MC.NamespaceName), ("w14", Ns.W14.NamespaceName), ("w15", Ns.W15.NamespaceName),
        ("w16cid", Ns.W16CID.NamespaceName), ("w16cex", Ns.W16CEX.NamespaceName), ("w16se", Ns.W16SE.NamespaceName),
        ("w16", "http://schemas.microsoft.com/office/word/2018/wordml"),
        ("w16sdtdh", "http://schemas.microsoft.com/office/word/2020/wordml/sdtdatahash"),
        ("w16du", "http://schemas.microsoft.com/office/word/2023/wordml/word16du"),
        ("w16sdtfl", "http://schemas.microsoft.com/office/word/2024/wordml/sdtformatlock"),
        ("wp14", Ns.WP14.NamespaceName), ("wps", Ns.WPS.NamespaceName), ("wpg", Ns.WPG.NamespaceName), ("wpc", Ns.WPC.NamespaceName),
        ("wpi", "http://schemas.microsoft.com/office/word/2010/wordprocessingInk"),
        ("m", Ns.M.NamespaceName), ("v", Ns.V.NamespaceName), ("o", Ns.O.NamespaceName), ("w10", Ns.W10.NamespaceName),
        ("wne", Ns.WNE.NamespaceName), ("a14", Ns.A14.NamespaceName), ("c", Ns.C.NamespaceName), ("dgm", Ns.DGM.NamespaceName),
        ("cx", "http://schemas.microsoft.com/office/drawing/2014/chartex"),
        ("aink", "http://schemas.microsoft.com/office/drawing/2016/ink"),
        ("am3d", "http://schemas.microsoft.com/office/drawing/2017/model3d"),
        ("iw", Ns.IW.NamespaceName),
    };

    /// <summary>Extension namespaces Word lists in mc:Ignorable (older readers skip them).</summary>
    private static readonly string[] IgnorablePrefixes = { "w14", "w15", "w16se", "w16cid", "w16", "w16cex", "w16sdtdh", "w16du", "w16sdtfl", "wp14", "iw" };

    /// <summary>
    /// Declares on the root every namespace the part uses (with Word's prefixes) and the prefixes mc:Choice
    /// Requires names, and keeps mc:Ignorable to declared prefixes (adding Word's extension prefixes that are declared).
    /// </summary>
    public static void FixNamespaces(XElement root)
    {
        var nsToPrefix = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefixToNs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in root.Attributes())
        {
            if (!a.IsNamespaceDeclaration) continue;
            var prefix = a.Name.Namespace == XNamespace.None ? "" : a.Name.LocalName;
            prefixToNs[prefix] = a.Value;
            if (!nsToPrefix.ContainsKey(a.Value)) nsToPrefix[a.Value] = prefix;
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var requires = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in root.DescendantsAndSelf())
        {
            used.Add(e.Name.NamespaceName);
            foreach (var a in e.Attributes())
            {
                if (a.IsNamespaceDeclaration) continue;
                if (a.Name.Namespace != XNamespace.None) used.Add(a.Name.NamespaceName);
                else if (a.Name.LocalName == "Requires" && e.Name == Wn.Choice)
                    foreach (var p in a.Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) requires.Add(p);
            }
        }
        used.Remove("");
        used.Remove(XNamespace.Xml.NamespaceName);
        used.Remove(XNamespace.Xmlns.NamespaceName);

        void Declare(string prefix, string uri)
        {
            if (prefixToNs.ContainsKey(prefix)) return;
            root.SetAttributeValue(XNamespace.Xmlns + prefix, uri);
            prefixToNs[prefix] = uri;
            if (!nsToPrefix.ContainsKey(uri)) nsToPrefix[uri] = prefix;
        }

        foreach (var uri in used)
        {
            if (nsToPrefix.ContainsKey(uri)) continue;
            foreach (var (prefix, known) in KnownPrefixes)
            {
                if (known != uri) continue;
                Declare(prefix, uri);
                break;
            }
        }
        foreach (var prefix in requires)
        {
            if (prefixToNs.ContainsKey(prefix)) continue;
            foreach (var (known, uri) in KnownPrefixes)
            {
                if (known != prefix) continue;
                Declare(prefix, uri);
                break;
            }
        }

        var ignorable = root.Attribute(Wn.Ignorable)?.Value ?? "";
        var tokens = ignorable.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(p => prefixToNs.ContainsKey(p) && p.Length > 0).ToList();
        foreach (var p in IgnorablePrefixes)
        {
            if (tokens.Contains(p) || !prefixToNs.TryGetValue(p, out var uri)) continue;
            if (KnownPrefixes.Any(k => k.Prefix == p && k.Uri == uri)) tokens.Add(p);
        }
        // The root's own namespace is never ignorable.
        tokens.RemoveAll(p => prefixToNs.TryGetValue(p, out var uri) && uri == root.Name.NamespaceName);
        if (tokens.Count > 0)
        {
            Declare("mc", Ns.MC.NamespaceName);
            root.SetAttributeValue(Wn.Ignorable, string.Join(" ", tokens));
        }
        else
        {
            root.Attribute(Wn.Ignorable)?.Remove();
        }
    }

    /// <summary>Replaces characters XML 1.0 can't hold in text and attribute values (control characters, lone surrogates).</summary>
    public static void Sanitize(XElement root)
    {
        foreach (var node in root.DescendantNodesAndSelf())
        {
            if (node is XText text)
            {
                if (NeedsCleaning(text.Value)) text.Value = Clean(text.Value);
            }
            else if (node is XElement e && e.HasAttributes)
            {
                foreach (var a in e.Attributes())
                    if (NeedsCleaning(a.Value)) a.Value = Clean(a.Value);
            }
        }
    }

    private static bool NeedsCleaning(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c >= 0x20 && c < 0xD800) continue;
            if (c == '\t' || c == '\n' || c == '\r') continue;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
                continue;
            }
            if (c >= 0xE000 && c <= 0xFFFD) continue;
            return true;
        }
        return false;
    }

    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c >= 0x20 && c < 0xD800 || c == '\t' || c == '\n' || c == '\r' || c >= 0xE000 && c <= 0xFFFD)
            {
                sb.Append(c);
            }
            else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb.Append(c).Append(s[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                sb.Append('�');
            }
            // other control characters and U+FFFE / U+FFFF are dropped
        }
        return sb.ToString();
    }

    /// <summary>A w14:paraId / durableId value Word accepts: 8 hex digits, above 0 and below 0x80000000.</summary>
    public static bool IsValidParaId(string? id)
    {
        if (id == null || id.Length != 8) return false;
        if (!uint.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        return v > 0 && v < 0x7FFFFFFF;
    }

    /// <summary>A new random id not in <paramref name="used"/> (added to it).</summary>
    public static string NewParaId(HashSet<string> used)
    {
        while (true)
        {
            var id = Compat.NextRandom(0x100000, 0x7FFFFFFE).ToString("X8", CultureInfo.InvariantCulture);
            if (used.Add(id)) return id;
        }
    }
}

// The passes over the generated parts: unique ids, references to things that no longer exist, relationships.
internal sealed partial class DocxWriteSession
{
    private static readonly HashSet<string> RevisionNames = new(StringComparer.Ordinal)
    {
        "ins", "del", "moveFrom", "moveTo", "moveFromRangeStart", "moveFromRangeEnd", "moveToRangeStart", "moveToRangeEnd",
        "rPrChange", "pPrChange", "sectPrChange", "tblPrChange", "trPrChange", "tcPrChange", "tblGridChange", "tblPrExChange",
        "numberingChange", "cellIns", "cellDel", "cellMerge",
        "customXmlInsRangeStart", "customXmlInsRangeEnd", "customXmlDelRangeStart", "customXmlDelRangeEnd",
        "customXmlMoveFromRangeStart", "customXmlMoveFromRangeEnd", "customXmlMoveToRangeStart", "customXmlMoveToRangeEnd",
    };

    private sealed class Scan
    {
        public List<XElement> Revisions { get; } = new();
        public List<XElement> Bookmarks { get; } = new();
        public List<XElement> DocPrs { get; } = new();
        public List<XAttribute> ParaIds { get; } = new();
        public List<XElement> CommentRefs { get; } = new();
        public List<XElement> NoteRefs { get; } = new();
        public List<XElement> HeaderRefs { get; } = new();
    }

    private static Scan ScanTree(XElement root)
    {
        var scan = new Scan();
        void Visit(XElement e, bool inFallback)
        {
            var name = e.Name;
            if (name.Namespace == Ns.W)
            {
                var local = name.LocalName;
                if (RevisionNames.Contains(local)) scan.Revisions.Add(e);
                else if (local is "bookmarkStart" or "bookmarkEnd") scan.Bookmarks.Add(e);
                else if (local is "commentRangeStart" or "commentRangeEnd" or "commentReference") scan.CommentRefs.Add(e);
                else if (local is "footnoteReference" or "endnoteReference") scan.NoteRefs.Add(e);
                else if (local is "headerReference" or "footerReference") scan.HeaderRefs.Add(e);
                if (!inFallback && (local is "p" or "tr") && e.Attribute(Wn.ParaId) is { } paraId) scan.ParaIds.Add(paraId);
            }
            else if (name == Wn.WpDocPr)
            {
                scan.DocPrs.Add(e);
            }
            var fallback = inFallback || name == Wn.Fallback;
            foreach (var child in e.Elements()) Visit(child, fallback);
        }
        Visit(root, false);
        return scan;
    }

    private void FixUpTrees()
    {
        var scans = _trees.Select(t => ScanTree(t.Xml.Root!)).ToList();

        // Revision ids: unique across the document (pairs of range markers keep a shared id).
        var revision = 0;
        foreach (var scan in scans)
        {
            var open = new Dictionary<string, Stack<string>>(StringComparer.Ordinal);
            foreach (var e in scan.Revisions)
            {
                var local = e.Name.LocalName;
                var oldId = e.Attribute(Wn.WId)?.Value ?? "";
                if (local.EndsWith("RangeStart", StringComparison.Ordinal))
                {
                    var id = (++revision).ToString(CultureInfo.InvariantCulture);
                    var key = local.Substring(0, local.Length - 5) + "|" + oldId;
                    if (!open.TryGetValue(key, out var stack)) open[key] = stack = new Stack<string>();
                    stack.Push(id);
                    e.SetAttributeValue(Wn.WId, id);
                }
                else if (local.EndsWith("RangeEnd", StringComparison.Ordinal))
                {
                    var key = local.Substring(0, local.Length - 3) + "|" + oldId;
                    e.SetAttributeValue(Wn.WId, open.TryGetValue(key, out var stack) && stack.Count > 0 ? stack.Pop() : (++revision).ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    e.SetAttributeValue(Wn.WId, (++revision).ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        // Bookmark ids: unique, starts and ends paired; ends without a start are dropped.
        var bookmark = 0;
        foreach (var scan in scans)
        {
            var open = new Dictionary<string, Stack<string>>(StringComparer.Ordinal);
            foreach (var e in scan.Bookmarks)
            {
                var oldId = e.Attribute(Wn.WId)?.Value ?? "";
                if (e.Name == Wn.BookmarkStart)
                {
                    var id = (bookmark++).ToString(CultureInfo.InvariantCulture);
                    if (!open.TryGetValue(oldId, out var stack)) open[oldId] = stack = new Stack<string>();
                    stack.Push(id);
                    e.SetAttributeValue(Wn.WId, id);
                    if (string.IsNullOrEmpty(e.Attribute(Wn.WName)?.Value)) e.SetAttributeValue(Wn.WName, "_Bookmark" + id);
                }
                else if (open.TryGetValue(oldId, out var stack) && stack.Count > 0)
                {
                    e.SetAttributeValue(Wn.WId, stack.Pop());
                }
                else
                {
                    e.Remove();
                }
            }
        }

        // Drawing ids (wp:docPr/@id): unique across the document; existing ones are kept where possible.
        var usedDocPr = new HashSet<long>();
        var needId = new List<XElement>();
        foreach (var e in scans.SelectMany(s => s.DocPrs))
        {
            var v = DocxXml.ParseLong(e.Attribute("id")?.Value);
            if (v is > 0 and <= uint.MaxValue && usedDocPr.Add(v.Value)) continue;
            needId.Add(e);
        }
        long nextDocPr = 1;
        foreach (var e in needId)
        {
            while (usedDocPr.Contains(nextDocPr)) nextDocPr++;
            usedDocPr.Add(nextDocPr);
            e.SetAttributeValue("id", Ox.Str(nextDocPr));
        }

        // Paragraph ids (w14:paraId): unique; comments' last paragraphs keep theirs (commentsExtended refers to them).
        var usedPara = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in _trees)
            foreach (var a in tree.ReservedParaIds)
                usedPara.Add(a.Value);
        for (var i = 0; i < _trees.Count; i++)
        {
            foreach (var a in scans[i].ParaIds)
            {
                if (_trees[i].ReservedParaIds.Contains(a)) continue;
                if (DocxFixups.IsValidParaId(a.Value) && usedPara.Add(a.Value)) continue;
                a.Value = DocxFixups.NewParaId(usedPara);
            }
        }

        // References to comments and notes that no longer exist would make Word report the file as damaged.
        foreach (var scan in scans)
        {
            foreach (var e in scan.CommentRefs)
            {
                var id = e.IntAttr("id");
                if (id != null && _commentIds.Contains(id.Value)) continue;
                RemoveRunContent(e);
            }
            foreach (var e in scan.NoteRefs)
            {
                var id = e.IntAttr("id");
                var valid = e.Name == Wn.FootnoteReference ? _footnoteIds : _endnoteIds;
                if (id != null && valid.Contains(id.Value)) continue;
                RemoveRunContent(e);
                Warn("A reference to a footnote or endnote that no longer exists was removed.");
            }
            foreach (var e in scan.HeaderRefs)
            {
                var id = e.Attribute(Ns.R + "id")?.Value;
                if (id != null && _hfIdMap.TryGetValue(id, out var mapped))
                {
                    id = mapped;
                    e.SetAttributeValue(Ns.R + "id", id);
                }
                var header = e.Name == Wn.HeaderReference;
                if (id != null && _main.TryGetPartById(id, out var part) && (header ? part is HeaderPart : part is FooterPart)) continue;
                e.Remove();
            }
        }

        foreach (var tree in _trees)
        {
            DocxFixups.FixNamespaces(tree.Xml.Root!);
            DocxFixups.Sanitize(tree.Xml.Root!);
        }
    }

    /// <summary>Removes an element from its run (and the run if nothing else is left in it).</summary>
    private static void RemoveRunContent(XElement e)
    {
        var run = e.Parent;
        e.Remove();
        if (run != null && run.Name == Wn.R && !run.Elements().Any(c => c.Name != Wn.RPr)) run.Remove();
    }

    /// <summary>Header and footer parts no section refers to any more are deleted.</summary>
    private void RemoveUnusedHeaderFooters()
    {
        var document = _trees.First(t => t.Kind == "document");
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in document.Xml.Root!.Descendants())
            if ((e.Name == Wn.HeaderReference || e.Name == Wn.FooterReference) && e.Attribute(Ns.R + "id")?.Value is { } id)
                referenced.Add(id);
        foreach (var pair in _main.Parts.ToList())
        {
            if (pair.OpenXmlPart is not (HeaderPart or FooterPart) || referenced.Contains(pair.RelationshipId)) continue;
            foreach (var tree in _trees)
                if (ReferenceEquals(tree.Writer.Part, pair.OpenXmlPart)) tree.Skip = true;
            _main.DeletePart(pair.RelationshipId);
        }
    }

    /// <summary>
    /// A relationship id the part doesn't have (stale content pasted from elsewhere, an object whose part is gone)
    /// would stop Word opening the file: the object holding it is removed instead.
    /// </summary>
    private void ScrubDanglingRelationships(StoryTree tree)
    {
        var valid = new HashSet<string>(DocxXml.RelationshipIds(tree.Writer.Part), StringComparer.Ordinal);
        foreach (var a in RelAttributes(tree.Xml.Root!).ToList())
        {
            if (a.Parent == null || valid.Contains(a.Value)) continue;
            var owner = a.Parent;
            if (owner.Name == Wn.Hyperlink)
            {
                a.Remove();
                continue;
            }
            var removable = owner.AncestorsAndSelf().FirstOrDefault(x =>
                x.Name == Wn.Drawing || x.Name == Wn.AlternateContent || x.Name == Wn.Pict || x.Name == Wn.Object || x.Name == Wn.AltChunk ||
                x.Name == Ns.W + "subDoc" || x.Name == Ns.W + "contentPart" || x.Name == Wn.Background);
            if (removable != null)
            {
                if (removable.Parent?.Name == Wn.R) RemoveRunContent(removable);
                else removable.Remove();
                Warn("An object whose data couldn't be found was left out.");
            }
            else
            {
                a.Remove();
            }
        }
    }

    /// <summary>Image parts and hyperlink relationships nothing in the part refers to any more are removed.</summary>
    private static void RemoveUnusedRelationships(StoryTree tree)
    {
        var part = tree.Writer.Part;
        var used = new HashSet<string>(RelAttributes(tree.Xml.Root!).Select(a => a.Value), StringComparer.Ordinal);
        foreach (var pair in part.Parts.ToList())
            if (pair.OpenXmlPart is ImagePart && !used.Contains(pair.RelationshipId)) part.DeletePart(pair.RelationshipId);
        foreach (var link in part.HyperlinkRelationships.ToList())
            if (!used.Contains(link.Id)) part.DeleteReferenceRelationship(link);
    }
}
