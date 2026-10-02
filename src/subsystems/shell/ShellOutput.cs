using System;
using System.Collections.Generic;
using Cosmos.Kernel.System.Graphics;

namespace yggdrasilKernel;

public static class ShellOutput {
    private const int MaximumScrollbackLines = 2000;
    private static readonly List<string> _lines = new();
    private static int _scrollOffset;

    public static bool IsScrolledBack => _scrollOffset > 0;

    public static void WriteLine() {
        WriteLine(string.Empty);
    }

    public static void WriteLine(string? text) {
        if (_scrollOffset > 0) {
            _scrollOffset = 0;
        }

        string value = text ?? string.Empty;
        string[] lines = value.Split('\n');
        int columns = GetColumns();
        for (int index = 0; index < lines.Length; index++) {
            AppendWrappedLine(lines[index].TrimEnd('\r'), columns);
        }
        TrimScrollback();
        Console.WriteLine(value);
    }

    public static void Clear() {
        _lines.Clear();
        _scrollOffset = 0;
        Console.Clear();
    }

    public static void ScrollBy(int lineDelta, string prompt, string input, int cursor) {
        if (lineDelta == 0) {
            return;
        }

        int pageLines = Math.Max(1, GetRows() - 2);
        int maximumOffset = Math.Max(0, _lines.Count - pageLines);
        _scrollOffset = Math.Clamp(_scrollOffset + lineDelta, 0, maximumOffset);
        RenderViewport(prompt, input, cursor);
    }

    public static int PageSize => Math.Max(1, GetRows() - 3);

    public static void ScrollToBottom(string prompt, string input, int cursor) {
        _scrollOffset = 0;
        RenderViewport(prompt, input, cursor);
    }

    public static void RedrawPrompt(string prompt, string input, int cursor) {
        if (_scrollOffset > 0) {
            RenderViewport(prompt, input, cursor);
            return;
        }
        DrawPrompt(prompt, input, cursor);
    }

    public static void SubmitPrompt(string prompt, string input) {
        bool wasScrolled = _scrollOffset > 0;
        _scrollOffset = 0;
        if (wasScrolled) {
            RenderViewport(prompt, input, input.Length);
        }

        string[] promptLines = (prompt + input).Split('\n');
        int columns = GetColumns();
        for (int index = 0; index < promptLines.Length; index++) {
            AppendWrappedLine(promptLines[index].TrimEnd('\r'), columns);
        }
        TrimScrollback();
        Console.WriteLine();
    }

    public static void WriteAbovePrompt(IReadOnlyList<string> lines, string prompt, string input, int cursor) {
        int columns = GetColumns();
        for (int index = 0; index < lines.Count; index++) {
            AppendWrappedLine(lines[index], columns);
        }
        _scrollOffset = 0;
        TrimScrollback();
        RenderViewport(prompt, input, cursor);
    }

    private static void RenderViewport(string prompt, string input, int cursor) {
        Console.Clear();
        int rows = GetRows();
        int historyRows = Math.Max(0, rows - 2);
        int end = Math.Max(0, _lines.Count - _scrollOffset);
        int start = Math.Max(0, end - historyRows);
        for (int index = start; index < end; index++) {
            Console.WriteLine(_lines[index]);
        }

        string status = _scrollOffset == 0
            ? "-- end of scrollback --"
            : $"-- scrollback: {_scrollOffset} line(s) back; wheel or PgUp/PgDn, End to return --";
        Console.WriteLine(status);
        DrawPrompt(prompt, input, cursor);
    }

    private static void DrawPrompt(string prompt, string input, int cursor) {
        Console.Write('\r');
        Console.Write(prompt);
        Console.Write(input);
        Console.Write(' ');
        int backspaces = input.Length - cursor + 1;
        for (int index = 0; index < backspaces; index++) {
            Console.Write('\b');
        }
    }

    private static void AppendWrappedLine(string line, int columns) {
        if (line.Length == 0) {
            _lines.Add(string.Empty);
            return;
        }

        for (int start = 0; start < line.Length; start += columns) {
            int length = Math.Min(columns, line.Length - start);
            _lines.Add(line.Substring(start, length));
        }
    }

    private static void TrimScrollback() {
        int excess = _lines.Count - MaximumScrollbackLines;
        if (excess <= 0) {
            return;
        }
        _lines.RemoveRange(0, excess);
        _scrollOffset = Math.Max(0, _scrollOffset - excess);
    }

    private static int GetColumns() {
        return KernelConsole.Default?.Cols ?? 80;
    }

    private static int GetRows() {
        return KernelConsole.Default?.Rows ?? 25;
    }
}
