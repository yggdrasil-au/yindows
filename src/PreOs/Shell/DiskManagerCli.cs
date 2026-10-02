using System;
using System.Collections.Generic;
using System.Text;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Storage;
using Cosmos.Kernel.System.Vfs;
using yggdrasilKernel.Storage;
using yggdrasilKernel.Vfs;
using yggdrasilKernel;

namespace yggdrasilKernel.PreOs.Shell;

public static class DiskManagerCli {
    public static void PrintBanner() {
        ShellOutput.WriteLine("==================================================");
        ShellOutput.WriteLine("             YggdrasilOS Disk Manager             ");
        ShellOutput.WriteLine("==================================================");
        ShellOutput.WriteLine("Type 'help' for DiskManager commands, or 'home' to return.");
    }

    public static bool ExecuteCommand(string input) {
        string[] parts = SplitArgs(input);
        if (parts.Length == 0) {
            return true;
        }

        string command = parts[0].ToLowerInvariant();
        switch (command) {
            case "help":
                PrintHelp();
                break;
            case "home":
            case "exit":
            case "back":
            case "quit":
                ShellOutput.WriteLine("Returning to the main shell...");
                return false;
            case "clear":
            case "cls":
                ShellOutput.Clear();
                break;
            case "list":
                if (parts.Length == 1) {
                    ListDisks();
                    ListPartitions();
                    ListVolumes();
                } else {
                    switch (parts[1].ToLowerInvariant()) {
                        case "disk":
                        case "disks":
                            ListDisks();
                            break;
                        case "part":
                        case "parts":
                        case "partition":
                        case "partitions":
                            ListPartitions();
                            break;
                        case "drive":
                        case "drives":
                        case "letters":
                        case "volume":
                        case "volumes":
                            ListVolumes();
                            break;
                        default:
                            ShellOutput.WriteLine("Usage: list [disks | partitions | volumes]");
                            break;
                    }
                }
                break;
            case "disks":
                ListDisks();
                break;
            case "partitions":
            case "parts":
                ListPartitions();
                break;
            case "volumes":
            case "drives":
                ListVolumes();
                break;
            case "space":
                DiskSpaceQuery.Start(parts.Length > 1 ? parts[1] : null);
                break;
            case "assign":
                if (parts.Length < 4) {
                    ShellOutput.WriteLine("Usage: assign <disk#> <part#> <letter>");
                } else {
                    HandleAssign(parts[1], parts[2], parts[3]);
                }
                break;
            case "format":
                if (parts.Length == 2) {
                    HandleFormatDriveLetter(parts[1]);
                } else if (parts.Length >= 3) {
                    HandleFormatPartition(parts[1], parts[2]);
                } else {
                    ShellOutput.WriteLine("Usage: format <drive:> OR format <disk#> <part#>");
                }
                break;
            case "init":
                if (parts.Length < 2) {
                    ShellOutput.WriteLine("Usage: init <disk#> [nometa]");
                } else {
                    bool reserveMetadata = parts.Length < 3 || !parts[2].Equals("nometa", StringComparison.OrdinalIgnoreCase);
                    HandleInitDisk(parts[1], reserveMetadata);
                }
                break;
            case "rescan":
                HandleRescan();
                break;
            case "info":
                if (parts.Length < 2) {
                    ShellOutput.WriteLine("Usage: info <disk#>");
                } else {
                    HandleDiskInfo(parts[1]);
                }
                break;
            default:
                ShellOutput.WriteLine($"Unknown DiskManager command '{command}'. Type 'help' for commands.");
                break;
        }
        return true;
    }

