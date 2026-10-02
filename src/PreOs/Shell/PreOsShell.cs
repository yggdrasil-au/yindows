using System;
using yggdrasilKernel.Storage;
using yggdrasilKernel.Vfs;
using yggdrasilKernel;
using System.Runtime.InteropServices;

namespace yggdrasilKernel.PreOs.Shell;

public enum PreOsShellRequest {
    None,
    LaunchYindows,
    StopKernel,
}

public sealed class PreOsShell {
    private bool _isDiskManagerMode;

    public PreOsShellRequest Run(bool kernelStopped) {
        DiskSpaceQuery.PumpCompleted();

        if (_isDiskManagerMode) {
            string diskManagerInput = ShellLineEditor.ReadLine("DiskManager> ", diskManagerMode: true);
            if (!string.IsNullOrWhiteSpace(diskManagerInput)) {
                _isDiskManagerMode = DiskManagerCli.ExecuteCommand(diskManagerInput);
            }
            return PreOsShellRequest.None;
        }

        string input = ShellLineEditor.ReadLine($"{YDirectory.GetCurrentDirectory()}> ");
        if (string.IsNullOrWhiteSpace(input)) {
            return PreOsShellRequest.None;
        }

        string trimmed = input.Trim();
        if (IsDirectDriveSwitch(trimmed)) {
            ChangeDirectory(trimmed);
            return PreOsShellRequest.None;
        }

        int separator = trimmed.IndexOf(' ');
        string command = (separator < 0 ? trimmed : trimmed.Substring(0, separator)).ToLowerInvariant();
        string arguments = separator < 0 ? string.Empty : trimmed.Substring(separator + 1).Trim();

        switch (command) {
            case "dev":
                //ShellOutput.WriteLine("OS Version = " + Environment.OSVersion);
                ShellOutput.WriteLine("Is Windows = " + RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
                ShellOutput.WriteLine("Is Linux = " + RuntimeInformation.IsOSPlatform(OSPlatform.Linux));
                ShellOutput.WriteLine("Is macOS = " + RuntimeInformation.IsOSPlatform(OSPlatform.OSX));
                ShellOutput.WriteLine("Stopped = " + kernelStopped);
                break;
            case "help":
                PrintHelp();
                break;
            case "clear":
            case "cls":
                ShellOutput.Clear();
                break;
            case "exit":
                string confirmation = ShellLineEditor.ReadLine("Are you sure you want to exit? (y/n): ");
                if (confirmation.Trim().ToLowerInvariant() == "y") {
                    ShellOutput.WriteLine("Exiting the shell...");
                    return PreOsShellRequest.StopKernel;
                }
                break;
            case "halt":
            case "shutdown":
                ShellOutput.WriteLine("Halting system...");
                return PreOsShellRequest.StopKernel;
            case "pwd":
                ShellOutput.WriteLine(YDirectory.GetCurrentDirectory());
                break;
            case "drives":
                DiskManagerCli.ListVolumes();
                break;
            case "space":
                DiskSpaceQuery.Start(arguments);
                break;
            case "diskmanager":
            case "diskmgmt":
            case "diskpart":
                _isDiskManagerMode = true;
                DiskManagerCli.PrintBanner();
                break;
            case "ls":
            case "dir":
                ListDirectory(string.IsNullOrEmpty(arguments) ? YDirectory.GetCurrentDirectory() : arguments);
                break;
            case "cd":
                ChangeDirectory(arguments);
                break;
            case "cat":
            case "type":
                CatFile(arguments);
                break;
            case "tree":
                PrintTree(string.IsNullOrEmpty(arguments) ? YDirectory.GetCurrentDirectory() : arguments);
                break;
            case "mkdir":
                CreateDirectory(arguments);
                break;
            case "rm":
            case "del":
                DeleteFile(arguments);
                break;
            case "rmdir":
                DeleteDirectory(arguments);
                break;
            case "write":
                WriteFile(arguments);
                break;
            case "testwrite":
                TestWrite();
                break;
            case "yindows":
                return PreOsShellRequest.LaunchYindows;
            default:
                ShellOutput.WriteLine($"\"{command}\" is not a valid command. Type 'help' for commands.");
                break;
        }

        return PreOsShellRequest.None;
    }

    private static void PrintHelp() {
        ShellOutput.WriteLine("Standard commands:");
        ShellOutput.WriteLine("  ls / dir [path]        List files and directories");
        ShellOutput.WriteLine($"  cd <path | drive:>     Change directory or switch drives (cd AA:\\; max {YVolumeManager.MaxDriveNameLength} letters)");
        ShellOutput.WriteLine("  pwd                    Print the current directory");
        ShellOutput.WriteLine("  cat / type <file>      Display a text file");
        ShellOutput.WriteLine("  tree [path]            Display a directory tree");
        ShellOutput.WriteLine("  mkdir <directory>      Create a directory");
        ShellOutput.WriteLine("  rm / del <file>        Delete a file");
        ShellOutput.WriteLine("  rmdir <directory>      Delete a directory tree");
        ShellOutput.WriteLine("  write <file> <text>    Write text to a file");
        ShellOutput.WriteLine("  drives                 List mounted drive letters");
        ShellOutput.WriteLine("  space [drive:]         Scan and report free space in the background");
        ShellOutput.WriteLine("  diskmanager            Open the interactive disk manager");
        ShellOutput.WriteLine("  testwrite              Create sample files on C:");
        ShellOutput.WriteLine("  clear / cls            Clear the screen");
        ShellOutput.WriteLine("  yindows                Launch the Yindows GUI");
    }

    private static bool IsDirectDriveSwitch(string path) {
        int colonIndex = path.IndexOf(':');
        if (colonIndex <= 0 || colonIndex > YVolumeManager.MaxDriveNameLength
            || !YVolumeManager.TryNormalizeDriveName(path.Substring(0, colonIndex), out _)) {
            return false;
        }

        string suffix = path.Substring(colonIndex + 1);
        return suffix.Length == 0 || (suffix.Length == 1 && (suffix[0] == '\\' || suffix[0] == '/'));
    }

    private static void ChangeDirectory(string target) {
        if (string.IsNullOrWhiteSpace(target)) {
            ShellOutput.WriteLine(YDirectory.GetCurrentDirectory());
            return;
        }

        if (target.StartsWith("/d ", StringComparison.OrdinalIgnoreCase)) {
            target = target.Substring(3).Trim();
        }
        target = Unquote(target);

        int colonIndex = target.IndexOf(':');
        if (colonIndex > 0 && colonIndex <= YVolumeManager.MaxDriveNameLength
            && YVolumeManager.TryNormalizeDriveName(target.Substring(0, colonIndex), out string driveName)) {
            string suffix = target.Substring(colonIndex + 1);
            if (suffix.Length == 0) {
                if (!YVolumeManager.SetCurrentDrive(driveName)) {
                    ShellOutput.WriteLine($"Drive {driveName}:\\ is not mounted.");
                }
                return;
            }

            if (!YVolumeManager.TryGetVolumePath(driveName, out _)) {
                ShellOutput.WriteLine($"Drive {driveName}:\\ is not mounted.");
                return;
            }

            if (suffix.Length == 1 && (suffix[0] == '\\' || suffix[0] == '/')) {
                target = $"{driveName}:\\";
            }
        }

        if (!YDirectory.Exists(target)) {
            ShellOutput.WriteLine($"The system cannot find the path specified: {target}");
            return;
        }

        try {
            YDirectory.SetCurrentDirectory(target);
        } catch (Exception exception) {
            ShellOutput.WriteLine($"cd error: {exception.Message}");
        }
    }

    private static void ListDirectory(string path) {
        path = Unquote(path);
        if (!YDirectory.Exists(path)) {
            ShellOutput.WriteLine($"Directory not found: {path}");
            return;
        }

        ShellOutput.WriteLine($"\n Directory of {YPath.ToWindowsDisplay(path)}\n");
        string[] directories = YDirectory.GetDirectories(path);
        string[] files = YDirectory.GetFiles(path);
        for (int index = 0; index < directories.Length; index++) {
            ShellOutput.WriteLine($"  <DIR>          {ExtractLeafName(directories[index])}");
        }
        for (int index = 0; index < files.Length; index++) {
            ulong size = 0;
            try {
                size = (ulong)YFile.ReadAllBytes(files[index]).Length;
            } catch (Exception) {
            }
            ShellOutput.WriteLine($"  {YVolumeManager.FormatSize(size),-13} {ExtractLeafName(files[index])}");
        }
        ShellOutput.WriteLine($"\n  {files.Length} File(s), {directories.Length} Dir(s)\n");
    }

    private static void CatFile(string path) {
        if (string.IsNullOrWhiteSpace(path)) {
            ShellOutput.WriteLine("Usage: cat <file>");
            return;
        }
        path = Unquote(path);
        if (!YFile.Exists(path)) {
            ShellOutput.WriteLine($"File not found: {path}");
            return;
        }
        try {
            ShellOutput.WriteLine(YFile.ReadAllText(path));
        } catch (Exception exception) {
            ShellOutput.WriteLine($"cat error: {exception.Message}");
        }
    }

    private static void CreateDirectory(string path) {
        if (string.IsNullOrWhiteSpace(path)) {
            ShellOutput.WriteLine("Usage: mkdir <directory>");
            return;
        }
        path = Unquote(path);
        try {
            YDirectory.CreateDirectory(path);
        } catch (Exception exception) {
            ShellOutput.WriteLine($"mkdir error: {exception.Message}");
        }
    }

    private static void DeleteFile(string path) {
        if (string.IsNullOrWhiteSpace(path)) {
            ShellOutput.WriteLine("Usage: rm <file>");
            return;
        }
        path = Unquote(path);
        if (!YFile.Exists(path)) {
            ShellOutput.WriteLine($"File not found: {path}");
            return;
        }
        try {
            YFile.Delete(path);
        } catch (Exception exception) {
            ShellOutput.WriteLine($"rm error: {exception.Message}");
        }
    }

    private static void DeleteDirectory(string path) {
        if (string.IsNullOrWhiteSpace(path)) {
            ShellOutput.WriteLine("Usage: rmdir <directory>");
            return;
        }
        path = Unquote(path);
        if (!YDirectory.Exists(path)) {
            ShellOutput.WriteLine($"Directory not found: {path}");
            return;
        }
        try {
            YDirectory.Delete(path, recursive: true);
        } catch (Exception exception) {
            ShellOutput.WriteLine($"rmdir error: {exception.Message}");
        }
    }

    private static void WriteFile(string arguments) {
        string path;
        string content;
        if (arguments.Length > 0 && (arguments[0] == '"' || arguments[0] == '\'')) {
            char quote = arguments[0];
            int closingQuote = arguments.IndexOf(quote, 1);
            if (closingQuote < 0) {
                ShellOutput.WriteLine("Usage: write <file> <text>");
                return;
            }
            path = arguments.Substring(1, closingQuote - 1);
            content = arguments.Substring(closingQuote + 1).TrimStart();
        } else {
            int separator = arguments.IndexOf(' ');
            if (separator <= 0) {
                ShellOutput.WriteLine("Usage: write <file> <text>");
                return;
            }
            path = arguments.Substring(0, separator).Trim();
            content = arguments.Substring(separator + 1);
        }
        if (string.IsNullOrEmpty(path)) {
            ShellOutput.WriteLine("Usage: write <file> <text>");
            return;
        }
        try {
            YFile.WriteAllText(path, content);
            ShellOutput.WriteLine($"Wrote {content.Length} characters to {YPath.ToWindowsDisplay(path)}");
        } catch (Exception exception) {
            ShellOutput.WriteLine($"write error: {exception.Message}");
        }
    }

    private static void PrintTree(string path) {
        if (!YDirectory.Exists(path)) {
            ShellOutput.WriteLine($"Directory not found: {path}");
            return;
        }
        ShellOutput.WriteLine(YPath.ToWindowsDisplay(path));
        PrintTreeRecursive(path, string.Empty, 0);
    }

    private static void PrintTreeRecursive(string currentPath, string indent, int depth) {
        if (depth >= 16) {
            return;
        }

        string[] directories = YDirectory.GetDirectories(currentPath);
        string[] files = YDirectory.GetFiles(currentPath);
        int itemCount = directories.Length + files.Length;
        int itemIndex = 0;
        for (int index = 0; index < directories.Length; index++) {
            bool isLast = ++itemIndex == itemCount;
            ShellOutput.WriteLine($"{indent}{(isLast ? "\\---" : "+---")}[{ExtractLeafName(directories[index])}]");
            PrintTreeRecursive(directories[index], indent + (isLast ? "    " : "|   "), depth + 1);
        }
        for (int index = 0; index < files.Length; index++) {
            bool isLast = ++itemIndex == itemCount;
            ShellOutput.WriteLine($"{indent}{(isLast ? "\\---" : "+---")}{ExtractLeafName(files[index])}");
        }
    }

    private static string ExtractLeafName(string path) {
        string trimmed = path.TrimEnd('\\', '/');
        int separator = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        return separator >= 0 ? trimmed.Substring(separator + 1) : trimmed;
    }

    private static string Unquote(string value) {
        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[value.Length - 1] == value[0]) {
            return value.Substring(1, value.Length - 2);
        }
        return value;
    }

    private static void TestWrite() {
        if (!YVolumeManager.TryGetVolumePath('C', out _)) {
            ShellOutput.WriteLine("C: is not mounted.");
            return;
        }
        try {
            YDirectory.CreateDirectory(@"C:\System\Logs/DeepFolder\Sub");
            YFile.WriteAllText(@"C:\welcome.txt", "Welcome to Yindows on C!");
            ShellOutput.WriteLine("Windows: " + YFile.ReadAllText(@"C:\welcome.txt"));
            ShellOutput.WriteLine("Unix: " + YFile.ReadAllText("/c/welcome.txt"));
            ShellOutput.WriteLine("Mixed: " + YFile.ReadAllText(@"C:/System/Logs\..\..\welcome.txt"));
            ShellOutput.WriteLine("NT: " + YFile.ReadAllText("/Device/HarddiskVolume1/welcome.txt"));
        } catch (Exception exception) {
            ShellOutput.WriteLine($"testwrite error: {exception.Message}");
        }
    }
}