using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using Inkwell.Core.Model;
using Inkwell.Core.Rendering;

namespace Inkwell.Core.IO;

/// <summary>A font embedded in a document (w:embedRegular / Bold / Italic / BoldItalic in the font table).</summary>
public sealed class EmbeddedFont
{
    public EmbeddedFont(string family, bool bold, bool italic, byte[] data)
    {
        Family = family;
        Bold = bold;
        Italic = italic;
        Data = data;
    }

    public string Family { get; }
    public bool Bold { get; }
    public bool Italic { get; }

    /// <summary>The font file (TrueType / OpenType), already de-obfuscated.</summary>
    public byte[] Data { get; }
}

/// <summary>Embedded fonts: obfuscation as ECMA-376 Part 1 §17.8.1 specifies, and reading them back.</summary>
public static class DocxFonts
{
    public const string ObfuscatedFontContentType = "application/vnd.openxmlformats-officedocument.obfuscatedFont";

    /// <summary>
    /// The 16-byte key from a w:fontKey GUID ("{XXXXXXXX-XXXX-…}"): the GUID's bytes in the order its hex digits are
    /// written, reversed.
    /// </summary>
    public static byte[] KeyBytes(string fontKey)
    {
        var hex = new StringBuilder(32);
        foreach (var c in fontKey)
            if (Uri.IsHexDigit(c)) hex.Append(c);
        if (hex.Length != 32) throw new ArgumentException("A font key is a GUID.", nameof(fontKey));
        var key = new byte[16];
        for (var i = 0; i < 16; i++)
            key[15 - i] = byte.Parse(hex.ToString(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return key;
    }

    /// <summary>XORs the first 32 bytes of the font with the key (twice the 16-byte key). The same call undoes it.</summary>
    public static byte[] Obfuscate(byte[] font, string fontKey)
    {
        var key = KeyBytes(fontKey);
        var result = (byte[])font.Clone();
        for (var i = 0; i < 32 && i < result.Length; i++) result[i] ^= key[i % 16];
        return result;
    }

    public static byte[] Deobfuscate(byte[] data, string fontKey) => Obfuscate(data, fontKey);

    /// <summary>The fonts embedded in a loaded document (empty for new documents).</summary>
    public static IReadOnlyList<EmbeddedFont> Read(Document doc) => doc.Source == null ? Array.Empty<EmbeddedFont>() : Read(doc.Source.Bytes);

    /// <summary>The fonts embedded in a .docx package.</summary>
    public static IReadOnlyList<EmbeddedFont> Read(byte[] package)
    {
        var list = new List<EmbeddedFont>();
        try
        {
            using var ms = new MemoryStream(package, false);
            using var pkg = WordprocessingDocument.Open(ms, false);
            var table = pkg.MainDocumentPart?.FontTablePart;
            var root = table == null ? null : DocxXml.Load(table).Root;
            if (table == null || root == null) return list;
            foreach (var font in root.Elements(Ns.W + "font"))
            {
                var family = font.Attr("name");
                if (string.IsNullOrEmpty(family)) continue;
                foreach (var embed in font.Elements())
                {
                    var (ok, bold, italic) = embed.Name.LocalName switch
                    {
                        "embedRegular" => (true, false, false),
                        "embedBold" => (true, true, false),
                        "embedItalic" => (true, false, true),
                        "embedBoldItalic" => (true, true, true),
                        _ => (false, false, false),
                    };
                    var id = embed.Attribute(Ns.R + "id")?.Value;
                    if (!ok || id == null || !table.TryGetPartById(id, out var part)) continue;
                    var data = DocxXml.ReadAllBytes(part);
                    var key = embed.Attr("fontKey");
                    if (key != null && part.ContentType == ObfuscatedFontContentType)
                    {
                        try
                        {
                            data = Deobfuscate(data, key);
                        }
                        catch (ArgumentException)
                        {
                            continue;
                        }
                    }
                    list.Add(new EmbeddedFont(family!, bold, italic, data));
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A damaged font table just means no embedded fonts.
        }
        return list;
    }
}

internal sealed partial class DocxWriteSession
{
    private static readonly Dictionary<string, int> FontOrder = new(StringComparer.Ordinal)
    {
        ["altName"] = 0, ["panose1"] = 10, ["charset"] = 20, ["family"] = 30, ["notTrueType"] = 40, ["pitch"] = 50, ["sig"] = 60,
        ["embedRegular"] = 70, ["embedBold"] = 80, ["embedItalic"] = 90, ["embedBoldItalic"] = 100, ["*"] = 110,
    };

    private static readonly string[] EmbedNames = { "embedRegular", "embedBold", "embedItalic", "embedBoldItalic" };

    private void WriteFontTable()
    {
        var part = _main.FontTablePart;
        if (part == null && _doc.FontTable == null && !_isNew && !_options.EmbedFonts) return;
        part ??= MainPart<FontTablePart>(null);
        var x = _doc.FontTable != null ? new XDocument(_doc.FontTable) : NewXml(Ns.NewRoot(Ns.W + "fonts"));
        if (x.Root == null) x.Add(Ns.NewRoot(Ns.W + "fonts"));
        var root = x.Root!;
        if (_options.EmbedFonts) EmbedFonts(part, root);

        // Embedding elements must point at font parts that exist; font parts nothing points at go.
        var valid = new HashSet<string>(part.Parts.Select(p => p.RelationshipId), StringComparer.Ordinal);
        foreach (var e in root.Descendants().Where(e => e.Name.Namespace == Ns.W && EmbedNames.Contains(e.Name.LocalName)).ToList())
        {
            var id = e.Attribute(Ns.R + "id")?.Value;
            if (id == null || !valid.Contains(id)) e.Remove();
        }
        var referenced = new HashSet<string>(root.Descendants().Select(e => e.Attribute(Ns.R + "id")?.Value).Where(v => v != null)!, StringComparer.Ordinal);
        foreach (var pair in part.Parts.ToList())
            if (pair.OpenXmlPart is FontPart && !referenced.Contains(pair.RelationshipId)) part.DeletePart(pair.RelationshipId);
        SaveOwned(x, part);
    }

    private void EmbedFonts(FontTablePart part, XElement root)
    {
        foreach (var family in UsedFonts())
        {
            if (!FontCatalog.IsBundled(family)) continue;
            List<(string File, bool Bold, bool Italic)> files;
            try
            {
                files = FontCatalog.FilesFor(family).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                files = new List<(string, bool, bool)>();
            }
            if (files.Count == 0) continue;

            var font = root.Elements(Ns.W + "font").FirstOrDefault(f => string.Equals(f.Attr("name"), family, StringComparison.OrdinalIgnoreCase));
            if (font == null)
            {
                font = new XElement(Ns.W + "font", new XAttribute(Ns.W + "name", family),
                    new XElement(Ns.W + "charset", new XAttribute(Ox.Val, "00")),
                    new XElement(Ns.W + "family", new XAttribute(Ox.Val, "auto")),
                    new XElement(Ns.W + "pitch", new XAttribute(Ox.Val, "variable")));
                root.Add(font);
            }
            font.Elements().Where(e => e.Name.Namespace == Ns.W && EmbedNames.Contains(e.Name.LocalName)).Remove();
            foreach (var (file, bold, italic) in files)
            {
                byte[] data;
                try
                {
                    data = File.ReadAllBytes(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Warn("The font " + family + " couldn't be embedded.");
                    continue;
                }
                var key = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}";
                var id = DocxXml.NewRelId(part);
                var fontPart = part.AddFontPart(FontPartType.FontOdttf, id);
                var obfuscated = DocxFonts.Obfuscate(data, key);
                using (var s = fontPart.GetStream(FileMode.Create, FileAccess.Write)) s.Write(obfuscated, 0, obfuscated.Length);
                var name = bold && italic ? "embedBoldItalic" : bold ? "embedBold" : italic ? "embedItalic" : "embedRegular";
                font.Add(new XElement(Ns.W + name, new XAttribute(Ns.R + "id", id), new XAttribute(Ns.W + "fontKey", key)));
                _fontsEmbedded = true;
            }
            SchemaOrder.Sort(font, FontOrder);
        }
    }

    /// <summary>Font names the document uses: theme fonts and w:rFonts in the parts, styles and numbering.</summary>
    private IEnumerable<string> UsedFonts()
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name!.Trim())) names.Add(name.Trim());
        }
        foreach (var major in new[] { true, false })
            foreach (var slot in new[] { "latin", "ea", "cs" })
                Add(_doc.Theme.Font(major, slot));
        var roots = _trees.Where(t => !t.Skip).Select(t => t.Xml.Root!).Concat(new[] { _doc.Styles.Root, _doc.Numbering.Root });
        foreach (var root in roots)
        {
            foreach (var e in root.Descendants(Ns.W + "rFonts"))
            {
                Add(e.Attr("ascii"));
                Add(e.Attr("hAnsi"));
                Add(e.Attr("eastAsia"));
                Add(e.Attr("cs"));
            }
        }
        return names;
    }
}