    public static void ListDisks() {
        IReadOnlyList<IBlockDevice> devices = StorageManager.Devices;
        ShellOutput.WriteLine("\n--- Physical Disks ---");
        ShellOutput.WriteLine("Disk  Name          Table  Removable  BlockSize  Total Size   Allocated    Unallocated");
        ShellOutput.WriteLine("----  ------------  -----  ---------  ---------  -----------  -----------  -----------");

        for (int diskIndex = 0; diskIndex < devices.Count; diskIndex++) {
            IBlockDevice device = devices[diskIndex];
            ulong totalBytes = device.BlockCount * device.BlockSize;
            string tableType = Gpt.IsGpt(device) ? "GPT" : Mbr.IsMbr(device) ? "MBR" : "RAW";
            IReadOnlyList<Partition> partitions = StorageManager.GetPartitions(device);
            ulong allocatedBytes = 0;
            for (int partitionIndex = 0; partitionIndex < partitions.Count; partitionIndex++) {
                allocatedBytes += partitions[partitionIndex].BlockCount * partitions[partitionIndex].BlockSize;
            }
            ulong unallocatedBytes = totalBytes > allocatedBytes ? totalBytes - allocatedBytes : 0;

            ShellOutput.WriteLine(
                $"{diskIndex,-4}  {device.Name,-12}  {tableType,-5}  {(YVolumeManager.IsRemovableDevice(device) ? "Yes" : "No"),-9}  " +
                $"{device.BlockSize,-9}  {YVolumeManager.FormatSize(totalBytes),-11}  " +
                $"{YVolumeManager.FormatSize(allocatedBytes),-11}  {YVolumeManager.FormatSize(unallocatedBytes)}");
        }
        ShellOutput.WriteLine();
    }

    public static void ListPartitions() {
        IReadOnlyList<IBlockDevice> devices = StorageManager.Devices;
        ShellOutput.WriteLine("\n--- Partitions ---");
        ShellOutput.WriteLine("Disk/Part  Name          Drive  Role      Start Sector  Sectors       Size         Mount Point");
        ShellOutput.WriteLine("---------  ------------  -----  --------  ------------  ------------  -----------  -----------------------");

        for (int diskIndex = 0; diskIndex < devices.Count; diskIndex++) {
            IReadOnlyList<Partition> partitions = StorageManager.GetPartitions(devices[diskIndex]);
            if (partitions.Count == 0) {
                ShellOutput.WriteLine($"{diskIndex,-9}  (No partitions)");
                continue;
            }

            for (int partitionIndex = 0; partitionIndex < partitions.Count; partitionIndex++) {
                Partition partition = partitions[partitionIndex];
                string? mountPoint = YVolumeManager.GetMountPointForPartition(partition);
                string driveName = mountPoint is null ? string.Empty : YVolumeManager.GetDriveLetterForMount(mountPoint);
                string drive = driveName.Length == 0 ? "-" : $"{driveName}:\\";
                string role = YVolumeManager.IsMetadataPartition(partition) ? "Metadata" : "Data";
                ShellOutput.WriteLine(
                    $"{diskIndex}:{partitionIndex,-7}  {partition.Name,-12}  {drive,-5}  {role,-8}  " +
                    $"{partition.StartSector,-12}  {partition.BlockCount,-12}  " +
                    $"{YVolumeManager.FormatSize(partition.BlockCount * partition.BlockSize),-11}  {mountPoint ?? "(Unmounted)"}");
            }
        }
        ShellOutput.WriteLine();
    }

    public static void ListVolumes() {
        IReadOnlyDictionary<string, string> drives = YVolumeManager.GetDrives();
            ShellOutput.WriteLine("\n--- Mounted Volumes and Drive Names ---");
        ShellOutput.WriteLine("Drive  Total Size   Free Space       VFS Mount Path");
        ShellOutput.WriteLine("-----  -----------  ---------------  -----------------------");

        foreach (KeyValuePair<string, string> drive in drives) {
            Partition? partition = FindMountedPartition(drive.Value);
            if (partition is not null) {
                ulong totalBytes = partition.BlockCount * partition.BlockSize;
                ShellOutput.WriteLine(
                    $"{drive.Key}:\\    {YVolumeManager.FormatSize(totalBytes),-11}  " +
                    $"run 'space {drive.Key}:'  {drive.Value}");
            } else {
                ShellOutput.WriteLine($"{drive.Key}:\\    Unknown      run 'space {drive.Key}:'  {drive.Value}");
            }
        }
        ShellOutput.WriteLine();
    }

    private static void PrintHelp() {
        ShellOutput.WriteLine("DiskManager commands:");
        ShellOutput.WriteLine("  list [disks|partitions|volumes]  Show storage inventory");
        ShellOutput.WriteLine("  info <disk#>                     Show disk and partition details");
        ShellOutput.WriteLine("  space [drive:]                   Scan and report free space in background");
        ShellOutput.WriteLine("  format <drive:>                  Format a mounted drive as FAT");
        ShellOutput.WriteLine("  format <disk#> <part#>           Format a partition and mount it");
        ShellOutput.WriteLine("  init <disk#> [nometa]            Initialize a blank disk");
        ShellOutput.WriteLine($"  assign <disk#> <part#> <name>    Mount or reassign a drive name (max {YVolumeManager.MaxDriveNameLength} letters)");
        ShellOutput.WriteLine("  rescan                           Rescan partition tables");
        ShellOutput.WriteLine("  clear                            Clear the screen");
        ShellOutput.WriteLine("  home / exit                      Return to the main shell");
    }

