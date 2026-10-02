using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Cosmos.Kernel.System.Keyboard;
using Cosmos.Kernel.System.Mouse;
using Cosmos.Kernel.System.Timer;

namespace yggdrasilKernel;

public static class ShellLineEditor {
    private static readonly List<string> _history = new();
    private static readonly string[] ShellCommands = {
        "help", "clear", "cls", "halt", "shutdown", "pwd", "drives", "space", "diskmanager", "diskmgmt",
        "diskpart", "ls", "dir", "cd", "cat", "type", "tree", "mkdir", "rm", "del", "rmdir",
        "write", "testwrite", "yindows",
    };
    private static readonly string[] DiskManagerCommands = {
        "help", "home", "exit", "back", "quit", "clear", "cls", "list", "disks", "partitions",
        "parts", "volumes", "drives", "space", "assign", "format", "init", "rescan", "info",
    };

    public static string ReadLine(string prompt, bool diskManagerMode = false) {
        StringBuilder buffer = new();
        int cursor = 0;
        int historyIndex = _history.Count;
        string historyDraft = string.Empty;
        string? cycleBaseLine = null;
        string? lastCompletedLine = null;
        int cycleStart = 0;
        int cycleEnd = 0;
        int cycleIndex = -1;
        List<string>? cycleItems = null;

        Console.Write(prompt);
        while (true) {
            DiskSpaceQuery.PumpCompleted(prompt, buffer.ToString(), cursor);

            int scrollDelta = MouseManager.ScrollDelta;
            if (scrollDelta != 0) {
                MouseManager.ResetScrollDelta();
                ShellOutput.ScrollBy(scrollDelta * 3, prompt, buffer.ToString(), cursor);
            }

            if (!Console.KeyAvailable) {
                if (TimerManager.IsInitialized) {
                    TimerManager.Wait(10);
                }
                continue;
            }

            ConsoleKeyInfo key = Console.ReadKey(true);
            switch (key.Key) {
                case ConsoleKey.Enter:
                    ShellOutput.SubmitPrompt(prompt, buffer.ToString());
                    string result = buffer.ToString();
                    if (!string.IsNullOrWhiteSpace(result)
                        && (_history.Count == 0 || !string.Equals(_history[_history.Count - 1], result, StringComparison.Ordinal))) {
                        _history.Add(result);
                    }
                    return result;
                case ConsoleKey.Tab:
                    if (cycleItems is not null && cycleBaseLine is not null
                        && string.Equals(buffer.ToString(), lastCompletedLine, StringComparison.Ordinal)) {
                        int direction = (key.Modifiers & ConsoleModifiers.Shift) != 0 ? -1 : 1;
                        cycleIndex = (cycleIndex + direction + cycleItems.Count) % cycleItems.Count;
                        ApplyCompletion(buffer, ref cursor, cycleBaseLine, cycleStart, cycleEnd, cycleItems[cycleIndex]);
                        lastCompletedLine = buffer.ToString();
                    } else {
                        cycleBaseLine = buffer.ToString();
                        if (TryGetCompletions(cycleBaseLine, cursor, diskManagerMode, out cycleStart, out cycleEnd, out cycleItems)
                            && cycleItems.Count > 0) {
                            cycleIndex = 0;
                            ApplyCompletion(buffer, ref cursor, cycleBaseLine, cycleStart, cycleEnd, cycleItems[cycleIndex]);
                            lastCompletedLine = buffer.ToString();
                        } else {
                            cycleItems = null;
                            cycleBaseLine = null;
                            lastCompletedLine = null;
                        }
                    }
                    Redraw(prompt, buffer, cursor);
                    break;
                case ConsoleKey.Backspace:
                    if (cursor > 0) {
                        buffer.Remove(cursor - 1, 1);
                        cursor--;
                    }
                    ResetCompletion(ref cycleBaseLine, ref lastCompletedLine, ref cycleItems, ref cycleIndex);
                    Redraw(prompt, buffer, cursor);
                    break;
                case ConsoleKey.Delete:
                    if (cursor < buffer.Length) {
                        buffer.Remove(cursor, 1);
                    }
                    ResetCompletion(ref cycleBaseLine, ref lastCompletedLine, ref cycleItems, ref cycleIndex);
                    Redraw(prompt, buffer, cursor);
                    break;
                case ConsoleKey.LeftArrow:
                case ConsoleKey.NumPad4 when !KeyboardManager.NumLock:
                    if (cursor > 0) {
                        cursor--;
                        ResetCompletion(ref cycleBaseLine, ref lastCompletedLine, ref cycleItems, ref cycleIndex);
                        Redraw(prompt, buffer, cursor);
                    }
                    break;
                case ConsoleKey.RightArrow:
                case ConsoleKey.NumPad6 when !KeyboardManager.NumLock:
                    if (cursor < buffer.Length) {
                        cursor++;
                        ResetCompletion(ref cycleBaseLine, ref lastCompletedLine, ref cycleItems, ref cycleIndex);
                        Redraw(prompt, buffer, cursor);
                    }
                    break;
                case ConsoleKey.Home:
                case ConsoleKey.NumPad7 when !KeyboardManager.NumLock:
                    cursor = 0;
                    ResetCompletion(ref cycleBaseLine, ref lastCompletedLine, ref cycleItems, ref cycleIndex);
                    Redraw(prompt, buffer, cursor);
                    break;
                case ConsoleKey.End:
                case ConsoleKey.NumPad1 when !KeyboardManager.NumLock:
                    if (ShellOutput.IsScrolledBack) {
                        cursor = buffer.Length;
                        ShellOutput.ScrollToBottom(prompt, buffer.ToString(), cursor);
                    } else {
                        cursor = buffer.Length;
                        Redraw(prompt, buffer, cursor);
                    }
                    ResetCompletion(ref cycleBaseLine, ref lastCompletedLine, ref cycleItems, ref cycleIndex);
                    break;
                case ConsoleKey.UpArrow:
                case ConsoleKey.NumPad8 when !KeyboardManager.NumLock:
                    if ((key.Modifiers & ConsoleModifiers.Shift) != 0) {
                        ShellOutput.ScrollBy(ShellOutput.PageSize / 3 + 1, prompt, buffer.ToString(), cursor);
                    } else if (_history.Count > 0 && historyIndex > 0) {
                        if (historyIndex == _history.Count) {
                            historyDraft = buffer.ToString();
                        }
                        historyIndex--;
                        SetBuffer(buffer, _history[historyIndex], out cursor);
                        ResetCompletion(ref cycleBaseLine, ref lastCompletedLine, ref cycleItems, ref cycleIndex);
                        Redraw(prompt, buffer, cursor);
                    }
                    break;
                case ConsoleKey.DownArrow:
                case ConsoleKey.NumPad2 when !KeyboardManager.NumLock:
                    if ((key.Modifiers & ConsoleModifiers.Shift) != 0) {
                        ShellOutput.ScrollBy(-(ShellOutput.PageSize / 3 + 1), prompt, buffer.ToString(), cursor);
                    } else if (historyIndex < _history.Count) {
                        historyIndex++;
                        SetBuffer(buffer, historyIndex == _history.Count ? historyDraft : _history[historyIndex], out cursor);
                        ResetCompletion(ref cycleBaseLine, ref lastCompletedLine, ref cycleItems, ref cycleIndex);
                        Redraw(prompt, buffer, cursor);
                    }
                    break;
                case ConsoleKey.PageUp:
                case ConsoleKey.NumPad9 when !KeyboardManager.NumLock:
                    ShellOutput.ScrollBy(ShellOutput.PageSize, prompt, buffer.ToString(), cursor);
                    break;
                case ConsoleKey.PageDown:
                case ConsoleKey.NumPad3 when !KeyboardManager.NumLock:
                    ShellOutput.ScrollBy(-ShellOutput.PageSize, prompt, buffer.ToString(), cursor);
                    break;
                case ConsoleKey.Escape:
                    buffer.Clear();
                    cursor = 0;
                    historyIndex = _history.Count;
                    ResetCompletion(ref cycleBaseLine, ref lastCompletedLine, ref cycleItems, ref cycleIndex);
                    Redraw(prompt, buffer, cursor);
                    break;
                default:
                    if (!char.IsControl(key.KeyChar)) {
                        buffer.Insert(cursor, key.KeyChar);
                        cursor++;
                        historyIndex = _history.Count;
                        ResetCompletion(ref cycleBaseLine, ref lastCompletedLine, ref cycleItems, ref cycleIndex);
                        Redraw(prompt, buffer, cursor);
                    }
                    break;
            }
        }
    }

