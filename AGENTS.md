# Inkwell: notes for coding agents

Shared project memory for coding assistants (Codex, Claude Code, Gemini CLI, …). `CLAUDE.md` and `GEMINI.md` load this file. Keep it up to date when something important changes.

## What this is
Inkwell is a free Windows word processor ("like Microsoft Word, but modern, better-looking, and without Word's irritations"), built in C#/WPF.
- Owner: Muhammad Noman (https://github.com/MuhammadNoman76). No repository on GitHub yet: commit locally, never push.
- Tagline: "Write beautifully. Without fighting your document."
- Look: Windows 11 Fluent like Stagecraft and Trove (Mica, light / dark and the accent colour, Segoe UI Variable, vector Fluent System Icons). No ribbon: a slim command bar, a contextual panel on the right, an optional outline pane on the left, pages in the middle.
- Opens and saves .docx natively and keeps everything it doesn't understand.

## Layout (work in progress; see the end of this file for the final layout)
- `src/Shared/`: `Compat.cs`, `LegacyPolyfills.cs`, `MathEx.cs`, compiled into both assemblies (namespace `Inkwell`).
- `src/Inkwell.Core/`: the engine (no windows; the tests use it directly).
  - `Model/`: the document model, close to WordprocessingML.
    - Properties are XML-backed (`PropBag` subclasses `RunProps`, `ParaProps`, `SectProps`, `TableProps`, `RowProps`, `CellProps` wrap the real `w:rPr`, `w:pPr`… elements), so every attribute and extension survives a round trip. Typed accessors read / write what Inkwell understands; `SchemaOrder` inserts new children in schema order.
    - Styles, numbering, settings and theme parts are kept as `XDocument`s with typed accessors (`StyleSheet`, `Numbering`, `DocSettings`, `DocTheme`).
    - Stories (`MainStory`, `HeaderFooter`, `Note`, `Comment`) hold a `BlockList` of blocks: `Paragraph`, `Table` (rows → cells → blocks), `SdtBlock` (content control; TOC and bibliography are managed, others read-only), `FieldBlock` (a field whose result spans paragraphs), `OpaqueBlock` (kept as XML, read-only), `MarkerBlock` (bookmark / comment markers between blocks).
    - Paragraph content is a list of `Inline`s. Text lives in `TextRun`s with Word's control characters: tab `\t`, line break `\v`, page break `\f`, column break U+000E, soft hyphen U+00AD, non-breaking hyphen U+2011. Objects (fields, pictures in line, note references, symbols, opaque items) have length 1; markers (bookmarks, comment ranges, floating-drawing anchors) have length 0. A position in a paragraph is an offset in these units.
    - Hyperlinks, tracked insertions / deletions are properties of inlines (`Inline.Link`, `Inserted`, `Deleted`); consecutive inlines sharing a `Hyperlink` instance form one `w:hyperlink`.
    - Fields: `FieldInline` (one paragraph: code + result runs) and `FieldBlock` (several paragraphs). Fields with nested fields in their code are kept as `PreservedXml` (read-only).
    - Pictures: `DrawingObject` with the picture's bytes in `Document.Media` (by SHA-256), size in EMU, wrap mode and float position; `Original` keeps the `w:drawing` XML so effects and crops survive (the writer patches size / wrap / position into it).
    - `Document.Source` keeps the original package bytes: saving starts from them, so parts Inkwell doesn't rewrite (macros, charts, SmartArt, custom XML, embeddings…) stay byte for byte.
  - `Templates/`: `DocumentDesigns` (the designs: style sets built on theme fonts).
  - `Formats/`: formats other than .docx: Markdown import / export (Markdig), HTML export (page, clipboard CF_HTML), HTML import for pasting (match-destination or keep-source formatting; Word and Google Docs clipboard HTML), plain text, RTF and ODT import. Paste converters return a `PasteResult`: blocks that use the target document's styles, with their lists, pictures and notes already added to it.

## Units
- Lengths in the model are twips (1/1440 inch), font sizes half-points, borders eighths of a point, drawings EMU (914 400 per inch) — exactly as in the XML. Layout works in WPF device-independent pixels (1/96 inch): twips / 15.

## Compatibility rules (don't break)
- The code must compile for both net10.0-windows and net48 with 0 warnings. Use `Compat`, `MathEx.Clamp`, the polyfills.
- No `Math.Clamp`, `Environment.ProcessPath`, `File.Move(…, true)`, `Random.Shared` directly (use `Compat.NextRandom`).
- 32-bit: never import `GetWindowLongPtrW`/`SetWindowLongPtrW` directly; use the `IntPtr.Size` wrappers in `Interop/Native.cs`.
- Windows 7/8.1: guard Windows 10/11-only APIs (Mica, dark title bar, ISpellChecker on Windows 7).

## Git and writing rules
- Commit as `Muhammad Noman <muhammadnomanshafiq76@gmail.com>`.
- Never add AI attribution: no `Co-Authored-By` trailer and no "Generated with …" line.
- Don't mention AI in commit messages, README, release notes or repo descriptions.
- Never push.
- Read and write text files as UTF-8. PowerShell `Get-Content`/`Set-Content` without `-Encoding` corrupts characters such as · – ’ …
- Public claims (README) must match the code. Don't invent features or numbers, and say what is untested.
- No LICENSE file: the owner will choose one.

## Testing on the owner's PC (be careful)
- The owner uses VibePlayer, PawSip, Trove and Stagecraft daily: never close or touch them, and don't change their file associations.
- Never open windows over a full-screen app (check `SHQueryUserNotificationState`). Prefer the off-screen test mode.
- Don't send keystrokes to "whatever is in front" (SendKeys / keybd_event / SendInput): post messages only to Inkwell's own windows (check the PID). Capture screenshots with PrintWindow on Inkwell's own windows only.
- Back up `%APPDATA%\Inkwell` and `%LOCALAPPDATA%\Inkwell` before test runs and restore them afterwards.
- Never modify the owner's documents. Never change the .docx association or default apps.
- Microsoft Word (COM): only when WINWORD.EXE is not already running; Visible = false; only on generated files in a scratch folder; quit Word afterwards and make sure no WINWORD.EXE we started remains.
- The engine tests write only under `%TEMP%\inkwell-tests-*` and delete it.