    private static void HandleDiskInfo(string diskArgument) {
        if (!TryGetDisk(diskArgument, out IBlockDevice? disk, out int diskIndex) || disk is null) {
            return;
        }

        ulong totalBytes = disk.BlockCount * disk.BlockSize;
        IReadOnlyList<Partition> partitions = StorageManager.GetPartitions(disk);
        ulong allocatedBytes = 0;
        for (int index = 0; index < partitions.Count; index++) {
            allocatedBytes += partitions[index].BlockCount * partitions[index].BlockSize;
        }

        ShellOutput.WriteLine($"\nDisk {diskIndex} ({disk.Name})");
        ShellOutput.WriteLine($"  Total size      : {YVolumeManager.FormatSize(totalBytes)} ({totalBytes} bytes)");
        ShellOutput.WriteLine($"  Total sectors   : {disk.BlockCount} (block size {disk.BlockSize} B)");
        ShellOutput.WriteLine($"  Partition table : {(Gpt.IsGpt(disk) ? "GPT" : Mbr.IsMbr(disk) ? "MBR" : "None/Raw")}");
        ShellOutput.WriteLine($"  Removable       : {(YVolumeManager.IsRemovableDevice(disk) ? "Yes" : "No")}");
        ShellOutput.WriteLine($"  Allocated       : {YVolumeManager.FormatSize(allocatedBytes)}");
        ShellOutput.WriteLine($"  Unallocated     : {YVolumeManager.FormatSize(totalBytes > allocatedBytes ? totalBytes - allocatedBytes : 0)}");

        for (int index = 0; index < partitions.Count; index++) {
            Partition partition = partitions[index];
            string? mountPoint = YVolumeManager.GetMountPointForPartition(partition);
            string driveName = mountPoint is null ? string.Empty : YVolumeManager.GetDriveLetterForMount(mountPoint);
            ulong endSector = partition.StartSector + partition.BlockCount - 1;
            ShellOutput.WriteLine(
                $"  Part {index}: {partition.StartSector}..{endSector}, " +
                $"{YVolumeManager.FormatSize(partition.BlockCount * partition.BlockSize)}, " +
                $"{(YVolumeManager.IsMetadataPartition(partition) ? "metadata" : "data")}, " +
                $"{(driveName.Length == 0 ? "no letter" : $"{driveName}:\\")}, {mountPoint ?? "unmounted"}");
        }
        ShellOutput.WriteLine();
    }

    private static void HandleFormatDriveLetter(string driveArgument) {
        if (!TryParseDriveName(driveArgument, out string driveName)
              || !YVolumeManager.TryGetVolumePath(driveName, out string mountPoint)) {
            ShellOutput.WriteLine($"Drive '{driveArgument}' is not mounted.");
            return;
        }

        Partition? partition = FindMountedPartition(mountPoint);
        if (partition is null) {
            ShellOutput.WriteLine($"Could not locate the partition behind {driveName}:\\.");
            return;
        }
        if (!Confirm($"WARNING: Format {driveName}:\\ as FAT? All data will be erased (y/n): ")) {
            ShellOutput.WriteLine("Format cancelled.");
            return;
        }
        FormatPartition(partition, driveName, mountPoint);
    }

    private static void HandleFormatPartition(string diskArgument, string partitionArgument) {
        if (!TryGetPartition(diskArgument, partitionArgument, out Partition? partition, out int diskIndex, out int partitionIndex)
            || partition is null) {
            return;
        }
        if (YVolumeManager.IsMetadataPartition(partition)) {
            ShellOutput.WriteLine("The system metadata partition cannot be formatted here.");
            return;
        }

        string? mountPoint = YVolumeManager.GetMountPointForPartition(partition);
        string driveName = mountPoint is null ? string.Empty : YVolumeManager.GetDriveLetterForMount(mountPoint);
        if (!Confirm($"WARNING: Format Disk {diskIndex}, Partition {partitionIndex} ({partition.Name}) as FAT? All data will be erased (y/n): ")) {
            ShellOutput.WriteLine("Format cancelled.");
            return;
        }
        FormatPartition(partition, driveName, mountPoint);
    }

