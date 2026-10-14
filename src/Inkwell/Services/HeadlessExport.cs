namespace Inkwell.Services;

/// <summary>--export-pdf &lt;in&gt; &lt;out.pdf&gt;: converts a document without opening a window. Exit code 0 on success.</summary>
internal static class HeadlessExport
{
    public static int Run(Command command)
    {
        try
        {
            if (command.Paths.Count == 0 || command.Output == null)
            {
                Console.Error.WriteLine("Usage: Inkwell.exe --export-pdf <document.docx|.md|.txt|.rtf|.odt> <output.pdf>");
                return 2;
            }
            var input = command.Paths[0];
            var doc = Core.IO.DocumentFiles.Open(input).Document;
            Core.Export.PdfExporter.Export(doc, command.Output, new Core.Export.PdfExportOptions { FileName = Path.GetFileName(input) });
            Log.Info($"Exported {input} to {command.Output}");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Export PDF from the command line");
            try { Console.Error.WriteLine(ex.Message); } catch { /* no console */ }
            return 1;
        }
    }
}
