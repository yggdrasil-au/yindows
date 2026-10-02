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
    private const ulong MinimumFixedDiskBytes = 1024UL * 1024 * 1024;
    private const int RegistryCopySectors = 2;
    private const int RegistryCopies = 2;
    private const int RegistryHeaderSize = 28;
    private const int RegistryRecordSize = 25;
    private const int MaximumRegistryRecords = (RegistryCopySectors * 512 - RegistryHeaderSize) / RegistryRecordSize;
    private const uint RegistryVersion = 1;
    private const string RegistryMagic = "YVMETA01";
    private const string RegistryFileName = "/.YVOLMAP";

    private static readonly Dictionary<char, string> _driveLinks = new();
    private static readonly Dictionary<char, string> _currentDirectories = new();
    private static Partition? _metadataPartition;

    public static char CurrentDrive { get; private set; } = 'C';

    public static void InitializeStorage() {
        IReadOnlyList<IBlockDevice> devices = StorageManager.Devices;
        if (devices.Count == 0) {
            Console.WriteLine("No storage devices found.");
            return;
        }

        for (int index = 0; index < devices.Count; index++) {
            IBlockDevice device = devices[index];
            if (StorageManager.GetPartitions(device).Count != 0) {
                continue;
            }

            bool removable = IsRemovableDevice(device);
            if (removable) {
                Console.WriteLine($"{device.Name}: under 1 GiB; treating as removable and not creating a metadata partition.");
            }

            if (!DiskFormatter.TryInitializeDisk(device, !removable, out Partition? metadata, out Partition? data)) {
                Console.WriteLine($"{device.Name}: no partitions initialized; disk is not blank, unsupported, or too small.");
                continue;
            }

            if (metadata is not null) {
                InitializeMetadataPartition(metadata);
            }
            if (data is not null && !DiskFormatter.TryFormatNewVolume(data)) {
                Console.WriteLine($"{device.Name}: formatting the new data partition failed.");
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
            Console.WriteLine("No fixed-disk metadata partition is available; drive letters are temporary for this boot.");
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
                Console.WriteLine($"Mount failed for {partition.Name} at {mountPath}; it was not formatted.");
                continue;
            }

            mounted[index] = true;
            Console.WriteLine($"Mounted {partition.Name} at {mount.MountPoint}");
            if (VfsManager.TryStatFs(mountPath, out VfsStatFs stats)) {
                ulong freeBytes = stats.Bavail * stats.BlockSize;
                ulong totalBytes = stats.Blocks * stats.BlockSize;
                Console.WriteLine($"  {freeBytes} of {totalBytes} bytes free");
            }
        }

        List<DriveRecord> records = _metadataPartition is null
            ? ReadRegistryFiles(mountPaths, mounted)
            : ReadRegistry(_metadataPartition, out _, out _);
        char[] letters = AssignLetters(volumes, deviceKeys, records);
        for (int index = 0; index < volumes.Count; index++) {
            char letter = letters[index];
            if (!mounted[index]) {
                continue;
            }
            if (letter == '\0') {
                Console.WriteLine($"Skipping {volumes[index].Name}: no drive letters remain.");
                continue;
            }

            AssignDriveLetter(letter, mountPaths[index]);
            Console.WriteLine($"{letter}:\\ -> {mountPaths[index]} ({volumes[index].Name})");
        }

        if (_metadataPartition is not null) {
            SaveRegistry(_metadataPartition, records);
        } else {
            SaveRegistryFiles(mountPaths, mounted, records);
        }

        if (!SetCurrentDrive('C')) {
            foreach (KeyValuePair<char, string> drive in _driveLinks) {
                SetCurrentDrive(drive.Key);
                break;
            }
        }
    }

    public static bool MountVolume(Partition partition, int volumeNumber, char driveLetter) {
        string mountPath = $"/Device/HarddiskVolume{volumeNumber}";
        if (!VfsManager.TryMount("fat", partition, MountFlags.None, mountPath, out _)) {
            return false;
        }
        AssignDriveLetter(driveLetter, mountPath);
        return true;
    }

    public static void AssignDriveLetter(char driveLetter, string ntDevicePath) {
        char upper = char.ToUpperInvariant(driveLetter);
        string root = ntDevicePath.TrimEnd('/');
        _driveLinks[upper] = root;
        if (!_currentDirectories.ContainsKey(upper)) {
            _currentDirectories[upper] = root;
        }
    }

    public static bool TryGetVolumePath(char driveLetter, out string ntDevicePath) {
        return _driveLinks.TryGetValue(char.ToUpperInvariant(driveLetter), out ntDevicePath!);
    }

    public static IReadOnlyDictionary<char, string> GetDrives() => _driveLinks;

    public static bool SetCurrentDrive(char driveLetter) {
        char upper = char.ToUpperInvariant(driveLetter);
        if (!_driveLinks.TryGetValue(upper, out string? root)) {
            return false;
        }

        CurrentDrive = upper;
        string currentDirectory = _currentDirectories.TryGetValue(upper, out string? saved) ? saved : root;
        Directory.SetCurrentDirectory(currentDirectory);
        return true;
    }

    public static void UpdateCurrentDirectory(string vfsPath) {
        foreach (KeyValuePair<char, string> drive in _driveLinks) {
            if (vfsPath.Equals(drive.Value, StringComparison.OrdinalIgnoreCase)
                || vfsPath.StartsWith(drive.Value + "/", StringComparison.OrdinalIgnoreCase)) {
                CurrentDrive = drive.Key;
                _currentDirectories[drive.Key] = vfsPath;
                return;
            }
        }
    }

    internal static bool TryGetCurrentDirectory(char driveLetter, out string directory) {
        char upper = char.ToUpperInvariant(driveLetter);
        if (_currentDirectories.TryGetValue(upper, out string? saved)) {
            directory = saved;
            return true;
        }
        return TryGetVolumePath(upper, out directory!);
    }

    public static bool IsRemovableDevice(IBlockDevice device) {
        return device.BlockSize == 0 || device.BlockCount < MinimumFixedDiskBytes / device.BlockSize;
    }

    private static char[] AssignLetters(List<Partition> volumes, List<ulong> deviceKeys, List<DriveRecord> records) {
        char[] assigned = new char[volumes.Count];
        HashSet<char> used = new();

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
            char letter = recordIndex >= 0 && !used.Contains(records[recordIndex].Letter)
                ? records[recordIndex].Letter
                : NextAvailableLetter(used);

            if (letter == '\0') {
                continue;
            }

            assigned[volumeIndex] = letter;
            used.Add(letter);
            DriveRecord updated = new(deviceKey, volume.StartSector, volume.BlockCount, letter);
            if (recordIndex >= 0) {
                records[recordIndex] = updated;
            } else if (records.Count < MaximumRegistryRecords) {
                records.Add(updated);
            }
        }

        return assigned;
    }

    private static char NextAvailableLetter(HashSet<char> used) {
        for (char letter = 'C'; letter <= 'Z'; letter++) {
            if (!used.Contains(letter)) {
                return letter;
            }
        }
        return '\0';
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

    private static bool IsMetadataPartition(Partition partition) {
        if (partition.BlockSize != 512 || partition.BlockCount < RegistryCopySectors * RegistryCopies) {
            return false;
        }
        try {
            byte[] sector = new byte[512];
            partition.ReadBlock(0, 1, sector);
            return HasMagic(sector, 0);
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
        if (partition.BlockSize != 512 || partition.BlockCount < RegistryCopySectors * RegistryCopies) {
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
            partition.ReadBlock((ulong)(slot * RegistryCopySectors), RegistryCopySectors, data);
        } catch (Exception) {
            return false;
        }

        if (!HasMagic(data, 0) || ReadUInt32(data, 8) != RegistryVersion) {
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
            ulong startSector = ReadUInt64(data, offset + 8);
            ulong sectorCount = ReadUInt64(data, offset + 16);
            char letter = (char)data[offset + 24];
            if (letter < 'C' || letter > 'Z' || sectorCount == 0) {
                return false;
            }
            records.Add(new DriveRecord(deviceKey, startSector, sectorCount, letter));
            offset += RegistryRecordSize;
        }
        return true;
    }

    private static void SaveRegistry(Partition partition, List<DriveRecord> records) {
        ReadRegistry(partition, out ulong sequence, out int activeSlot);
        WriteRegistry(partition, records, sequence, activeSlot);
    }

    private static List<DriveRecord> ReadRegistryFiles(List<string> mountPaths, bool[] mounted) {
        List<DriveRecord> records = new();
        for (int mountIndex = 0; mountIndex < mountPaths.Count; mountIndex++) {
            if (!mounted[mountIndex]) {
                continue;
            }

            try {
                string[] lines = File.ReadAllText(mountPaths[mountIndex] + RegistryFileName).Split('\n');
                if (lines.Length == 0 || lines[0].TrimEnd('\r') != RegistryMagic) {
                    continue;
                }

                for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++) {
                    string[] fields = lines[lineIndex].TrimEnd('\r').Split('|');
                    if (fields.Length != 4
                        || !ulong.TryParse(fields[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong deviceKey)
                        || !ulong.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong startSector)
                        || !ulong.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong sectorCount)
                        || fields[3].Length != 1 || fields[3][0] < 'C' || fields[3][0] > 'Z'
                        || sectorCount == 0) {
                        continue;
                    }

                    DriveRecord record = new(deviceKey, startSector, sectorCount, fields[3][0]);
                    if (FindRecord(records, record.DeviceKey, new Partition(record.DeviceKey.ToString(CultureInfo.InvariantCulture), record.StartSector, record.SectorCount, "")) < 0
                        && records.Count < MaximumRegistryRecords) {
                        records.Add(record);
                    }
                }
            } catch (IOException) {
            }
        }
        return records;
    }

    private static void SaveRegistryFiles(List<string> mountPaths, bool[] mounted, List<DriveRecord> records) {
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
            if (!mounted[mountIndex]) {
                continue;
            }
            try {
                File.WriteAllText(mountPaths[mountIndex] + RegistryFileName, registry);
            } catch (IOException) {
                Console.WriteLine($"Could not save drive-letter metadata on {mountPaths[mountIndex]}.");
            }
        }
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
            data[offset + 24] = (byte)record.Letter;
            offset += RegistryRecordSize;
        }
        WriteUInt32(data, 24, ComputeChecksum(data));
        partition.WriteBlock((ulong)(targetSlot * RegistryCopySectors), RegistryCopySectors, data);
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
        public DriveRecord(ulong deviceKey, ulong startSector, ulong sectorCount, char letter) {
            DeviceKey = deviceKey;
            StartSector = startSector;
            SectorCount = sectorCount;
            Letter = letter;
        }

        public ulong DeviceKey { get; }
        public ulong StartSector { get; }
        public ulong SectorCount { get; }
        public char Letter { get; }
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

        if (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':') {
            char drive = char.ToUpperInvariant(value[0]);
            string volumeRoot = YVolumeManager.TryGetVolumePath(drive, out string mappedRoot) ? mappedRoot : $"/Unmounted_{drive}";
            floor = CountSegments(volumeRoot);
            if (value.Length > 2 && value[2] == '/') {
                root = volumeRoot;
                remainder = value.Substring(3);
            } else {
                root = YVolumeManager.TryGetCurrentDirectory(drive, out string driveDirectory) ? driveDirectory : volumeRoot;
                remainder = value.Substring(2);
            }
        } else if (value.Length >= 2 && value[0] == '/' && char.IsLetter(value[1])
            && (value.Length == 2 || value[2] == '/')) {
            char drive = char.ToUpperInvariant(value[1]);
            if (YVolumeManager.TryGetVolumePath(drive, out string mappedRoot)) {
                root = mappedRoot;
                remainder = value.Length > 2 ? value.Substring(3) : string.Empty;
                floor = CountSegments(root);
            } else {
                root = "/";
                remainder = value.TrimStart('/');
                floor = 0;
            }
        } else if (value.StartsWith("/Device/", StringComparison.OrdinalIgnoreCase) || value.Equals("/Device", StringComparison.OrdinalIgnoreCase)) {
            root = "/";
            remainder = value.TrimStart('/');
            floor = 0;
        } else if (value.StartsWith('/')) {
            if (value == "/") {
                root = "/";
                remainder = string.Empty;
                floor = 0;
            } else if (YVolumeManager.TryGetVolumePath(YVolumeManager.CurrentDrive, out string currentRoot)) {
                root = currentRoot;
                remainder = value.TrimStart('/');
                floor = CountSegments(root);
            } else {
                root = "/";
                remainder = value.TrimStart('/');
                floor = 0;
            }
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
        char selectedDrive = '\0';
        string selectedRoot = string.Empty;
        foreach (KeyValuePair<char, string> drive in YVolumeManager.GetDrives()) {
            if ((vfsPath.Equals(drive.Value, StringComparison.OrdinalIgnoreCase)
                || vfsPath.StartsWith(drive.Value + "/", StringComparison.OrdinalIgnoreCase))
                && drive.Value.Length > selectedRoot.Length) {
                selectedDrive = drive.Key;
                selectedRoot = drive.Value;
            }
        }

        if (selectedDrive == '\0') {
            return vfsPath.Replace('/', '\\');
        }
        string subPath = vfsPath.Length == selectedRoot.Length
            ? string.Empty
            : vfsPath.Substring(selectedRoot.Length + 1).Replace('/', '\\');
        return subPath.Length == 0 ? $"{selectedDrive}:\\" : $"{selectedDrive}:\\{subPath}";
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