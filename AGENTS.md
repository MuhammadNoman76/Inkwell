# Inkwell: notes for coding agents

Shared project memory for coding assistants (Codex, Claude Code, Gemini CLI, …). `CLAUDE.md` and `GEMINI.md` load this file. Keep it up to date when something important changes.

## What this is
Inkwell is a free Windows word processor ("like Microsoft Word, but modern, better-looking, and without Word's irritations"), built in C#/WPF.
- Owner: Muhammad Noman (https://github.com/MuhammadNoman76). Repository: https://github.com/MuhammadNoman76/Inkwell (capital I). Commit locally; the owner pushes after review (never push yourself).
- Download page: https://www.nomanshafiq.com/inkwell/. Installers are linked as `https://github.com/MuhammadNoman76/Inkwell/raw/refs/heads/main/release/<file>`, checksums at `https://github.com/MuhammadNoman76/Inkwell/blob/main/release/SHA256SUMS.txt`. Update feed: `https://www.nomanshafiq.com/updates/inkwell.json` (`INKWELL_UPDATE_URL` overrides it).
- Tagline: "Write beautifully. Without fighting your document."
- Look: Windows 11 Fluent like Stagecraft and Trove (Mica, light / dark and the accent colour, Segoe UI Variable, vector Fluent System Icons). No ribbon: a slim command bar, a contextual panel on the right, an optional outline pane on the left, pages in the middle.
- Opens and saves .docx natively and keeps everything it doesn't understand.

## Layout
- `src/Shared/`: `Compat.cs`, `LegacyPolyfills.cs`, `MathEx.cs`, compiled into both assemblies (namespace `Inkwell`).
- `src/Inkwell.Core/`: the engine (no windows; the tests use it directly).
  - `Model/`: the document model, close to WordprocessingML.
    - Properties are XML-backed (`PropBag` subclasses `RunProps`, `ParaProps`, `SectProps`, `TableProps`, `RowProps`, `CellProps` wrap the real `w:rPr`, `w:pPr`… elements), so every attribute and extension survives a round trip. Typed accessors read / write what Inkwell understands; `SchemaOrder` inserts new children in schema order (header and footer references share a position: they're one choice group).
    - Styles, numbering, settings and theme parts are kept as `XDocument`s with typed accessors (`StyleSheet`, `Numbering`, `DocSettings`, `DocTheme`).
    - Stories (`MainStory`, `HeaderFooter`, `Note`, `Comment`) hold a `BlockList` of blocks: `Paragraph`, `Table` (rows → cells → blocks), `SdtBlock` (content control; TOC and bibliography are managed, others read-only), `FieldBlock` (a field whose result spans paragraphs), `OpaqueBlock` (kept as XML, read-only), `MarkerBlock` (bookmark / comment markers between blocks).
    - Paragraph content is a list of `Inline`s. Text lives in `TextRun`s with Word's control characters: tab `\t`, line break `\v`, page break `\f`, column break U+000E, soft hyphen U+00AD, non-breaking hyphen U+2011. Objects (fields, pictures in line, note references, symbols, opaque items) have length 1; markers (bookmarks, comment ranges, floating-drawing anchors) have length 0. A position in a paragraph is an offset in these units.
    - Hyperlinks, tracked insertions / deletions are properties of inlines (`Inline.Link`, `Inserted`, `Deleted`); consecutive inlines sharing a `Hyperlink` instance form one `w:hyperlink`.
    - Fields: `FieldInline` (one paragraph: code + result runs) and `FieldBlock` (several paragraphs). Fields with nested fields in their code are kept as `PreservedXml` (read-only).
    - Pictures: `DrawingObject` with the picture's bytes in `Document.Media` (by SHA-256), size in EMU, wrap mode and float position; `Original` keeps the `w:drawing` XML so effects and crops survive (the writer patches size / wrap / position into it).
    - `Document.Source` keeps the original package bytes: saving starts from them, so parts Inkwell doesn't rewrite (macros, charts, SmartArt, custom XML, embeddings…) stay byte for byte.
    - `DocBuilder` (light markup for templates, demos and tests), `DocumentCloner` (deep copy for background saves and exports).
  - `Styles/`: `StyleResolver` (docDefaults → table style → paragraph style chain → numbering → character style → direct; toggles XOR; theme fonts; per-script font slots).
  - `Layout/`: the layout engine. `ParaSource` feeds WPF's `TextFormatter` (shaping, bidi, line breaking); `LineFormatter` formats each line with explicit geometry (so floats can narrow lines and right-to-left lines work); `Paginator` does pages, columns (with balancing of continuous sections), keep-with-next, widow / orphan control, keep-lines, tables across pages (repeated header rows, rows that don't split), footnotes per column, floating pictures with wrapping, and pageless view; `DocumentLayout` keeps pages and relays out incrementally from the first changed page; `HitTesting` (caret, hit test, selection rectangles).
    - Right-to-left: a TextLine of a right-to-left paragraph measures in page coordinates but draws its content as if its gap were on the right; `PageRenderer.DrawParagraph` shifts it (`2·Start + Width − FormatWidth`). Inline objects draw above the baseline (WPF passes the baseline point as the origin).
    - The empty last paragraph after a final table is squeezed onto the table's last page; a page break right before a paragraph mark keeps the mark on the break's line (no empty line, no blank last page).
  - `Rendering/`: `FontCatalog` (bundled fonts, loaded privately from `<app>\Fonts`; substitutes for missing fonts), `MediaImages` (decoded pictures, frozen), `PageRenderer` (pages for the editor, thumbnails, PDF and printing; formatting marks and labelled break markers).
  - `Editing/`: `DocEditor` (all edits as transactions: `Edit(label, t => …)` touches what it changes; `UndoStack` merges typing; automatic changes — autocorrect, Markdown shortcuts — are their own undo steps), `Positions` (`DocPos`, `Selection`, `Nav`), formatting, objects, tables, document-level commands (sections, landscape page, headers / footers, TOC, captions, cross-references, citations), review (accept / reject), `Clipboard` (`DocFragment`, `PasteNormalizer`: match destination / keep source / text only), `FindReplace`, `RevealFormatting` + `StyleConsistency`, `AutoCorrect` + `MarkdownShortcuts` (pure text rules that return edits), `DocumentStats` (counts like Word's: list numbers count as words, "•" bullets don't).
  - `Templates/`: `DocumentDesigns` (six designs: style sets on theme fonts and colours) and `TemplateCatalog` (eight templates).
  - `Citations/`: APA 7, MLA 9, Chicago 17 (author-date) and IEEE. `CitationEngine`, `CitationField` / `BibliographyField` (Word's field codes), `CitationModel`. Output is `StyledText`.
  - `Proofing/`: `WindowsSpellChecker` (the Windows 8+ Spell Checking API, hand-declared COM, on a private MTA thread; unavailable on Windows 7). Tests must never call `Add`: it changes the user's Windows dictionary.
  - `IO/`: `DocxReader` / `DocxWriter` (+ `DocxFixups`, `DocxFonts` for embedding the bundled fonts), `DocumentFiles` (open / save / export / clipboard formats with friendly errors; atomic writes), `VersionHistory` (snapshots in `%LOCALAPPDATA%\Inkwell\History`), `RecoveryStore` (crash copies in `%LOCALAPPDATA%\Inkwell\Recovery`).
    - The writer emits a complex field's result inside the same hyperlink / tracked change as its field characters: Word refuses a file whose field begins in one `w:hyperlink` and ends in another (the schema allows it, and the Open XML validator doesn't catch it; `DemoDocumentTests.AssertFieldsNotSplit` does).
  - `Formats/`: Markdown import / export (Markdig), HTML export (page, clipboard CF_HTML), HTML import (pasting; files), plain text, RTF and ODT import.
  - `Export/PdfExporter`: draws the pages with PDFsharp (text as outlines in the real fonts) plus an invisible text layer (Inter, and Noto Naskh Arabic for Arabic-script text) so PDFs are searchable and copyable; bookmarks from headings, links. Runs on any STA thread.
  - `Samples/`: `DemoDocuments` (report, letter, résumé, essay, Urdu + English) and `SampleArt` (pictures drawn in code, PNG encoded without WPF).
- `src/Inkwell/`: the app.
  - `App.xaml.cs` (verbs: open files, `--export-pdf <in> <out>`, `--settings`, hidden `--capture <folder>`), `Services/` (`AppSettings` in `%APPDATA%\Inkwell`, `CommandLine`, `InstanceManager` (one process; later launches hand over their files), `Log`, `Updater` / `UpdateService`, `Recovery`, `HeadlessExport`, `CaptureRunner`).
  - `Views/MainWindow` (one document per window; busy overlay, toasts), `StartView` (templates, recent files with thumbnails, recovered work, the tour of what Inkwell fixes), `EditorView` (partial files: `.xaml.cs` state and panels, `Commands` (the command table: shortcuts and the palette), `Typing` (autocorrect, chips, Markdown, right-to-left typing, Tab), `Slash`, `Clipboard` (paste chip, drag and drop), `Menus`, `Insert` (+ format painter), `Navigation` (links, TOC jumps, headers / footers, right-click menu), `Spelling`, `Status` (word count, goal ring, comment highlights), `Files` (save, automatic save on a background copy, recovery, version history, open notice), `Testing` (hooks for capture mode)).
  - `Views/Shortcuts.cs`: every keyboard shortcut, in one table. The command table binds them; menus, tooltips, the palette and messages show `Shortcuts.X.Text` (the key names of the keyboard layout in use; `UsText` for the README). Never write a key name in UI text: `ShortcutTests` fails on duplicates, on keys outside the table, on hand-written shortcuts and on a README keyboard table that doesn't match.
  - `Views/Editing/DocumentView`: the page surface (IScrollInfo; cached page visuals; overlay for selection, highlights, squiggles, focus dimming, picture handles, anchor mark, guides; caret; IME via Imm32 + TSF; UI Automation text pattern).
  - `Views/Panels/`: `ContextPanel` (Format / Picture / Table / Page / Review / Reveal / Design), `OutlinePane`, `FindBar`, `CommandPalette`, `Pickers` (fonts, symbols).
  - `Views/Dialogs/`: `InkDialog` (standard dialog), export / print, version history, citations, link, caption, cross-reference, properties, word count.
  - `Controls/`, `Interop/` (`Native.cs`: 32-bit safe wrappers, `CaptureOwnWindow`, `IsUserBusy`), `Themes/` (`Theme.xaml`, `Editor.xaml`, `Icons.xaml` from Fluent UI System Icons, `ThemeManager`), `Assets/` (icons).
- `tests/Inkwell.Tests/`: xUnit, both targets; references the engine and the app (`ShortcutTests`, `UpdaterTests`: AppId, install scope, version numbers, no other app's identifiers; `SettingsTests`). `Sta.Run` for anything that touches WPF. `INKWELL_RENDER_OUT` renders pages to PNG; `INKWELL_SAMPLES_OUT` writes the samples; `INKWELL_PERF_OUT` appends the 300-page numbers; `INKWELL_FIDELITY_DIR` writes the Word comparison set.
- `installer/Inkwell.iss`: one Inno Setup script for every edition (`/DEdition=`, `/DAppVersion=`); AppId `{B1102FC4-9C59-4C92-B3B7-4D1F6648BA1B}` (`Updater.InstallerAppId` must match: a test checks). "Open with" for .docx / .md as optional tasks; never the default app.
- `build.ps1`, `tools/` (`get-fonts.ps1`, `make-icon.ps1`, `make-icons.mjs`, `word-fidelity.ps1`), `samples/` (demo .docx with fonts embedded, and their PDFs), `docs/` (icon and README screenshots), `release/` (installers + `SHA256SUMS.txt`), `publish/` and `dist/` (build output, git-ignored).

## Editions (one codebase)
| Edition | Target | Installer |
|---|---|---|
| x64 | net10.0-windows, win-x64, self-contained | `Inkwell-Setup-<ver>-x64.exe` (Windows 10/11) |
| arm64 | net10.0-windows, win-arm64, self-contained | `-arm64.exe` |
| x86 | net10.0-windows, win-x86, self-contained | `-x86.exe` (32-bit Windows 10) |
| win7-8 | net48 AnyCPU | `-win7-8.exe` (Windows 7 SP1 / 8.1; needs .NET Framework 4.8) |
- The edition is embedded at build time (`-p:InkwellEdition=`, AssemblyMetadata "Edition"); the updater uses it. build.ps1 calls the Windows 7/8 edition `legacy` internally and `win7-8` in file names and the feed.
- Keep the same AppId for every edition. Each installer must stay under 100 MB (GitHub's limit); they're about 45–50 MB.

## Build and release
1. Bump `<Version>` in `src/Inkwell/Inkwell.csproj` and `src/Inkwell.Core/Inkwell.Core.csproj`.
2. `powershell -ExecutionPolicy Bypass -File build.ps1 -Version X.Y.Z` (tests on both targets, every edition, installers, `release/` + `SHA256SUMS.txt`). The .NET SDK is at `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`; Inno Setup 6 at `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`.
3. Update the README's download table (bytes and SHA-256).
4. The website (separate repo) gets the version, the editions (exact bytes and SHA-256) and the update feed `public/updates/inkwell.json`.

## Test modes
- `INKWELL_TEST_OFFSCREEN=1`: windows open left of every display, never take the focus; settings stay in memory, logs go to `%TEMP%\Inkwell-test-logs`, nothing is written to `%APPDATA%\Inkwell` or `%LOCALAPPDATA%\Inkwell` (no recent files, thumbnails, recovery or history).
- `Inkwell.exe --capture <folder>` (`INKWELL_CAPTURE_ONLY=start,editor,…` to pick steps): the same off-screen mode, scripted; saves screenshots of Inkwell's own windows (PrintWindow on our own handles, never the screen), and `smoke.txt` with end-to-end checks (open, type, save, automatic save, undo, Markdown and text import, messages on view changes, autocorrect by Enter, "Stop doing this" and the Settings switch). The `fit` step lays the editor out from 940 to 3840 px wide and writes `fit.txt` (command bar overflow, text cut off in the bars, panels and dialogs at the narrowest window) and `bar-<width>.png`. Waits while another app is full screen.

## Units
- Lengths in the model are twips (1/1440 inch), font sizes half-points, borders eighths of a point, drawings EMU (914 400 per inch) — exactly as in the XML. Layout works in WPF device-independent pixels (1/96 inch): twips / 15.

## Compatibility rules (don't break)
- The code must compile for both net10.0-windows and net48 with 0 warnings. Use `Compat`, `MathEx.Clamp`, the polyfills.
- No `Math.Clamp`, `Environment.ProcessPath`, `File.Move(…, true)`, `Random.Shared` directly (use `Compat.NextRandom`).
- 32-bit: never import `GetWindowLongPtrW`/`SetWindowLongPtrW` directly; use the `IntPtr.Size` wrappers in `Interop/Native.cs`.
- Windows 7/8.1: guard Windows 10/11-only APIs (Mica, dark title bar, ISpellChecker on Windows 7).
- WPF objects shared across threads must be frozen (pictures, brushes); exports and previews run on their own STA threads with a `DocumentCloner` copy.

## Microsoft Word comparison
`INKWELL_FIDELITY_DIR=<folder> dotnet test --filter WordFidelityTests.WriteDocuments`, then `tools\word-fidelity.ps1 -Folder <folder>` (only when Word isn't running; invisible, macros off, read-only, nothing saved — `SaveAs2` could show prompts and hang). Last result (1.0.0): all 9 documents open in Word; 116 of 117 counts equal (pages, words, tables, pictures, notes, comments, sections, links, TOC, sources, revisions); the résumé is 2 pages in Inkwell and 1 in Word (a line beside the floating portrait wraps one word earlier).

## Git and writing rules
- Commit as `Muhammad Noman <muhammadnomanshafiq76@gmail.com>`.
- Never add AI attribution: no `Co-Authored-By` trailer and no "Generated with …" line.
- Don't mention AI in commit messages, README, release notes or repo descriptions.
- Never push.
- Read and write text files as UTF-8. PowerShell `Get-Content`/`Set-Content` without `-Encoding` corrupts characters such as · – ’ …
- Public claims (README, the start screen's tour) must match the code. Don't invent features or numbers, and say what is untested.
- No LICENSE file: the owner will choose one.

## Testing on the owner's PC (be careful)
- The owner uses VibePlayer, PawSip, Trove and Stagecraft daily: never close or touch them, and don't change their file associations.
- Never open windows over a full-screen app (check `SHQueryUserNotificationState`). Prefer the off-screen test mode and the capture mode.
- Don't send keystrokes to "whatever is in front" (SendKeys / keybd_event / SendInput); the capture mode drives the editor through `EditorView.TypeForTest` / `PasteForTest` (never the system clipboard). Capture screenshots of Inkwell's own windows only.
- Back up `%APPDATA%\Inkwell` and `%LOCALAPPDATA%\Inkwell` before test runs and restore them afterwards.
- Never modify the owner's documents. Never change the .docx association or default apps; test installers per-user in a scratch folder with only the .md task (`/CURRENTUSER /DIR=… /TASKS="mdopenwith"`), then uninstall.
- Microsoft Word (COM): only when WINWORD.EXE is not already running; Visible = false; only on generated files in a scratch folder; quit Word afterwards and make sure no WINWORD.EXE we started remains (automation instances have `/Automation -Embedding` on their command line).
- The engine tests write only under `%TEMP%\inkwell-tests-*` and delete it.
