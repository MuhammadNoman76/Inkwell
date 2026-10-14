using System.Globalization;
using System.Text;

namespace Inkwell.Core.Formats;

/// <summary>The parts of a CF_HTML clipboard payload.</summary>
public sealed class CfHtmlParts
{
    public CfHtmlParts(string html, string fragment, string? sourceUrl, bool fromOffsets)
    {
        Html = html;
        Fragment = fragment;
        SourceUrl = sourceUrl;
        FromOffsets = fromOffsets;
    }

    /// <summary>The HTML document (StartHTML…EndHTML), with &lt;!--StartFragment--&gt; / &lt;!--EndFragment--&gt; comments around the fragment.</summary>
    public string Html { get; }

    /// <summary>The fragment itself (StartFragment…EndFragment).</summary>
    public string Fragment { get; }

    /// <summary>The page the content was copied from (SourceURL), if given.</summary>
    public string? SourceUrl { get; }

    /// <summary>True if the header's byte offsets were valid (false: the comments or the whole text were used).</summary>
    public bool FromOffsets { get; }
}

/// <summary>
/// The Windows clipboard's HTML format (CF_HTML, "HTML Format"): a header with byte offsets (in the UTF-8 payload)
/// of the HTML document and of the fragment that was copied, followed by the HTML.
/// </summary>
public static class CfHtml
{
    private const string StartMarker = "<!--StartFragment-->";
    private const string EndMarker = "<!--EndFragment-->";

    /// <summary>
    /// The CF_HTML payload for an HTML fragment: a small HTML document (with <paramref name="head"/> in its head) whose
    /// body holds the fragment between the StartFragment / EndFragment comments, behind a header whose offsets count
    /// UTF-8 bytes.
    /// </summary>
    public static string Build(string fragment, string? head = null, string? sourceUrl = null)
    {
        var before = "<html><head><meta charset=\"utf-8\">" + (head ?? "") + "</head><body>\r\n" + StartMarker;
        var after = EndMarker + "\r\n</body></html>";
        return BuildFromParts(before, fragment, after, sourceUrl);
    }

    private static string BuildFromParts(string before, string fragment, string after, string? sourceUrl)
    {
        // Fixed-width numbers make the header's own length known in advance.
        const string format = "0000000000";
        var headerTemplate = "Version:0.9\r\nStartHTML:" + format + "\r\nEndHTML:" + format + "\r\nStartFragment:" + format + "\r\nEndFragment:" + format + "\r\n" +
                             (sourceUrl != null ? "SourceURL:" + sourceUrl + "\r\n" : "");
        var utf8 = Encoding.UTF8;
        var headerBytes = utf8.GetByteCount(headerTemplate);
        var startHtml = headerBytes;
        var startFragment = startHtml + utf8.GetByteCount(before);
        var endFragment = startFragment + utf8.GetByteCount(fragment);
        var endHtml = endFragment + utf8.GetByteCount(after);
        string N(int v) => v.ToString(format, CultureInfo.InvariantCulture);
        var header = "Version:0.9\r\nStartHTML:" + N(startHtml) + "\r\nEndHTML:" + N(endHtml) + "\r\nStartFragment:" + N(startFragment) + "\r\nEndFragment:" + N(endFragment) + "\r\n" +
                     (sourceUrl != null ? "SourceURL:" + sourceUrl + "\r\n" : "");
        return header + before + fragment + after;
    }

    /// <summary>Reads a CF_HTML payload given as the clipboard's bytes (UTF-8).</summary>
    public static CfHtmlParts Parse(byte[] payload)
    {
        var length = payload.Length;
        while (length > 0 && payload[length - 1] == 0) length--;
        return ParseBytes(payload, length);
    }

    /// <summary>Reads a CF_HTML payload given as text (it is re-encoded as UTF-8 to apply the byte offsets).</summary>
    public static CfHtmlParts Parse(string payload) => Parse(Encoding.UTF8.GetBytes(payload.TrimEnd('\0')));

    private static CfHtmlParts ParseBytes(byte[] bytes, int length)
    {
        var headerEnd = 0;
        // The header is ASCII lines "Name:value" before the first '<'.
        while (headerEnd < length && bytes[headerEnd] != (byte)'<') headerEnd++;
        var header = Encoding.ASCII.GetString(bytes, 0, Math.Min(headerEnd, 4096));
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in header.Split('\r', '\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            values[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
        }
        int Get(string name) => values.TryGetValue(name, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
        var startHtml = Get("StartHTML");
        var endHtml = Get("EndHTML");
        var startFragment = Get("StartFragment");
        var endFragment = Get("EndFragment");
        values.TryGetValue("SourceURL", out var source);
        var utf8 = Encoding.UTF8;
        var fragmentValid = startFragment >= 0 && endFragment >= startFragment && endFragment <= length && startFragment >= headerEnd;
        if (startHtml < 0 || startHtml > length) startHtml = headerEnd;
        if (endHtml < startHtml || endHtml > length) endHtml = length;
        if (fragmentValid && (startFragment < startHtml || endFragment > endHtml))
        {
            startHtml = Math.Min(startHtml, startFragment);
            endHtml = Math.Max(endHtml, endFragment);
        }
        string html;
        string fragment;
        if (fragmentValid)
        {
            var before = utf8.GetString(bytes, startHtml, startFragment - startHtml);
            fragment = utf8.GetString(bytes, startFragment, endFragment - startFragment);
            var after = utf8.GetString(bytes, endFragment, endHtml - endFragment);
            // Make sure the comments mark the fragment, so the HTML converter can find it with its context.
            if (!before.EndsWith(StartMarker, StringComparison.OrdinalIgnoreCase)) before += StartMarker;
            if (!after.StartsWith(EndMarker, StringComparison.OrdinalIgnoreCase)) after = EndMarker + after;
            html = before + fragment + after;
            return new CfHtmlParts(html, fragment, source, true);
        }
        html = utf8.GetString(bytes, startHtml, endHtml - startHtml);
        var s = html.IndexOf(StartMarker, StringComparison.OrdinalIgnoreCase);
        var e = html.IndexOf(EndMarker, StringComparison.OrdinalIgnoreCase);
        fragment = s >= 0 && e > s ? html.Substring(s + StartMarker.Length, e - s - StartMarker.Length) : html;
        return new CfHtmlParts(html, fragment, source, false);
    }
}
