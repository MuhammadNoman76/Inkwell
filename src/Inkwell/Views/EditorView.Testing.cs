using System.Windows;
using Inkwell.Core.Editing;
using Inkwell.Views.Panels;

namespace Inkwell.Views;

/// <summary>Hooks for the capture mode and automated checks: the same code paths as typing and pasting, without input devices.</summary>
public partial class EditorView
{
    /// <summary>Types text through the same path as the keyboard (autocorrect, Markdown shortcuts, slash menu); '\n' is Enter.</summary>
    internal void TypeForTest(string text)
    {
        foreach (var ch in text)
        {
            try
            {
                if (ch == '\n')
                {
                    if (SlashMenuOpen)
                    {
                        RunSlashSelection();
                        continue;
                    }
                    if (!BeforeTyping("\n")) Editor.Enter();
                    continue;
                }
                var s = ch.ToString();
                if (BeforeTyping(s)) continue;
                Editor.InsertText(s);
                AfterTyping(s);
            }
            catch (EditRefusedException ex)
            {
                _window.ShowMessage(ex.Message, 3, warning: true);
            }
        }
    }

    /// <summary>Pastes from a data object (never the system clipboard) as Ctrl+V would.</summary>
    internal void PasteForTest(IDataObject data, PasteMode mode) => PasteData(data, mode, offerChoices: true);

    internal void ClosePaletteForTest()
    {
        foreach (var palette in OverlayLayer.Children.OfType<CommandPalette>().ToList()) OverlayLayer.Children.Remove(palette);
        _paletteOpen = false;
    }

    internal void CloseSlashMenuForTest() => CloseSlashMenu();

    /// <summary>Keeps version history in another folder (the capture mode never touches the real one).</summary>
    internal static void UseHistoryRoot(string folder) => s_history = new Core.IO.VersionHistory(folder, new Core.IO.VersionHistoryOptions()) { OnError = Services.Log.Error };
}
