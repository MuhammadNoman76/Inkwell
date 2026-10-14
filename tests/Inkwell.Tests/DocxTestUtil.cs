using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Inkwell.Core.IO;
using Inkwell.Core.Model;

namespace Inkwell.Tests;

/// <summary>Helpers for the .docx tests: save / load in memory, schema validation, reading parts.</summary>
internal static class Docx
{
    public static readonly XNamespace W = Ns.W;

    public static byte[] Save(Document doc, DocxSaveOptions? options = null)
    {
        using var ms = new MemoryStream();
        return DocxWriter.Save(doc, ms, options);
    }

    public static Document Load(byte[] bytes) => DocxReader.Load(new MemoryStream(bytes, false), "test.docx");

    /// <summary>Saves, validates and loads again.</summary>
    public static Document RoundTrip(Document doc, out byte[] bytes, DocxSaveOptions? options = null)
    {
        bytes = Save(doc, options);
        AssertValid(bytes);
        return Load(bytes);
    }

    public static Document RoundTrip(Document doc) => RoundTrip(doc, out _);

    /// <summary>Validates the package against the newest schemas the Open XML SDK knows (Microsoft 365).</summary>
    public static void AssertValid(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, false);
        using var pkg = WordprocessingDocument.Open(ms, false);
        var validator = new OpenXmlValidator(FileFormatVersions.Microsoft365);
        var errors = validator.Validate(pkg).ToList();
        if (errors.Count == 0) return;
        var message = new StringBuilder("The package isn't valid:\n");
        foreach (var e in errors.Take(25))
            message.Append(e.Part?.Uri).Append(' ').Append(e.Path?.XPath).Append(": ").Append(e.Description).Append(" [").Append(e.Node?.OuterXml?.Length > 300 ? e.Node.OuterXml.Substring(0, 300) : e.Node?.OuterXml).Append("]\n");
        Assert.Fail(message.ToString());
    }

    public static IReadOnlyList<string> PartNames(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    public static byte[]? PartBytes(byte[] bytes, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, name.TrimStart('/'), StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static XDocument PartXml(byte[] bytes, string name)
    {
        var data = PartBytes(bytes, name) ?? throw new InvalidOperationException("No part " + name);
        using var ms = new MemoryStream(data);
        return XDocument.Load(ms);
    }

    public static XElement Body(byte[] bytes) => PartXml(bytes, "word/document.xml").Root!.Element(W + "body")!;

    /// <summary>The content type the package gives a part.</summary>
    public static string? ContentTypeOf(byte[] bytes, string partName)
    {
        using var ms = new MemoryStream(bytes, false);
        using var pkg = WordprocessingDocument.Open(ms, false);
        foreach (var part in AllParts(pkg))
            if (string.Equals(part.Uri.OriginalString, partName, StringComparison.OrdinalIgnoreCase)) return part.ContentType;
        return null;
    }

    public static IEnumerable<OpenXmlPart> AllParts(OpenXmlPartContainer container)
    {
        var seen = new HashSet<OpenXmlPart>();
        var stack = new Stack<OpenXmlPartContainer>();
        stack.Push(container);
        while (stack.Count > 0)
        {
            foreach (var pair in stack.Pop().Parts)
            {
                if (!seen.Add(pair.OpenXmlPart)) continue;
                yield return pair.OpenXmlPart;
                stack.Push(pair.OpenXmlPart);
            }
        }
    }

    public static string Text(Document doc) => string.Join("\n", doc.Body.Paragraphs.Select(p => p.PlainText()));

    public static Paragraph Para(string text, string? style = null) => new(text, style);
}

/// <summary>Builds .docx packages with the Open XML SDK directly (as Word or other producers would write them).</summary>
internal static class TestDocx
{
    public const string Declarations =
        "xmlns:wpc=\"http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas\" " +
        "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
        "xmlns:o=\"urn:schemas-microsoft-com:office:office\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\" " +
        "xmlns:v=\"urn:schemas-microsoft-com:vml\" " +
        "xmlns:wp14=\"http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
        "xmlns:w10=\"urn:schemas-microsoft-com:office:word\" " +
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
        "xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\" " +
        "xmlns:w15=\"http://schemas.microsoft.com/office/word/2012/wordml\" " +
        "xmlns:wpg=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\" " +
        "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\" " +
        "mc:Ignorable=\"w14 w15 wp14\"";

    public const string SectPr = "<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr>";

    /// <summary>
    /// A package with the given body XML (w:document / w:body are added, and a final sectPr unless the body has one).
    /// <paramref name="setup"/> runs first, to add parts the body refers to.
    /// </summary>
    public static byte[] Create(string bodyXml, Action<WordprocessingDocument, MainDocumentPart>? setup = null,
        WordprocessingDocumentType type = WordprocessingDocumentType.Document, bool styles = true)
    {
        using var ms = new MemoryStream();
        using (var pkg = WordprocessingDocument.Create(ms, type))
        {
            var main = pkg.AddMainDocumentPart();
            setup?.Invoke(pkg, main);
            var sect = bodyXml.Contains("<w:sectPr") && bodyXml.TrimEnd().EndsWith("</w:sectPr>", StringComparison.Ordinal) ? "" : SectPr;
            Write(main, "<w:document " + Declarations + "><w:body>" + bodyXml + sect + "</w:body></w:document>");
            if (styles)
            {
                var sp = main.AddNewPart<StyleDefinitionsPart>("rIdStyles");
                Write(sp, "<w:styles " + Declarations + "><w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val=\"22\"/></w:rPr></w:rPrDefault><w:pPrDefault/></w:docDefaults>" +
                          "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style></w:styles>");
            }
        }
        return ms.ToArray();
    }

    public static void Write(OpenXmlPart part, string xml)
    {
        using var s = part.GetStream(FileMode.Create, FileAccess.Write);
        var b = new UTF8Encoding(false).GetBytes(xml);
        s.Write(b, 0, b.Length);
    }

    public static void Write(OpenXmlPart part, byte[] data)
    {
        using var s = part.GetStream(FileMode.Create, FileAccess.Write);
        s.Write(data, 0, data.Length);
    }

    public static string P(string text) => "<w:p><w:r><w:t xml:space=\"preserve\">" + System.Security.SecurityElement.Escape(text) + "</w:t></w:r></w:p>";

    /// <summary>An inline picture referring to image relationship <paramref name="relId"/>.</summary>
    public static string InlinePicture(string relId, int id = 1, long cx = 914400, long cy = 685800, string extraSpPr = "", string blipFillExtra = "") =>
        "<w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"" + cx + "\" cy=\"" + cy + "\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/>" +
        "<wp:docPr id=\"" + id + "\" name=\"Picture " + id + "\" descr=\"A picture\"/><wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect=\"1\"/></wp:cNvGraphicFramePr>" +
        "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:nvPicPr><pic:cNvPr id=\"0\" name=\"image.png\"/><pic:cNvPicPr/></pic:nvPicPr>" +
        "<pic:blipFill><a:blip r:embed=\"" + relId + "\"/>" + blipFillExtra + "<a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
        "<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"" + cx + "\" cy=\"" + cy + "\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" + extraSpPr + "</pic:spPr>" +
        "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing>";

    public static ImagePart AddImage(OpenXmlPart part, string relId, byte[] png)
    {
        var image = part.AddNewPart<ImagePart>("image/png", relId);
        Write(image, png);
        return image;
    }
}
