using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using Inkwell.Core.Model;

namespace Inkwell.Core.IO;

/// <summary>Element names used by the reader and writer (looked up once).</summary>
internal static class Wn
{
    private static readonly XNamespace W = Ns.W;

    public static readonly XName Document = W + "document";
    public static readonly XName Body = W + "body";
    public static readonly XName Background = W + "background";
    public static readonly XName P = W + "p";
    public static readonly XName PPr = W + "pPr";
    public static readonly XName R = W + "r";
    public static readonly XName RPr = W + "rPr";
    public static readonly XName T = W + "t";
    public static readonly XName DelText = W + "delText";
    public static readonly XName Tab = W + "tab";
    public static readonly XName Br = W + "br";
    public static readonly XName SoftHyphen = W + "softHyphen";
    public static readonly XName NoBreakHyphen = W + "noBreakHyphen";
    public static readonly XName Sym = W + "sym";
    public static readonly XName FldChar = W + "fldChar";
    public static readonly XName InstrText = W + "instrText";
    public static readonly XName DelInstrText = W + "delInstrText";
    public static readonly XName FldSimple = W + "fldSimple";
    public static readonly XName Hyperlink = W + "hyperlink";
    public static readonly XName Ins = W + "ins";
    public static readonly XName Del = W + "del";
    public static readonly XName MoveFrom = W + "moveFrom";
    public static readonly XName MoveTo = W + "moveTo";
    public static readonly XName SmartTag = W + "smartTag";
    public static readonly XName SmartTagPr = W + "smartTagPr";
    public static readonly XName CustomXml = W + "customXml";
    public static readonly XName CustomXmlPr = W + "customXmlPr";
    public static readonly XName Dir = W + "dir";
    public static readonly XName Bdo = W + "bdo";
    public static readonly XName Sdt = W + "sdt";
    public static readonly XName SdtPr = W + "sdtPr";
    public static readonly XName SdtEndPr = W + "sdtEndPr";
    public static readonly XName SdtContent = W + "sdtContent";
    public static readonly XName BookmarkStart = W + "bookmarkStart";
    public static readonly XName BookmarkEnd = W + "bookmarkEnd";
    public static readonly XName CommentRangeStart = W + "commentRangeStart";
    public static readonly XName CommentRangeEnd = W + "commentRangeEnd";
    public static readonly XName CommentReference = W + "commentReference";
    public static readonly XName FootnoteReference = W + "footnoteReference";
    public static readonly XName EndnoteReference = W + "endnoteReference";
    public static readonly XName ProofErr = W + "proofErr";
    public static readonly XName Tbl = W + "tbl";
    public static readonly XName TblPr = W + "tblPr";
    public static readonly XName TblGrid = W + "tblGrid";
    public static readonly XName GridCol = W + "gridCol";
    public static readonly XName Tr = W + "tr";
    public static readonly XName TrPr = W + "trPr";
    public static readonly XName TblPrEx = W + "tblPrEx";
    public static readonly XName Tc = W + "tc";
    public static readonly XName TcPr = W + "tcPr";
    public static readonly XName SectPr = W + "sectPr";
    public static readonly XName HeaderReference = W + "headerReference";
    public static readonly XName FooterReference = W + "footerReference";
    public static readonly XName AltChunk = W + "altChunk";
    public static readonly XName Drawing = W + "drawing";
    public static readonly XName Pict = W + "pict";
    public static readonly XName Object = W + "object";
    public static readonly XName Control = W + "control";
    public static readonly XName TxbxContent = W + "txbxContent";
    public static readonly XName Comment = W + "comment";
    public static readonly XName Footnote = W + "footnote";
    public static readonly XName Endnote = W + "endnote";

    public static readonly XName WId = W + "id";
    public static readonly XName WName = W + "name";
    public static readonly XName WType = W + "type";
    public static readonly XName XmlSpace = XNamespace.Xml + "space";
    public static readonly XName ParaId = Ns.W14 + "paraId";

    public static readonly XName AlternateContent = Ns.MC + "AlternateContent";
    public static readonly XName Choice = Ns.MC + "Choice";
    public static readonly XName Fallback = Ns.MC + "Fallback";
    public static readonly XName Ignorable = Ns.MC + "Ignorable";

    public static readonly XName OMath = Ns.M + "oMath";
    public static readonly XName OMathPara = Ns.M + "oMathPara";