    private static void FormatPartition(Partition partition, string driveName, string? oldMountPoint) {
        if (YVolumeManager.IsMetadataPartition(partition)) {
            ShellOutput.WriteLine("The system metadata partition cannot be formatted here.");
            return;
        }

        if (oldMountPoint is not null && !VfsManager.TryUnmount(oldMountPoint)) {
            ShellOutput.WriteLine($"Could not unmount {oldMountPoint}; formatting stopped.");
            return;
        }

        if (!DiskFormatter.TryEnsureMetadataPartitionFirst(partition, out Partition dataPartition,
            out Partition? metadataPartition, out string failureReason)) {
            ShellOutput.WriteLine($"Could not prepare a metadata-first layout: {failureReason}");
            if (oldMountPoint is not null && driveName.Length != 0
                && !YVolumeManager.MountVolume(partition, oldMountPoint, driveName)) {
                ShellOutput.WriteLine("The original volume could not be remounted after layout preparation failed.");
            }
            return;
        }

        if (metadataPartition is not null) {
            YVolumeManager.RegisterMetadataPartition(metadataPartition);
        }

        partition = dataPartition;
        bool formatted = DiskFormatter.TryFormatNewVolume(partition);
        if (!formatted) {
            ShellOutput.WriteLine("FAT formatting failed.");
        }

        string mountPoint = oldMountPoint ?? $"/Device/HarddiskVolume{YVolumeManager.GetNextMountNumber()}";
        if (driveName.Length == 0) {
            driveName = YVolumeManager.GetNextAvailableDriveLetter();
        }
        if (driveName.Length != 0 && YVolumeManager.MountVolume(partition, mountPoint, driveName)) {
            ShellOutput.WriteLine($"{(formatted ? "Formatted" : "Remounted")} {partition.Name} at {driveName}:\\ ({mountPoint}).");
        } else if (formatted) {
            ShellOutput.WriteLine("Formatted successfully, but the volume could not be mounted or assigned a drive letter.");
        } else {
            ShellOutput.WriteLine("The volume could not be remounted after the failed format.");
        }
    }

    private static void HandleInitDisk(string diskArgument, bool requestMetadata) {
        if (!TryGetDisk(diskArgument, out IBlockDevice? disk, out int diskIndex) || disk is null) {
            return;
        }
        if (StorageManager.GetPartitions(disk).Count != 0) {
            ShellOutput.WriteLine($"Disk {diskIndex} already has partitions. Use 'format {diskIndex} <part#>' for a partition.");
            return;
        }
        if (!Confirm($"Initialize Disk {diskIndex} ({disk.Name})? This writes a partition table (y/n): ")) {
            ShellOutput.WriteLine("Initialization cancelled.");
            return;
        }

        bool reserveMetadata = requestMetadata && !YVolumeManager.IsRemovableDevice(disk);
        if (requestMetadata && !reserveMetadata) {
            ShellOutput.WriteLine("This disk is treated as removable; no metadata partition will be created.");
        }

        if (!DiskFormatter.TryInitializeDisk(disk, reserveMetadata, out Partition? metadataPartition, out Partition? dataPartition)) {
            ShellOutput.WriteLine($"Failed to initialize Disk {diskIndex}. It may not be blank or large enough.");
            if (metadataPartition is not null) {
                YVolumeManager.RegisterMetadataPartition(metadataPartition);
            }
            return;
        }
        if (metadataPartition is not null) {
            YVolumeManager.RegisterMetadataPartition(metadataPartition);
        }
        if (dataPartition is null || !DiskFormatter.TryFormatNewVolume(dataPartition)) {
            ShellOutput.WriteLine("Partition table created, but FAT formatting failed.");
            return;
        }

        string driveName = YVolumeManager.GetNextAvailableDriveLetter();
        int volumeNumber = YVolumeManager.GetNextMountNumber();
        if (driveName.Length != 0 && YVolumeManager.MountVolume(dataPartition, volumeNumber, driveName)) {
            ShellOutput.WriteLine($"Initialized, formatted, and mounted as {driveName}:\\.");
        } else {
            ShellOutput.WriteLine("Initialized and formatted, but no drive letter could be assigned.");
        }
    }

