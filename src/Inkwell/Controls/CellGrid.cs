using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Inkwell.Controls;

/// <summary>
/// A small spreadsheet-like grid of text boxes (chart data, tables). Tab / arrows / Enter move between cells;
/// pasting rows copied from Excel or a web page fills the grid.
/// </summary>
public sealed class CellGrid : Grid
{
    private readonly List<List<TextBox>> _cells = new();
    private bool _loading;

    public CellGrid()
    {
        Focusable = false;
    }

    public event Action? Changed;

    /// <summary>Column widths in pixels (the first can be wider for labels).</summary>
    public double FirstColumnWidth { get; set; } = 150;
    public double ColumnWidth { get; set; } = 110;

    /// <summary>Bold first row (series names / table header).</summary>
    public bool BoldFirstRow { get; set; } = true;

    public int Rows => _cells.Count;
    public int Columns => _cells.Count == 0 ? 0 : _cells[0].Count;

    public void Load(IReadOnlyList<IReadOnlyList<string>> values)
    {
        _loading = true;
        var rows = Math.Max(1, values.Count);
        var cols = Math.Max(1, values.Count == 0 ? 1 : values.Max(r => r.Count));
        Rebuild(rows, cols, (r, c) => r < values.Count && c < values[r].Count ? values[r][c] ?? "" : "");
        _loading = false;
    }

    public List<List<string>> Read() => _cells.Select(r => r.Select(t => t.Text.Trim()).ToList()).ToList();

    private void Rebuild(int rows, int cols, Func<int, int, string> value)
    {
        Children.Clear();
        RowDefinitions.Clear();
        ColumnDefinitions.Clear();
        _cells.Clear();
        for (var c = 0; c < cols; c++) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c == 0 ? FirstColumnWidth : ColumnWidth) });
        for (var r = 0; r < rows; r++)
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = new List<TextBox>();
            for (var c = 0; c < cols; c++)
            {
                var box = new TextBox { Text = value(r, c), Margin = new Thickness(0, 0, 4, 4), MinHeight = 30, Padding = new Thickness(8, 4, 6, 5) };
                if (r == 0 && BoldFirstRow) box.FontWeight = FontWeights.SemiBold;
                if (c > 0 && r > 0) box.TextAlignment = TextAlignment.Right;
                AutomationProperties.SetName(box, $"Row {r + 1}, column {c + 1}");
                var rr = r;
                var cc = c;
                box.TextChanged += (_, _) =>
                {
                    if (!_loading) Changed?.Invoke();
                };
                box.PreviewKeyDown += (_, e) => OnKey(rr, cc, e);
                DataObject.AddPastingHandler(box, (_, e) => OnPaste(rr, cc, e));
                SetRow(box, r);
                SetColumn(box, c);
                Children.Add(box);
                row.Add(box);
            }
            _cells.Add(row);
        }
    }

    public void AddRow()
    {
        var data = Read();
        data.Add(Enumerable.Repeat("", Columns).ToList());
        LoadAndNotify(data);
    }

    public void RemoveRow()
    {
        var data = Read();
        if (data.Count <= 2) return;
        data.RemoveAt(data.Count - 1);
        LoadAndNotify(data);
    }

    public void AddColumn()
    {
        var data = Read();
        foreach (var row in data) row.Add("");
        LoadAndNotify(data);
    }

    public void RemoveColumn()
    {
        var data = Read();
        if (Columns <= 2) return;
        foreach (var row in data) row.RemoveAt(row.Count - 1);
        LoadAndNotify(data);
    }

    private void LoadAndNotify(List<List<string>> data)
    {
        Load(data.Select(r => (IReadOnlyList<string>)r).ToList());
        Changed?.Invoke();
    }

    public void FocusCell(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Columns) return;
        _cells[row][col].Focus();
        _cells[row][col].SelectAll();
    }

    private void OnKey(int r, int c, KeyEventArgs e)
    {
        var box = _cells[r][c];
        switch (e.Key)
        {
            case Key.Enter:
                FocusCell(r + 1 < Rows ? r + 1 : r, c);
                e.Handled = true;
                break;
            case Key.Up:
                FocusCell(r - 1, c);
                e.Handled = true;
                break;
            case Key.Down:
                FocusCell(r + 1, c);
                e.Handled = true;
                break;
            case Key.Left when box.CaretIndex == 0 && box.SelectionLength == 0:
                FocusCell(r, c - 1);
                e.Handled = true;
                break;
            case Key.Right when box.CaretIndex == box.Text.Length && box.SelectionLength == 0:
                FocusCell(r, c + 1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Tab-separated rows (from Excel, Sheets, web tables) spread over the grid from this cell.</summary>
    private void OnPaste(int r, int c, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)) return;
        var text = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? "";
        if (!text.Contains('\t') && !text.Trim().Contains('\n')) return;
        e.CancelCommand();
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(l => l.Split('\t')).ToList();
        var data = Read();
        var needRows = r + lines.Count;
        var needCols = c + lines.Max(l => l.Length);
        while (data.Count < Math.Min(needRows, 60)) data.Add(Enumerable.Repeat("", data[0].Count).ToList());
        if (needCols > data[0].Count)
            foreach (var row in data)
                while (row.Count < Math.Min(needCols, 20)) row.Add("");
        for (var i = 0; i < lines.Count && r + i < data.Count; i++)
        for (var j = 0; j < lines[i].Length && c + j < data[r + i].Count; j++)
            data[r + i][c + j] = lines[i][j].Trim();
        LoadAndNotify(data);
    }
}
