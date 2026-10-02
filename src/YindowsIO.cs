using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Storage;
using Cosmos.Kernel.System.Vfs;

namespace yggdrasilKernel;

public static class YVolumeManager {
    public const int MaxDriveNameLength = 2;
    private const ulong MinimumFixedDiskBytes = 1024UL * 1024 * 1024;
    private const ulong MinimumMetadataFileBytes = 500UL * 1024 * 1024;
    private const int RegistryCopies = 2;
    private const int RegistryHeaderSize = 28;
    private const uint RegistryVersion = 3;
    private static readonly int RegistryRecordSize = 25 + MaxDriveNameLength;
    private static readonly int MaximumRegistryRecords = GetDriveNameCapacity(MaxDriveNameLength);
    private static readonly int RegistryCopySectors = (RegistryHeaderSize + MaximumRegistryRecords * RegistryRecordSize + 511) / 512;
    private const string RegistryMagic = "YVMETA01";
    private const string RegistryFileName = "/.metaDisk";

    private static readonly Dictionary<string, string> _driveLinks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _currentDirectories = new(StringComparer.OrdinalIgnoreCase);
    private static Partition? _metadataPartition;

    public static string CurrentDrive { get; private set; } = "C";

    public static void InitializeStorage() {
        IReadOnlyList<IBlockDevice> devices = StorageManager.Devices;
        if (devices.Count == 0) {
            ShellOutput.WriteLine("No storage devices found.");
            return;
        }

        _metadataPartition = FindMetadataPartition();
        for (int index = 0; index < devices.Count; index++) {
            IBlockDevice device = devices[index];
            if (StorageManager.GetPartitions(device).Count != 0) {
                continue;
            }

            bool removable = IsRemovableDevice(device);
            if (removable) {
                ShellOutput.WriteLine($"{device.Name}: under 1 GiB; treating as removable and not creating a metadata partition.");
            }

            bool initialized = DiskFormatter.TryInitializeDisk(device, !removable && _metadataPartition is null,
                out Partition? metadata, out Partition? data);
            if (metadata is not null) {
                _metadataPartition = metadata;
                InitializeMetadataPartition(metadata);
            }
            if (!initialized) {
                ShellOutput.WriteLine($"{device.Name}: no partitions initialized; disk is not blank, unsupported, or too small.");
                continue;
            }

            if (data is not null && !DiskFormatter.TryFormatNewVolume(data)) {
                ShellOutput.WriteLine($"{device.Name}: formatting the new data partition failed.");
            }
        }

        _metadataPartition = FindMetadataPartition();
        if (_metadataPartition is null) {
            for (int index = 0; index < devices.Count; index++) {
                IBlockDevice device = devices[index];
                if (IsRemovableDevice(device)) {
                    continue;
                }

                if (DiskFormatter.TryCreateMetadataPartition(device, out Partition? created)) {
                    _metadataPartition = created;
                    if (_metadataPartition is not null) {
                        InitializeMetadataPartition(_metadataPartition);
                    }
                    break;
                }
            }
        }

        if (_metadataPartition is null) {
            ShellOutput.WriteLine("No fixed-disk metadata partition is available; drive letters may not be persistent.");
        }

        List<Partition> volumes = new();
        List<ulong> deviceKeys = new();
        devices = StorageManager.Devices;
        for (int deviceIndex = 0; deviceIndex < devices.Count; deviceIndex++) {
            IBlockDevice device = devices[deviceIndex];
            ulong deviceKey = GetDeviceKey(device);
            IReadOnlyList<Partition> partitions = StorageManager.GetPartitions(device);
            for (int partitionIndex = 0; partitionIndex < partitions.Count; partitionIndex++) {
                Partition partition = partitions[partitionIndex];
                if (ReferenceEquals(partition, _metadataPartition) || IsMetadataPartition(partition)) {
                    continue;
                }
                volumes.Add(partition);
                deviceKeys.Add(deviceKey);
            }
        }

        List<string> mountPaths = new();
        bool[] mounted = new bool[volumes.Count];
        int volumeNumber = 1;
        for (int index = 0; index < volumes.Count; index++) {
            Partition partition = volumes[index];
            string mountPath = $"/Device/HarddiskVolume{volumeNumber++}";
            mountPaths.Add(mountPath);
            if (!VfsManager.TryMount("fat", partition, MountFlags.None, mountPath, out VfsManager.VfsMount? mount)) {
                ShellOutput.WriteLine($"Mount failed for {partition.Name} at {mountPath}; it was not formatted.");
                continue;
            }

            mounted[index] = true;
            ShellOutput.WriteLine($"Mounted {partition.Name} at {mount.MountPoint}");
        }

        List<DriveRecord> fileRecords = ReadRegistryFiles(mountPaths, mounted, volumes);
        List<DriveRecord> records;
        if (_metadataPartition is null) {
            records = fileRecords;
        } else {
            records = ReadRegistry(_metadataPartition, out _, out int activeSlot);
            if (activeSlot < 0) {
                records = fileRecords;
            }
        }
        string[] driveNames = AssignLetters(volumes, deviceKeys, records);
        for (int index = 0; index < volumes.Count; index++) {
            string driveName = driveNames[index];
            if (!mounted[index]) {
                continue;
            }
            if (driveName.Length == 0) {
                ShellOutput.WriteLine($"Skipping {volumes[index].Name}: no drive letters remain.");
                continue;
            }

            AssignDriveLetter(driveName, mountPaths[index]);
            ShellOutput.WriteLine($"{driveName}:\\ -> {mountPaths[index]} ({volumes[index].Name})");
        }

        if (_metadataPartition is not null) {
            SaveRegistry(_metadataPartition, records);
        }
        SaveRegistryFiles(mountPaths, mounted, volumes, records);

        if (!SetCurrentDrive("C")) {
            foreach (KeyValuePair<string, string> drive in _driveLinks) {
                SetCurrentDrive(drive.Key);
                break;
            }
        }
    }

