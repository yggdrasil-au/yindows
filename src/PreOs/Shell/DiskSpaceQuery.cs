using System;
using System.Collections.Generic;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Diagnostics;
using Cosmos.Kernel.System.Timer;
using Cosmos.Kernel.System.Vfs;
using yggdrasilKernel.Storage;
using yggdrasilKernel.Vfs;
using yggdrasilKernel;

namespace yggdrasilKernel.PreOs.Shell;

public static class DiskSpaceQuery {
    private static bool _isRunning;
    private static volatile bool _hasCompleted;
    private static List<string>? _completedLines;

    public static bool Start(string? driveArgument = null) {
        if (_isRunning) {
            ShellOutput.WriteLine("A free-space scan is already running.");
            return false;
        }

        List<VolumeRequest> volumes = GetVolumes(driveArgument);
        if (volumes.Count == 0) {
            ShellOutput.WriteLine(string.IsNullOrWhiteSpace(driveArgument)
                ? "No mounted volumes are available."
                : $"Drive '{driveArgument}' is not mounted.");
            return false;
        }

        _isRunning = true;
        _hasCompleted = false;
        _completedLines = null;
        ulong alarmId = AlarmManager.Schedule(() => Scan(volumes), TimeSpan.FromMilliseconds(1));
        if (alarmId == 0) {
            _isRunning = false;
            ShellOutput.WriteLine("Could not schedule a free-space scan; the kernel scheduler is unavailable.");
            return false;
        }

        ShellOutput.WriteLine(volumes.Count == 1
            ? $"Free-space scan started for {volumes[0].Letter}:. The shell remains available while it runs."
            : $"Free-space scan started for {volumes.Count} mounted volumes. The shell remains available while it runs.");
        return true;
    }

    public static void PumpCompleted(string? prompt = null, string? input = null, int cursor = 0) {
        if (!_hasCompleted) {
            return;
        }

        List<string>? lines = _completedLines;
        _completedLines = null;
        _hasCompleted = false;
        _isRunning = false;
        if (lines is null) {
            return;
        }
        if (prompt is not null && input is not null) {
            ShellOutput.WriteAbovePrompt(lines, prompt, input, cursor);
            return;
        }
        for (int index = 0; index < lines.Count; index++) {
            ShellOutput.WriteLine(lines[index]);
        }
    }

    private static List<VolumeRequest> GetVolumes(string? driveArgument) {
        List<VolumeRequest> volumes = new();
        IReadOnlyDictionary<string, string> drives = YVolumeManager.GetDrives();
        if (!string.IsNullOrWhiteSpace(driveArgument)) {
            string value = driveArgument.Trim().TrimEnd(':', '\\', '/');
            if (!YVolumeManager.TryNormalizeDriveName(value, out string driveName)) {
                return volumes;
            }
            if (YVolumeManager.TryGetVolumePath(driveName, out string mountPoint)) {
                volumes.Add(new VolumeRequest(driveName, mountPoint));
            }
            return volumes;
        }

        foreach (KeyValuePair<string, string> drive in drives) {
            volumes.Add(new VolumeRequest(drive.Key, drive.Value));
        }
        return volumes;
    }

    private static void Scan(List<VolumeRequest> volumes) {
        List<string> lines = new();
        for (int index = 0; index < volumes.Count; index++) {
            VolumeRequest volume = volumes[index];
            try {
                if (VfsManager.TryStatFs(volume.MountPoint, out VfsStatFs stats)) {
                    ulong totalBytes = stats.Blocks * stats.BlockSize;
                    ulong freeBytes = stats.Bavail * stats.BlockSize;
                    ulong usedBytes = totalBytes >= freeBytes ? totalBytes - freeBytes : 0;
                    double usedPercent = totalBytes > 0 ? (double)usedBytes * 100.0 / totalBytes : 0;
                    lines.Add($"{volume.Letter}: total {YVolumeManager.FormatSize(totalBytes)}, " +
                        $"used {YVolumeManager.FormatSize(usedBytes)}, free {YVolumeManager.FormatSize(freeBytes)} " +
                        $"({usedPercent:0.0}% used)");
                } else {
                    lines.Add($"{volume.Letter}: could not read filesystem statistics at {volume.MountPoint}.");
                }
            } catch (Exception exception) {
                Log.WriteString("[DiskSpaceQuery] StatFs failed: ");
                Log.WriteString(exception.Message);
                Log.WriteString("\n");
                lines.Add($"{volume.Letter}: free-space scan failed: {exception.Message}");
            }
        }

        _completedLines = lines;
        _hasCompleted = true;
    }

    private readonly struct VolumeRequest {
        public VolumeRequest(string letter, string mountPoint) {
            Letter = letter;
            MountPoint = mountPoint;
        }

        public string Letter { get; }
        public string MountPoint { get; }
    }
}