    public static readonly XName WpInline = Ns.WP + "inline";
    public static readonly XName WpAnchor = Ns.WP + "anchor";
    public static readonly XName WpDocPr = Ns.WP + "docPr";

    public static readonly XName IwRuns = Ns.IW + "runs";
    public static readonly XName IwBlocks = Ns.IW + "blocks";
}

/// <summary>Reading and writing part XML, and small XML helpers shared by the reader and the writer.</summary>
internal static class DocxXml
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        CheckCharacters = false,
        IgnoreProcessingInstructions = false,
    };

    public static XDocument Load(Stream stream)
    {
        using var reader = XmlReader.Create(stream, ReaderSettings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    public static XDocument Load(OpenXmlPart part)
    {
        using var s = part.GetStream(FileMode.Open, FileAccess.Read);
        return Load(s);
    }

    public static byte[] ReadAllBytes(OpenXmlPart part)
    {
        using var s = part.GetStream(FileMode.Open, FileAccess.Read);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Saves a part's XML as UTF-8 with Word's declaration (standalone="yes").</summary>
    public static void Save(XDocument doc, OpenXmlPart part)
    {
        using var s = part.GetStream(FileMode.Create, FileAccess.Write);
        Save(doc, s);
    }

    public static void Save(XDocument doc, Stream stream)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            NewLineHandling = NewLineHandling.None,
            CheckCharacters = true,
        };
        if (doc.Declaration == null || doc.Declaration.Standalone != "yes") doc.Declaration = new XDeclaration("1.0", "UTF-8", "yes");
        using var writer = XmlWriter.Create(stream, settings);
        doc.Save(writer);
    }

    public static void Save(XElement root, OpenXmlPart part) => Save(new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root), part);

    /// <summary>The element with its attributes (namespace declarations included) but no content.</summary>
    public static XElement Shell(XElement e) => new(e.Name, e.Attributes());

    /// <summary>ST_OnOff attribute: missing → false; "0", "false", "off" → false; anything else → true.</summary>
    public static bool OnOffAttr(XElement e, XName name)
    {
        var v = e.Attribute(name)?.Value;
        return v != null && !(v == "0" || v == "false" || v == "off");
    }

    public static bool OnOffAttr(XElement e, string wLocalName) => OnOffAttr(e, Ns.W + wLocalName);

    public static long? ParseLong(string? s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>The id the media store gives these bytes (first 10 bytes of the SHA-256, hex).</summary>
    public static string MediaHash(byte[] data)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(data);
        var sb = new StringBuilder(20);
        for (var i = 0; i < 10; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>Concatenated text of w:t / m:t descendants (for read-only display).</summary>
    public static string VisibleText(XElement e)
    {
        var sb = new StringBuilder();
        foreach (var d in e.Descendants())
        {
            if (d.Name == Wn.T || d.Name == Ns.M + "t") sb.Append(d.Value);
            else if (d.Name == Wn.Tab) sb.Append('\t');
            else if (d.Name == Wn.Br || d.Name == Ns.W + "cr") sb.Append(' ');
        }
        return sb.ToString();
    }

    /// <summary>The relationship ids a part uses (parts, external, hyperlink, data part references).</summary>
    public static IEnumerable<string> RelationshipIds(OpenXmlPartContainer part)
    {
        foreach (var p in part.Parts) yield return p.RelationshipId;
        foreach (var r in part.ExternalRelationships) yield return r.Id;
        foreach (var r in part.HyperlinkRelationships) yield return r.Id;
        foreach (var r in part.DataPartReferenceRelationships) yield return r.Id;
    }

    /// <summary>A relationship id not used by the part (nor in <paramref name="reserved"/>).</summary>
    public static string NewRelId(OpenXmlPartContainer part, ICollection<string>? reserved = null)
    {
        var used = new HashSet<string>(RelationshipIds(part), StringComparer.Ordinal);
        if (reserved != null) used.UnionWith(reserved);
        var max = 0;
        foreach (var id in used)
        {
            if (id.StartsWith("rId", StringComparison.Ordinal) && int.TryParse(id.Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                max = Math.Max(max, n);
        }
        for (var n = max + 1; ; n++)
        {
            var id = "rId" + n.ToString(CultureInfo.InvariantCulture);
            if (!used.Contains(id)) return id;
        }
    }
}