    public static bool MountVolume(Partition partition, int volumeNumber, char driveLetter) {
        return MountVolume(partition, volumeNumber, driveLetter.ToString());
    }

    public static bool MountVolume(Partition partition, int volumeNumber, string driveName) {
        string mountPath = $"/Device/HarddiskVolume{volumeNumber}";
        return MountVolume(partition, mountPath, driveName);
    }

    public static bool MountVolume(Partition partition, string mountPath, char driveLetter) {
        return MountVolume(partition, mountPath, driveLetter.ToString());
    }

    public static bool MountVolume(Partition partition, string mountPath, string driveName) {
        string? existingMount = GetMountPointForPartition(partition);
        if (existingMount is not null) {
            if (!string.Equals(existingMount, mountPath, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
            AssignDriveLetter(driveName, existingMount);
            return true;
        }

        if (!VfsManager.TryMount("fat", partition, MountFlags.None, mountPath, out _)) {
            return false;
        }
        AssignDriveLetter(driveName, mountPath);
        return true;
    }

    public static void AssignDriveLetter(char driveLetter, string ntDevicePath) {
        AssignDriveLetter(driveLetter.ToString(), ntDevicePath);
    }

    public static void AssignDriveLetter(string driveName, string ntDevicePath) {
        if (!TryNormalizeDriveName(driveName, out string normalizedDriveName)) {
            throw new ArgumentException($"Drive name must contain 1 to {MaxDriveNameLength} ASCII letters.", nameof(driveName));
        }
        string root = ntDevicePath.TrimEnd('/');

        List<string> oldDriveNames = new();
        foreach (KeyValuePair<string, string> drive in _driveLinks) {
            if (!string.Equals(drive.Key, normalizedDriveName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(drive.Value, root, StringComparison.OrdinalIgnoreCase)) {
                oldDriveNames.Add(drive.Key);
            }
        }
        for (int index = 0; index < oldDriveNames.Count; index++) {
            _driveLinks.Remove(oldDriveNames[index]);
            _currentDirectories.Remove(oldDriveNames[index]);
        }

        if (_driveLinks.TryGetValue(normalizedDriveName, out string? previousRoot)
            && !string.Equals(previousRoot, root, StringComparison.OrdinalIgnoreCase)) {
            _currentDirectories.Remove(normalizedDriveName);
        }
        _driveLinks[normalizedDriveName] = root;
        if (!_currentDirectories.TryGetValue(normalizedDriveName, out string? currentDirectory)
            || !(currentDirectory.Equals(root, StringComparison.OrdinalIgnoreCase)
                || currentDirectory.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))) {
            _currentDirectories[normalizedDriveName] = root;
        }
        if (!string.Equals(CurrentDrive, normalizedDriveName, StringComparison.OrdinalIgnoreCase)
            && oldDriveNames.Exists(oldDriveName => string.Equals(oldDriveName, CurrentDrive, StringComparison.OrdinalIgnoreCase))) {
            CurrentDrive = normalizedDriveName;
        }
        PersistDriveAssignment(normalizedDriveName, root);
    }

    public static bool TryGetVolumePath(char driveLetter, out string ntDevicePath) {
        return TryGetVolumePath(driveLetter.ToString(), out ntDevicePath);
    }

    public static bool TryGetVolumePath(string driveName, out string ntDevicePath) {
        if (TryNormalizeDriveName(driveName, out string normalizedDriveName)) {
            return _driveLinks.TryGetValue(normalizedDriveName, out ntDevicePath!);
        }
        ntDevicePath = string.Empty;
        return false;
    }

    public static IReadOnlyDictionary<string, string> GetDrives() => _driveLinks;

    public static string GetDriveLetterForMount(string mountPoint) {
        string normalized = mountPoint.TrimEnd('/');
        foreach (KeyValuePair<string, string> drive in _driveLinks) {
            if (string.Equals(drive.Value, normalized, StringComparison.OrdinalIgnoreCase)) {
                return drive.Key;
            }
        }
        return string.Empty;
    }

    public static string? GetMountPointForPartition(Partition partition) {
        IReadOnlyList<VfsManager.VfsMount> mounts = VfsManager.Mounts;
        for (int index = 0; index < mounts.Count; index++) {
            if (ReferenceEquals(mounts[index].Partition, partition)) {
                return mounts[index].MountPoint;
            }
        }
        return null;
    }

    public static string GetNextAvailableDriveLetter() {
        return NextAvailableDriveName(new HashSet<string>(_driveLinks.Keys, StringComparer.OrdinalIgnoreCase));
    }

    public static int GetNextMountNumber() {
        int number = 1;
        while (VfsManager.TryGetMount($"/Device/HarddiskVolume{number}", out _)) {
            number++;
        }
        return number;
    }

    public static void RegisterMetadataPartition(Partition partition) {
        if (IsRemovableDevice(partition.Host)) {
            return;
        }
        if (!IsMetadataPartition(partition)) {
            InitializeMetadataPartition(partition);
        }
        if (IsMetadataPartition(partition)
            && (_metadataPartition is null || ReferenceEquals(_metadataPartition, partition))) {
            _metadataPartition = partition;
        }
    }

    public static bool SetCurrentDrive(char driveLetter) {
        return SetCurrentDrive(driveLetter.ToString());
    }

    public static bool SetCurrentDrive(string driveName) {
        if (!TryNormalizeDriveName(driveName, out string normalizedDriveName)
            || !_driveLinks.TryGetValue(normalizedDriveName, out string? root)) {
            return false;
        }

        CurrentDrive = normalizedDriveName;
        string currentDirectory = _currentDirectories.TryGetValue(normalizedDriveName, out string? saved) ? saved : root;
        Directory.SetCurrentDirectory(currentDirectory);
        return true;
    }

    public static void UpdateCurrentDirectory(string vfsPath) {
        foreach (KeyValuePair<string, string> drive in _driveLinks) {
            if (vfsPath.Equals(drive.Value, StringComparison.OrdinalIgnoreCase)
                || vfsPath.StartsWith(drive.Value + "/", StringComparison.OrdinalIgnoreCase)) {
                CurrentDrive = drive.Key;
                _currentDirectories[drive.Key] = vfsPath;
                return;
            }
        }
    }

    internal static bool TryGetCurrentDirectory(char driveLetter, out string directory) {
        return TryGetCurrentDirectory(driveLetter.ToString(), out directory);
    }

    internal static bool TryGetCurrentDirectory(string driveName, out string directory) {
        if (TryNormalizeDriveName(driveName, out string normalizedDriveName)
            && _currentDirectories.TryGetValue(normalizedDriveName, out string? saved)) {
            directory = saved;
            return true;
        }
        return TryGetVolumePath(driveName, out directory!);
    }

    public static bool TryNormalizeDriveName(string value, out string driveName) {
        string candidate = value.Trim().TrimEnd(':', '\\', '/');
        if (candidate.Length == 0 || candidate.Length > MaxDriveNameLength) {
            driveName = string.Empty;
            return false;
        }
        for (int index = 0; index < candidate.Length; index++) {
            char character = candidate[index];
            if (!((character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z'))) {
                driveName = string.Empty;
                return false;
            }
        }
        driveName = candidate.ToUpperInvariant();
        return true;
    }

    private static int GetDriveNameCapacity(int maximumLength) {
        long combinations = 1;
        long count = 0;
        for (int length = 1; length <= maximumLength; length++) {
            combinations *= 26;
            count += length == 1 ? combinations - 2 : combinations;
            if (count > int.MaxValue) {
                return int.MaxValue;
            }
        }
        return (int)count;
    }

    public static bool IsRemovableDevice(IBlockDevice device) {
        return device.BlockSize == 0 || device.BlockCount < MinimumFixedDiskBytes / device.BlockSize;
    }

    private static string[] AssignLetters(List<Partition> volumes, List<ulong> deviceKeys, List<DriveRecord> records) {
        string[] assigned = new string[volumes.Count];
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);

        for (int recordIndex = 0; recordIndex < records.Count; recordIndex++) {
            DriveRecord record = records[recordIndex];
            bool isPresent = false;
            for (int volumeIndex = 0; volumeIndex < volumes.Count; volumeIndex++) {
                if (Matches(record, deviceKeys[volumeIndex], volumes[volumeIndex])) {
                    isPresent = true;
                    break;
                }
            }
            if (!isPresent) {
                used.Add(record.Letter);
            }
        }

        for (int volumeIndex = 0; volumeIndex < volumes.Count; volumeIndex++) {
            Partition volume = volumes[volumeIndex];
            ulong deviceKey = deviceKeys[volumeIndex];
            int recordIndex = FindRecord(records, deviceKey, volume);
            string driveName = recordIndex >= 0 && !used.Contains(records[recordIndex].Letter)
                ? records[recordIndex].Letter
                : NextAvailableDriveName(used);

            if (driveName.Length == 0) {
                continue;
            }

            assigned[volumeIndex] = driveName;
            used.Add(driveName);
            DriveRecord updated = new(deviceKey, volume.StartSector, volume.BlockCount, driveName);
            if (recordIndex >= 0) {
                records[recordIndex] = updated;
            } else if (records.Count < MaximumRegistryRecords) {
                records.Add(updated);
            }
        }

        return assigned;
    }

    private static string NextAvailableDriveName(HashSet<string> used) {
        for (int length = 1; length <= MaxDriveNameLength; length++) {
            if (TryFindAvailableDriveName(new StringBuilder(length), length, used, out string driveName)) {
                return driveName;
            }
        }
        return string.Empty;
    }

    private static bool TryFindAvailableDriveName(StringBuilder prefix, int length, HashSet<string> used, out string driveName) {
        if (prefix.Length == length) {
            driveName = prefix.ToString();
            if (length == 1 && driveName[0] < 'C') {
                return false;
            }
            return !used.Contains(driveName);
        }

        for (char letter = 'A'; letter <= 'Z'; letter++) {
            prefix.Append(letter);
            if (TryFindAvailableDriveName(prefix, length, used, out driveName)) {
                return true;
            }
            prefix.Length--;
        }

        driveName = string.Empty;
        return false;
    }

    private static int FindRecord(List<DriveRecord> records, ulong deviceKey, Partition partition) {
        for (int index = 0; index < records.Count; index++) {
            if (Matches(records[index], deviceKey, partition)) {
                return index;
            }
        }
        return -1;
    }

    private static bool Matches(DriveRecord record, ulong deviceKey, Partition partition) {
        return record.DeviceKey == deviceKey && record.StartSector == partition.StartSector
            && record.SectorCount == partition.BlockCount;
    }

    private static ulong GetDeviceKey(IBlockDevice device) {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;
        string name = device.Name.ToUpperInvariant();
        for (int index = 0; index < name.Length; index++) {
            char value = name[index];
            hash = (hash ^ (byte)value) * prime;
            hash = (hash ^ (byte)(value >> 8)) * prime;
        }
        hash = (hash ^ device.BlockCount) * prime;
        hash = (hash ^ device.BlockSize) * prime;
        return hash;
    }

    private static Partition? FindMetadataPartition() {
        IReadOnlyList<IBlockDevice> devices = StorageManager.Devices;
        for (int deviceIndex = 0; deviceIndex < devices.Count; deviceIndex++) {
            IBlockDevice device = devices[deviceIndex];
            if (IsRemovableDevice(device)) {
                continue;
            }
            IReadOnlyList<Partition> partitions = StorageManager.GetPartitions(device);
            for (int partitionIndex = 0; partitionIndex < partitions.Count; partitionIndex++) {
                if (IsMetadataPartition(partitions[partitionIndex])) {
                    return partitions[partitionIndex];
                }
            }
        }
        return null;
    }

    public static bool IsMetadataPartition(Partition partition) {
        if (partition.BlockSize != 512 || partition.BlockCount < (ulong)RegistryCopySectors * RegistryCopies) {
            return false;
        }
        try {
            byte[] sector = new byte[512];
            partition.ReadBlock(0, 1, sector);
            if (IsCurrentRegistryHeader(sector)) {
                return true;
            }
            partition.ReadBlock((ulong)RegistryCopySectors, 1, sector);
            return IsCurrentRegistryHeader(sector);
        } catch (Exception) {
            return false;
        }
    }

    private static void InitializeMetadataPartition(Partition partition) {
        List<DriveRecord> records = ReadRegistry(partition, out ulong sequence, out int activeSlot);
        if (activeSlot < 0) {
            WriteRegistry(partition, records, sequence, activeSlot);
        }
    }

    private static List<DriveRecord> ReadRegistry(Partition partition, out ulong sequence, out int activeSlot) {
        sequence = 0;
        activeSlot = -1;
        List<DriveRecord> records = new();
        if (partition.BlockSize != 512 || partition.BlockCount < (ulong)RegistryCopySectors * RegistryCopies) {
            return records;
        }

        bool validFirst = TryReadRegistryCopy(partition, 0, out List<DriveRecord> firstRecords, out ulong firstSequence);
        bool validSecond = TryReadRegistryCopy(partition, 1, out List<DriveRecord> secondRecords, out ulong secondSequence);
        if (!validFirst && !validSecond) {
            return records;
        }

        activeSlot = validSecond && (!validFirst || secondSequence > firstSequence) ? 1 : 0;
        sequence = activeSlot == 0 ? firstSequence : secondSequence;
        return activeSlot == 0 ? firstRecords : secondRecords;
    }

    private static bool TryReadRegistryCopy(Partition partition, int slot, out List<DriveRecord> records, out ulong sequence) {
        records = new List<DriveRecord>();
        sequence = 0;
        byte[] data = new byte[RegistryCopySectors * 512];
        try {
            partition.ReadBlock((ulong)(slot * RegistryCopySectors), (ulong)RegistryCopySectors, data);
        } catch (Exception) {
            return false;
        }

        if (!IsCurrentRegistryHeader(data)) {
            return false;
        }

        uint count = ReadUInt32(data, 12);
        if (count > MaximumRegistryRecords || ReadUInt32(data, 24) != ComputeChecksum(data)) {
            return false;
        }

        sequence = ReadUInt64(data, 16);
        int offset = RegistryHeaderSize;
        for (uint index = 0; index < count; index++) {
            ulong deviceKey = ReadUInt64(data, offset);
            ulong recordStartSector = ReadUInt64(data, offset + 8);
            ulong sectorCount = ReadUInt64(data, offset + 16);
            int driveNameLength = data[offset + 24];
            if (driveNameLength <= 0 || driveNameLength > MaxDriveNameLength) {
                return false;
            }
            string driveName = Encoding.ASCII.GetString(data, offset + 25, driveNameLength);
            if (!TryNormalizeDriveName(driveName, out driveName) || sectorCount == 0) {
                return false;
            }
            records.Add(new DriveRecord(deviceKey, recordStartSector, sectorCount, driveName));
            offset += RegistryRecordSize;
        }
        return true;
    }

    private static void SaveRegistry(Partition partition, List<DriveRecord> records) {
        ReadRegistry(partition, out ulong sequence, out int activeSlot);
        WriteRegistry(partition, records, sequence, activeSlot);
    }

    private static List<DriveRecord> ReadRegistryFiles(List<string> mountPaths, bool[] mounted, IReadOnlyList<Partition> partitions) {
        List<DriveRecord> records = new();
        for (int mountIndex = 0; mountIndex < mountPaths.Count; mountIndex++) {
            if (!mounted[mountIndex]) {
                continue;
            }
            Partition partition = partitions[mountIndex];
            ulong ownDeviceKey = GetDeviceKey(partition.Host);

            List<DriveRecord> partitionRecords = new();
            TryReadRegistryFile(mountPaths[mountIndex] + RegistryFileName, partitionRecords);
            for (int index = 0; index < partitionRecords.Count; index++) {
                DriveRecord record = partitionRecords[index];
                int existingIndex = FindRecord(records, record.DeviceKey, partition);
                bool isOwnRecord = record.DeviceKey == ownDeviceKey
                    && record.StartSector == partition.StartSector && record.SectorCount == partition.BlockCount;
                if (existingIndex >= 0 && isOwnRecord) {
                    records[existingIndex] = record;
                } else if (existingIndex < 0 && records.Count < MaximumRegistryRecords) {
                    records.Add(record);
                }
            }
        }
        return records;
    }

    private static bool TryReadRegistryFile(string path, List<DriveRecord> records) {
        try {
            string[] lines = File.ReadAllText(path).Split('\n');
            if (lines.Length == 0 || lines[0].TrimEnd('\r') != RegistryMagic) {
                return false;
            }

            for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++) {
                string[] fields = lines[lineIndex].TrimEnd('\r').Split('|');
                if (fields.Length != 4
                    || !ulong.TryParse(fields[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong deviceKey)
                    || !ulong.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong startSector)
                    || !ulong.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong sectorCount)
                    || !TryNormalizeDriveName(fields[3], out string driveName)
                    || sectorCount == 0) {
                    continue;
                }

                DriveRecord record = new(deviceKey, startSector, sectorCount, driveName);
                if (!ContainsRecord(records, record) && records.Count < MaximumRegistryRecords) {
                    records.Add(record);
                }
            }
            return true;
        } catch (IOException) {
            return false;
        }
    }

    private static void PersistDriveAssignment(string driveName, string mountPath) {
        Partition? target = null;
        IReadOnlyList<VfsManager.VfsMount> mounts = VfsManager.Mounts;
        List<string> mountPaths = new(mounts.Count);
        List<Partition> partitions = new(mounts.Count);
        List<bool> mountedPartitions = new(mounts.Count);
        for (int index = 0; index < mounts.Count; index++) {
            VfsManager.VfsMount mount = mounts[index];
            if (mount.Partition is null) {
                continue;
            }
            mountPaths.Add(mount.MountPoint);
            partitions.Add(mount.Partition);
            mountedPartitions.Add(true);
            if (string.Equals(mount.MountPoint, mountPath, StringComparison.OrdinalIgnoreCase)) {
                target = mount.Partition;
            }
        }
        if (target is null) {
            return;
        }

        bool[] mounted = mountedPartitions.ToArray();
        List<DriveRecord> fileRecords = ReadRegistryFiles(mountPaths, mounted, partitions);
        List<DriveRecord> records;
        if (_metadataPartition is null) {
            records = fileRecords;
        } else {
            records = ReadRegistry(_metadataPartition, out _, out int activeSlot);
            if (activeSlot < 0) {
                records = fileRecords;
            }
        }
        DriveRecord assignment = new(GetDeviceKey(target.Host), target.StartSector, target.BlockCount, driveName);
        for (int index = records.Count - 1; index >= 0; index--) {
            DriveRecord record = records[index];
            bool sameVolume = record.DeviceKey == assignment.DeviceKey
                && record.StartSector == assignment.StartSector && record.SectorCount == assignment.SectorCount;
            if (string.Equals(record.Letter, driveName, StringComparison.OrdinalIgnoreCase) && !sameVolume) {
                records.RemoveAt(index);
            }
        }

        int recordIndex = FindRecord(records, assignment.DeviceKey, target);
        if (recordIndex >= 0) {
            records[recordIndex] = assignment;
        } else if (records.Count < MaximumRegistryRecords) {
            records.Add(assignment);
        }

        if (_metadataPartition is not null) {
            SaveRegistry(_metadataPartition, records);
        }
        SaveRegistryFiles(mountPaths, mounted, partitions, records);
    }

    private static bool ContainsRecord(List<DriveRecord> records, DriveRecord candidate) {
        for (int index = 0; index < records.Count; index++) {
            DriveRecord record = records[index];
            if (record.DeviceKey == candidate.DeviceKey && record.StartSector == candidate.StartSector
                && record.SectorCount == candidate.SectorCount) {
                return true;
            }
        }
        return false;
    }

    private static void SaveRegistryFiles(List<string> mountPaths, bool[] mounted, IReadOnlyList<Partition> partitions, List<DriveRecord> records) {
        StringBuilder contents = new();
        contents.Append(RegistryMagic).Append('\n');
        for (int index = 0; index < records.Count; index++) {
            DriveRecord record = records[index];
            contents.Append(record.DeviceKey.ToString("X16", CultureInfo.InvariantCulture)).Append('|')
                .Append(record.StartSector.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(record.SectorCount.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(record.Letter).Append('\n');
        }

        string registry = contents.ToString();
        for (int mountIndex = 0; mountIndex < mountPaths.Count; mountIndex++) {
            if (!mounted[mountIndex] || !ShouldPersistMetadataFile(partitions[mountIndex])) {
                continue;
            }
            try {
                File.WriteAllText(mountPaths[mountIndex] + RegistryFileName, registry);
            } catch (IOException) {
                ShellOutput.WriteLine($"Could not save drive-letter metadata on {mountPaths[mountIndex]}.");
            }
        }
    }

    private static bool ShouldPersistMetadataFile(Partition partition) {
        return partition.BlockSize != 0
            && (partition.BlockCount > MinimumMetadataFileBytes / partition.BlockSize
                || IsRemovableDevice(partition.Host));
    }

    public static string FormatSize(ulong bytes) {
        const ulong unit = 1024;
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB", "PiB" };
        double value = bytes;
        int unitIndex = 0;
        while (value >= unit && unitIndex < units.Length - 1) {
            value /= unit;
            unitIndex++;
        }
        return value.ToString(unitIndex == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + " " + units[unitIndex];
    }

    private static void WriteRegistry(Partition partition, List<DriveRecord> records, ulong sequence, int activeSlot) {
        if (partition.BlockSize != 512 || records.Count > MaximumRegistryRecords) {
            return;
        }

        int targetSlot = activeSlot == 0 ? 1 : 0;
        byte[] data = new byte[RegistryCopySectors * 512];
        WriteMagic(data, 0);
        WriteUInt32(data, 8, RegistryVersion);
        WriteUInt32(data, 12, (uint)records.Count);
        WriteUInt64(data, 16, sequence + 1);
        int offset = RegistryHeaderSize;
        for (int index = 0; index < records.Count; index++) {
            DriveRecord record = records[index];
            WriteUInt64(data, offset, record.DeviceKey);
            WriteUInt64(data, offset + 8, record.StartSector);
            WriteUInt64(data, offset + 16, record.SectorCount);
            data[offset + 24] = (byte)record.Letter.Length;
            for (int letterIndex = 0; letterIndex < record.Letter.Length; letterIndex++) {
                data[offset + 25 + letterIndex] = (byte)record.Letter[letterIndex];
            }
            offset += RegistryRecordSize;
        }
        WriteUInt32(data, 24, ComputeChecksum(data));
        partition.WriteBlock((ulong)(targetSlot * RegistryCopySectors), (ulong)RegistryCopySectors, data);
        partition.Flush();
    }

    private static bool HasMagic(byte[] data, int offset) {
        for (int index = 0; index < RegistryMagic.Length; index++) {
            if (data[offset + index] != (byte)RegistryMagic[index]) {
                return false;
            }
        }
        return true;
    }

    private static bool IsCurrentRegistryHeader(byte[] data) {
        return HasMagic(data, 0) && ReadUInt32(data, 8) == RegistryVersion;
    }

    private static void WriteMagic(byte[] data, int offset) {
        for (int index = 0; index < RegistryMagic.Length; index++) {
            data[offset + index] = (byte)RegistryMagic[index];
        }
    }

    private static uint ComputeChecksum(byte[] data) {
        uint checksum = 0xFFFFFFFF;
        for (int index = 0; index < data.Length; index++) {
            byte value = index >= 24 && index < 28 ? (byte)0 : data[index];
            checksum ^= value;
            for (int bit = 0; bit < 8; bit++) {
                checksum = (checksum & 1) != 0 ? (checksum >> 1) ^ 0xEDB88320 : checksum >> 1;
            }
        }
        return ~checksum;
    }

    private static uint ReadUInt32(byte[] data, int offset) {
        return (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);
    }

    private static ulong ReadUInt64(byte[] data, int offset) {
        return ReadUInt32(data, offset) | ((ulong)ReadUInt32(data, offset + 4) << 32);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value) {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteUInt64(byte[] data, int offset, ulong value) {
        WriteUInt32(data, offset, (uint)value);
        WriteUInt32(data, offset + 4, (uint)(value >> 32));
    }

    private readonly struct DriveRecord {
        public DriveRecord(ulong deviceKey, ulong startSector, ulong sectorCount, string letter) {
            DeviceKey = deviceKey;
            StartSector = startSector;
            SectorCount = sectorCount;
            Letter = letter;
        }

        public ulong DeviceKey { get; }
        public ulong StartSector { get; }
        public ulong SectorCount { get; }
        public string Letter { get; }
    }
}

public static class YPath {
    public static string ToVfs(string path) {
        if (string.IsNullOrWhiteSpace(path)) {
            return Directory.GetCurrentDirectory();
        }

        string value = path;
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal)
            || value.StartsWith(@"\\.\", StringComparison.Ordinal)
            || value.StartsWith("//?/", StringComparison.Ordinal)
            || value.StartsWith("//./", StringComparison.Ordinal)) {
            value = value.Substring(4);
        }
        value = value.Replace('\\', '/');

        string root;
        string remainder;
        int floor;

        int colonIndex = value.IndexOf(':');
        if (colonIndex > 0 && colonIndex <= YVolumeManager.MaxDriveNameLength
            && YVolumeManager.TryNormalizeDriveName(value.Substring(0, colonIndex), out string driveName)) {
            string suffix = value.Substring(colonIndex + 1);
            bool rooted = suffix.StartsWith("/", StringComparison.Ordinal);
            string volumeRoot = YVolumeManager.TryGetVolumePath(driveName, out string mappedRoot)
                ? mappedRoot
                : $"/Unmounted_{driveName}";
            floor = CountSegments(volumeRoot);
            if (rooted) {
                root = volumeRoot;
                remainder = suffix.TrimStart('/');
            } else {
                root = YVolumeManager.TryGetCurrentDirectory(driveName, out string driveDirectory)
                    ? driveDirectory
                    : volumeRoot;
                remainder = suffix;
            }
        } else if (value.StartsWith("/Device/", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/Device", StringComparison.OrdinalIgnoreCase)) {
            root = "/";
            remainder = value.TrimStart('/');
            floor = 0;
        } else if (value.StartsWith("/", StringComparison.Ordinal)) {
            root = ResolveRootedPath(value, out remainder, out floor);
        } else {
            root = Directory.GetCurrentDirectory();
            remainder = value;
            floor = YVolumeManager.TryGetVolumePath(YVolumeManager.CurrentDrive, out string currentRoot)
                && IsWithin(root, currentRoot) ? CountSegments(currentRoot) : 0;
        }

        return NormalizeUnbounded(root, remainder, floor);
    }

    public static string ToWindowsDisplay(string anyPath) {
        string vfsPath = ToVfs(anyPath);
        string selectedDrive = string.Empty;
        string selectedRoot = string.Empty;
        foreach (KeyValuePair<string, string> drive in YVolumeManager.GetDrives()) {
            if ((vfsPath.Equals(drive.Value, StringComparison.OrdinalIgnoreCase)
                || vfsPath.StartsWith(drive.Value + "/", StringComparison.OrdinalIgnoreCase))
                && drive.Value.Length > selectedRoot.Length) {
                selectedDrive = drive.Key;
                selectedRoot = drive.Value;
            }
        }

        if (selectedDrive.Length == 0) {
            return vfsPath.Replace('/', '\\');
        }
        string subPath = vfsPath.Length == selectedRoot.Length
            ? string.Empty
            : vfsPath.Substring(selectedRoot.Length + 1).Replace('/', '\\');
        return subPath.Length == 0 ? $"{selectedDrive}:\\" : $"{selectedDrive}:\\{subPath}";
    }

    private static string ResolveRootedPath(string value, out string remainder, out int floor) {
        if (YVolumeManager.TryGetVolumePath(YVolumeManager.CurrentDrive, out string currentRoot)) {
            remainder = value.TrimStart('/');
            floor = CountSegments(currentRoot);
            return currentRoot;
        }
        remainder = value.TrimStart('/');
        floor = 0;
        return "/";
    }

    private static string NormalizeUnbounded(string root, string relative, int floor) {
        List<string> segments = new();
        PushSegments(root, segments, 0);
        floor = Math.Min(floor, segments.Count);
        PushSegments(relative, segments, floor);
        if (segments.Count == 0) {
            return "/";
        }

        StringBuilder builder = new(root.Length + relative.Length + 2);
        for (int index = 0; index < segments.Count; index++) {
            builder.Append('/');
            builder.Append(segments[index]);
        }
        return builder.ToString();
    }

    private static void PushSegments(string path, List<string> segments, int floor) {
        int start = 0;
        for (int index = 0; index <= path.Length; index++) {
            if (index != path.Length && path[index] != '/') {
                continue;
            }
            int length = index - start;
            if (length > 0) {
                string segment = path.Substring(start, length);
                if (segment == "..") {
                    if (segments.Count > floor) {
                        segments.RemoveAt(segments.Count - 1);
                    }
                } else if (segment != ".") {
                    segments.Add(segment);
                }
            }
            start = index + 1;
        }
    }

    private static int CountSegments(string path) {
        int count = 0;
        bool insideSegment = false;
        for (int index = 0; index < path.Length; index++) {
            if (path[index] == '/') {
                insideSegment = false;
            } else if (!insideSegment) {
                count++;
                insideSegment = true;
            }
        }
        return count;
    }

    private static bool IsWithin(string path, string root) {
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }
}

public static class YFile {
    public static bool Exists(string path) => File.Exists(YPath.ToVfs(path));
    public static void WriteAllText(string path, string content) => File.WriteAllText(YPath.ToVfs(path), content);
    public static void AppendAllText(string path, string content) => File.AppendAllText(YPath.ToVfs(path), content);
    public static string ReadAllText(string path) => File.ReadAllText(YPath.ToVfs(path));
    public static byte[] ReadAllBytes(string path) => File.ReadAllBytes(YPath.ToVfs(path));
    public static void WriteAllBytes(string path, byte[] bytes) => File.WriteAllBytes(YPath.ToVfs(path), bytes);
    public static void Delete(string path) => File.Delete(YPath.ToVfs(path));
    public static void Copy(string source, string destination, bool overwrite = false) => File.Copy(YPath.ToVfs(source), YPath.ToVfs(destination), overwrite);
    public static void Move(string source, string destination, bool overwrite = false) => File.Move(YPath.ToVfs(source), YPath.ToVfs(destination), overwrite);
    public static FileStream Open(string path, FileMode mode, FileAccess access = FileAccess.ReadWrite) => new(YPath.ToVfs(path), mode, access);
}

public static class YDirectory {
    public static bool Exists(string path) => Directory.Exists(YPath.ToVfs(path));
    public static DirectoryInfo CreateDirectory(string path) => Directory.CreateDirectory(YPath.ToVfs(path));
    public static void Delete(string path, bool recursive = false) => Directory.Delete(YPath.ToVfs(path), recursive);

    public static string[] GetFiles(string path, string searchPattern = "*") {
        string[] files = Directory.GetFiles(YPath.ToVfs(path), searchPattern);
        for (int index = 0; index < files.Length; index++) {
            files[index] = YPath.ToWindowsDisplay(files[index]);
        }
        return files;
    }

    public static string[] GetDirectories(string path, string searchPattern = "*") {
        string[] directories = Directory.GetDirectories(YPath.ToVfs(path), searchPattern);
        for (int index = 0; index < directories.Length; index++) {
            directories[index] = YPath.ToWindowsDisplay(directories[index]);
        }
        return directories;
    }

    public static void SetCurrentDirectory(string path) {
        string vfsPath = YPath.ToVfs(path);
        Directory.SetCurrentDirectory(vfsPath);
        YVolumeManager.UpdateCurrentDirectory(vfsPath);
    }

    public static string GetCurrentDirectory() => YPath.ToWindowsDisplay(Directory.GetCurrentDirectory());
}