    private static bool TryGetCompletions(string line, int cursor, bool diskManagerMode, out int tokenStart, out int tokenEnd, out List<string> completions) {
        FindTokenRange(line, cursor, out tokenStart, out tokenEnd);
        string token = line.Substring(tokenStart, tokenEnd - tokenStart);
        completions = new List<string>();

        if (line.Substring(0, tokenStart).Trim().Length == 0) {
            string[] commands = diskManagerMode ? DiskManagerCommands : ShellCommands;
            for (int index = 0; index < commands.Length; index++) {
                if (commands[index].StartsWith(token, StringComparison.OrdinalIgnoreCase)) {
                    completions.Add(commands[index] + " ");
                }
            }
            if (token.Length > 0 && !diskManagerMode) {
                foreach (KeyValuePair<string, string> drive in YVolumeManager.GetDrives()) {
                    if (drive.Key.StartsWith(token, StringComparison.OrdinalIgnoreCase)) {
                        completions.Add(drive.Key + ":\\");
                    }
                }
            }
            return completions.Count > 0;
        }

        char quote = token.Length > 0 && (token[0] == '"' || token[0] == '\'') ? token[0] : '\0';
        string pathToken = quote == '\0' ? token : token.Substring(1);
        bool hasClosingQuote = quote != '\0' && pathToken.Length > 0 && pathToken[pathToken.Length - 1] == quote;
        if (hasClosingQuote) {
            pathToken = pathToken.Substring(0, pathToken.Length - 1);
        }

        int driveColon = pathToken.IndexOf(':');
        if (driveColon == pathToken.Length - 1 && driveColon > 0
            && YVolumeManager.TryNormalizeDriveName(pathToken.Substring(0, driveColon), out string driveName)
            && YVolumeManager.TryGetVolumePath(driveName, out _)) {
            string driveRoot = driveName + ":\\";
            if (quote != '\0') {
                driveRoot = quote + driveRoot + (hasClosingQuote ? quote.ToString() : string.Empty);
            }
            completions.Add(driveRoot);
            return true;
        }

        int separatorIndex = pathToken.LastIndexOfAny(new[] { '/', '\\' });
        string directoryPrefix = separatorIndex < 0 ? string.Empty : pathToken.Substring(0, separatorIndex + 1);
        string leafPrefix = separatorIndex < 0 ? pathToken : pathToken.Substring(separatorIndex + 1);
        string searchPath = directoryPrefix.Length == 0
            ? YDirectory.GetCurrentDirectory()
            : directoryPrefix;

        string[] directories;
        string[] files;
        try {
            directories = YDirectory.GetDirectories(searchPath);
            files = YDirectory.GetFiles(searchPath);
        } catch (Exception) {
            return false;
        }

        char separator = GetPreferredSeparator(pathToken);
        AddPathCompletions(completions, directories, directoryPrefix, leafPrefix, separator, quote, hasClosingQuote, true, tokenEnd == line.Length);
        AddPathCompletions(completions, files, directoryPrefix, leafPrefix, separator, quote, hasClosingQuote, false, tokenEnd == line.Length);
        return completions.Count > 0;
    }