    private static void HandleAssign(string diskArgument, string partitionArgument, string letterArgument) {
        if (!TryGetPartition(diskArgument, partitionArgument, out Partition? partition, out _, out _) || partition is null) {
            return;
        }
        if (YVolumeManager.IsMetadataPartition(partition)) {
            ShellOutput.WriteLine("The metadata partition is not a user volume.");
            return;
        }
        if (!TryParseDriveName(letterArgument, out string driveName)) {
            ShellOutput.WriteLine($"Drive name must contain 1 to {YVolumeManager.MaxDriveNameLength} letters.");
            return;
        }

        string? mountPoint = YVolumeManager.GetMountPointForPartition(partition);
        if (mountPoint is not null) {
            YVolumeManager.AssignDriveLetter(driveName, mountPoint);
            ShellOutput.WriteLine($"Assigned {driveName}:\\ -> {mountPoint} ({partition.Name}).");
            return;
        }

        int volumeNumber = YVolumeManager.GetNextMountNumber();
        if (YVolumeManager.MountVolume(partition, volumeNumber, driveName)) {
            ShellOutput.WriteLine($"Mounted {partition.Name} and assigned {driveName}:\\.");
        } else {
            ShellOutput.WriteLine($"Could not mount {partition.Name}. Check that it contains a supported FAT volume.");
        }
    }

    private static void HandleRescan() {
        IReadOnlyList<IBlockDevice> devices = StorageManager.Devices;
        for (int index = 0; index < devices.Count; index++) {
            StorageManager.RescanPartitions(devices[index]);
        }
        ShellOutput.WriteLine($"Rescanned {devices.Count} storage device(s). Existing mounts were not changed.");
    }

    private static bool TryGetDisk(string argument, out IBlockDevice? disk, out int diskIndex) {
        disk = null;
        if (!int.TryParse(argument, out diskIndex) || diskIndex < 0 || diskIndex >= StorageManager.Devices.Count) {
            ShellOutput.WriteLine($"Invalid disk index '{argument}'. Use 'disks' to list available disks.");
            return false;
        }
        disk = StorageManager.Devices[diskIndex];
        return true;
    }

    private static bool TryGetPartition(string diskArgument, string partitionArgument, out Partition? partition, out int diskIndex, out int partitionIndex) {
        partition = null;
        partitionIndex = -1;
        if (!TryGetDisk(diskArgument, out IBlockDevice? disk, out diskIndex) || disk is null) {
            return false;
        }
        IReadOnlyList<Partition> partitions = StorageManager.GetPartitions(disk);
        if (!int.TryParse(partitionArgument, out partitionIndex) || partitionIndex < 0 || partitionIndex >= partitions.Count) {
            ShellOutput.WriteLine($"Invalid partition index '{partitionArgument}' on Disk {diskIndex}.");
            return false;
        }
        partition = partitions[partitionIndex];
        return true;
    }

    private static Partition? FindMountedPartition(string mountPoint) {
        IReadOnlyList<VfsManager.VfsMount> mounts = VfsManager.Mounts;
        for (int index = 0; index < mounts.Count; index++) {
            if (string.Equals(mounts[index].MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase)) {
                return mounts[index].Partition;
            }
        }
        return null;
    }

    private static bool TryParseDriveName(string argument, out string driveName) {
        return YVolumeManager.TryNormalizeDriveName(argument, out driveName);
    }

    private static bool Confirm(string prompt) {
        string answer = ShellLineEditor.ReadLine(prompt, diskManagerMode: true);
        return answer is not null
            && (answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] SplitArgs(string input) {
        List<string> arguments = new();
        StringBuilder current = new();
        bool quoted = false;
        for (int index = 0; index < input.Length; index++) {
            char character = input[index];
            if (character == '"') {
                quoted = !quoted;
            } else if (char.IsWhiteSpace(character) && !quoted) {
                if (current.Length > 0) {
                    arguments.Add(current.ToString());
                    current.Clear();
                }
            } else {
                current.Append(character);
            }
        }
        if (current.Length > 0) {
            arguments.Add(current.ToString());
        }
        return arguments.ToArray();
    }
}