    private static void AddPathCompletions(List<string> completions, string[] entries, string directoryPrefix,
        string leafPrefix, char separator, char quote, bool hasClosingQuote, bool isDirectory, bool atLineEnd) {
        for (int index = 0; index < entries.Length; index++) {
            string name = ExtractLeafName(entries[index]);
            if (name.StartsWith(".", StringComparison.Ordinal) && !leafPrefix.StartsWith(".", StringComparison.Ordinal)) {
                continue;
            }
            if (!name.StartsWith(leafPrefix, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            string completedPath = directoryPrefix + name;
            bool needsQuotes = quote != '\0' || completedPath.IndexOf(' ') >= 0;
            char activeQuote = quote != '\0' ? quote : '"';
            string completed = needsQuotes ? activeQuote.ToString() + completedPath : completedPath;
            if (isDirectory) {
                completed += separator;
            }
            if (needsQuotes && (!isDirectory || hasClosingQuote)) {
                completed += activeQuote;
            }
            if (atLineEnd && !isDirectory) {
                completed += " ";
            }
            completions.Add(completed);
        }
    }

    private static void FindTokenRange(string line, int cursor, out int tokenStart, out int tokenEnd) {
        tokenStart = 0;
        char quote = '\0';
        for (int index = 0; index < cursor; index++) {
            char character = line[index];
            if ((character == '"' || character == '\'') && (quote == '\0' || quote == character)) {
                quote = quote == '\0' ? character : '\0';
            } else if (char.IsWhiteSpace(character) && quote == '\0') {
                tokenStart = index + 1;
            }
        }

        quote = '\0';
        for (int index = tokenStart; index < cursor; index++) {
            char character = line[index];
            if ((character == '"' || character == '\'') && (quote == '\0' || quote == character)) {
                quote = quote == '\0' ? character : '\0';
            }
        }

        tokenEnd = line.Length;
        for (int index = cursor; index < line.Length; index++) {
            char character = line[index];
            if ((character == '"' || character == '\'') && (quote == '\0' || quote == character)) {
                quote = quote == '\0' ? character : '\0';
            } else if (char.IsWhiteSpace(character) && quote == '\0') {
                tokenEnd = index;
                break;
            }
        }
    }

    private static void ApplyCompletion(StringBuilder buffer, ref int cursor, string original, int start, int end, string replacement) {
        buffer.Clear();
        buffer.Append(original.Substring(0, start));
        buffer.Append(replacement);
        buffer.Append(original.Substring(end));
        cursor = start + replacement.Length;
    }

    private static void Redraw(string prompt, StringBuilder buffer, int cursor) {
        ShellOutput.RedrawPrompt(prompt, buffer.ToString(), cursor);
    }

    private static char GetPreferredSeparator(string path) {
        int slash = path.LastIndexOf('/');
        int backslash = path.LastIndexOf('\\');
        if (backslash > slash) {
            return '\\';
        }
        if (slash >= 0) {
            return '/';
        }
        return '\\';
    }

    private static string ExtractLeafName(string path) {
        string trimmed = path.TrimEnd('/', '\\');
        int separator = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        return separator < 0 ? trimmed : trimmed.Substring(separator + 1);
    }

    private static void SetBuffer(StringBuilder buffer, string value, out int cursor) {
        buffer.Clear();
        buffer.Append(value);
        cursor = buffer.Length;
    }

    private static void ResetCompletion(ref string? cycleBaseLine, ref string? lastCompletedLine,
        ref List<string>? cycleItems, ref int cycleIndex) {
        cycleBaseLine = null;
        lastCompletedLine = null;
        cycleItems = null;
        cycleIndex = -1;
    }
}
