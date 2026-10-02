using System;
using System.Collections.Generic;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.System.Storage;
using yggdrasilKernel.Vfs;

namespace yggdrasilKernel.Storage;

public static class DiskFormatter {
    public const string FatFilesystemName = "fat";
    public const ulong MetadataPartitionSectors = 204800;
    private const ulong FirstPartitionSector = 2048;
    private const ulong AlignmentSectors = 2048;
    private const ulong ResetPrimaryTableSectors = 34;
    private const ulong GptBackupTableSectors = 33;
    private const int WipeBufferBytes = 65536;
    private const byte Fat32MbrType = 0x0C;
    private const byte MetadataMbrType = 0xDA;
    private static readonly Guid MetadataGptType = new("7E6D4A31-6E59-4D53-9A61-594D45544131");
    private static readonly string[] _supportedFilesystems = { FatFilesystemName };

    public static IReadOnlyList<string> SupportedFilesystems => _supportedFilesystems;

    public static bool IsSupportedFilesystem(string filesystem) {
        for (int index = 0; index < _supportedFilesystems.Length; index++) {
            if (string.Equals(_supportedFilesystems[index], filesystem, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }
        return false;
    }

    public static bool TryInitializeDisk(IBlockDevice disk, bool reserveMetadata, out Partition? metadataPartition, out Partition? dataPartition) {
        metadataPartition = null;
        dataPartition = null;

        if (disk.BlockSize != 512 || disk.BlockCount <= FirstPartitionSector + 1 || StorageManager.GetPartitions(disk).Count != 0) {
            return false;
        }

        bool isGpt = Gpt.IsGpt(disk);
        bool isMbr = Mbr.IsMbr(disk);
        if (!isGpt && !isMbr) {
            if (!IsBlankDisk(disk)) {
                return false;
            }

            Mbr.Create(disk);
        }

        ulong dataStart = FirstPartitionSector;
        if (reserveMetadata) {
            if (disk.BlockCount < (1024UL * 1024 * 1024) / disk.BlockSize
                || disk.BlockCount - FirstPartitionSector <= MetadataPartitionSectors + AlignmentSectors) {
                return false;
            }

            if (!PartitionManager.Create(disk, FirstPartitionSector, MetadataPartitionSectors, MetadataMbrType, MetadataGptType)) {
                return false;
            }

            dataStart = AlignUp(FirstPartitionSector + MetadataPartitionSectors, AlignmentSectors);
        }

        if (dataStart >= disk.BlockCount) {
            return false;
        }

        ulong dataCount = Math.Min(disk.BlockCount - dataStart, uint.MaxValue);
        if (!PartitionManager.Create(disk, dataStart, dataCount, Fat32MbrType, Gpt.BasicDataPartitionType)) {
            StorageManager.RescanPartitions(disk);
            metadataPartition = FindPartition(disk, FirstPartitionSector, MetadataPartitionSectors);
            return false;
        }

        StorageManager.RescanPartitions(disk);
        metadataPartition = reserveMetadata ? FindPartition(disk, FirstPartitionSector, MetadataPartitionSectors) : null;
        dataPartition = FindPartition(disk, dataStart, dataCount);
        return dataPartition is not null;
    }

    public static bool TryCreateMetadataPartition(IBlockDevice disk, out Partition? metadataPartition) {
        metadataPartition = null;
        if (disk.BlockSize != 512 || disk.BlockCount <= FirstPartitionSector
            || (!Gpt.IsGpt(disk) && !Mbr.IsMbr(disk))) {
            return false;
        }

        List<Partition> partitions = new(StorageManager.GetPartitions(disk));
        partitions.Sort((left, right) => left.StartSector.CompareTo(right.StartSector));
        ulong candidate = FirstPartitionSector;

        foreach (Partition partition in partitions) {
            if (partition.StartSector >= candidate && partition.StartSector - candidate >= MetadataPartitionSectors) {
                break;
            }

            ulong end = partition.StartSector + partition.BlockCount;
            if (end > candidate) {
                candidate = AlignUp(end, AlignmentSectors);
            }
        }

        if (candidate >= disk.BlockCount || disk.BlockCount - candidate < MetadataPartitionSectors) {
            return false;
        }

        if (!PartitionManager.Create(disk, candidate, MetadataPartitionSectors, MetadataMbrType, MetadataGptType)) {
            return false;
        }

        StorageManager.RescanPartitions(disk);
        metadataPartition = FindPartition(disk, candidate, MetadataPartitionSectors);
        return metadataPartition is not null;
    }

    public static bool TryEnsureMetadataPartitionFirst(
        Partition selectedDataPartition,
        out Partition dataPartition,
        out Partition? metadataPartition,
        out string failureReason
    ) {
        dataPartition = selectedDataPartition;
        metadataPartition = null;
        failureReason = string.Empty;

        IBlockDevice disk = selectedDataPartition.Host;
        if (YVolumeManager.IsRemovableDevice(disk)) {
            return true;
        }
        if (!Gpt.IsGpt(disk) && !Mbr.IsMbr(disk)) {
            failureReason = "The disk has no supported partition table.";
            return false;
        }

        List<Partition> partitions = new(StorageManager.GetPartitions(disk));
        partitions.Sort((left, right) => left.StartSector.CompareTo(right.StartSector));
        if (partitions.Count == 0) {
            failureReason = "The disk has no partitions.";
            return false;
        }

        Partition? existingMetadata = null;
        for (int index = 0; index < partitions.Count; index++) {
            if (YVolumeManager.IsMetadataPartition(partitions[index])) {
                existingMetadata = partitions[index];
                break;
            }
        }
        if (existingMetadata is not null) {
            if (!ReferenceEquals(existingMetadata, partitions[0])) {
                failureReason = "A metadata partition already exists, but it is not the first partition; the layout was left unchanged.";
                return false;
            }
            metadataPartition = existingMetadata;
            dataPartition = FindPartition(disk, selectedDataPartition.StartSector, selectedDataPartition.BlockCount) ?? selectedDataPartition;
            return true;
        }

        Partition first = partitions[0];
        if (first.StartSector < FirstPartitionSector) {
            failureReason = "The first partition starts before the supported metadata layout.";
            return false;
        }

        if (first.StartSector - FirstPartitionSector >= MetadataPartitionSectors) {
            if (!TryCreateMetadataPartitionAt(disk, FirstPartitionSector, out metadataPartition)) {
                failureReason = "Could not create the metadata partition in the free space before the first volume.";
                return false;
            }
            dataPartition = FindPartition(disk, selectedDataPartition.StartSector, selectedDataPartition.BlockCount) ?? selectedDataPartition;
            return true;
        }

        ulong metadataEnd = AlignUp(FirstPartitionSector + MetadataPartitionSectors, AlignmentSectors);
        ulong firstEnd = first.StartSector + first.BlockCount;
        ulong nextPartitionStart = partitions.Count > 1 ? partitions[1].StartSector : disk.BlockCount;
        bool firstIsSelected = first.StartSector == selectedDataPartition.StartSector
            && first.BlockCount == selectedDataPartition.BlockCount;

        if (metadataEnd <= nextPartitionStart && first.BlockCount <= nextPartitionStart - metadataEnd
            && YVolumeManager.GetMountPointForPartition(first) is null) {
            PartitionManager.PartitionLocation location = new(first.StartSector, first.BlockCount);
            if (PartitionManager.MoveWithData(disk, location, metadataEnd)) {
                StorageManager.RescanPartitions(disk);
                Partition? movedFirst = FindPartition(disk, metadataEnd, first.BlockCount);
                if (movedFirst is not null && TryCreateMetadataPartitionAt(disk, FirstPartitionSector, out metadataPartition)) {
                    dataPartition = firstIsSelected
                        ? movedFirst
                        : FindPartition(disk, selectedDataPartition.StartSector, selectedDataPartition.BlockCount) ?? selectedDataPartition;
                    return true;
                }

                if (movedFirst is not null) {
                    PartitionManager.MoveWithData(disk, new PartitionManager.PartitionLocation(metadataEnd, first.BlockCount), first.StartSector);
                    StorageManager.RescanPartitions(disk);
                }
            }
        }

        if (!firstIsSelected || partitions.Count != 1 || first.StartSector != FirstPartitionSector || firstEnd <= metadataEnd) {
            failureReason = "There is not enough free space before the first partition to add metadata safely.";
            return false;
        }

        ulong resizedDataCount = firstEnd - metadataEnd;
        if (!PartitionManager.Delete(disk, new PartitionManager.PartitionLocation(first.StartSector, first.BlockCount))) {
            failureReason = "Could not remove the selected partition entry to create the metadata-first layout.";
            return false;
        }
        if (!PartitionManager.Create(disk, FirstPartitionSector, MetadataPartitionSectors, MetadataMbrType, MetadataGptType)) {
            StorageManager.RescanPartitions(disk);
            failureReason = "Could not create the metadata partition after removing the selected volume.";
            return false;
        }
        if (!PartitionManager.Create(disk, metadataEnd, resizedDataCount, Fat32MbrType, Gpt.BasicDataPartitionType)) {
            StorageManager.RescanPartitions(disk);
            failureReason = "Metadata was created, but the formatted data partition could not be recreated.";
            return false;
        }

        StorageManager.RescanPartitions(disk);
        metadataPartition = FindPartition(disk, FirstPartitionSector, MetadataPartitionSectors);
        dataPartition = FindPartition(disk, metadataEnd, resizedDataCount)!;
        if (metadataPartition is null || dataPartition is null) {
            failureReason = "The new metadata-first layout could not be rescanned.";
            return false;
        }
        return true;
    }

    public static bool TryResetDisk(
        IBlockDevice disk,
        bool reserveMetadata,
        out Partition? metadataPartition,
        out Partition? dataPartition
    ) {
        metadataPartition = null;
        dataPartition = null;
        if (disk.BlockSize != 512 || disk.BlockCount <= ResetPrimaryTableSectors + GptBackupTableSectors) {
            return false;
        }

        bool useGpt = Gpt.IsGpt(disk) || HasGptBackupHeader(disk);
        if (useGpt) {
            byte[] zeroedBackup = new byte[(int)(GptBackupTableSectors * disk.BlockSize)];
            disk.WriteBlock(disk.BlockCount - GptBackupTableSectors, GptBackupTableSectors, zeroedBackup);
            Gpt.Create(disk);
        } else {
            Mbr.Create(disk);
        }
        disk.Flush();
        StorageManager.RescanPartitions(disk);
        return TryInitializeDisk(disk, reserveMetadata, out metadataPartition, out dataPartition);
    }

    public static bool TryWipeDisk(IBlockDevice disk) {
        if (disk.BlockSize == 0 || disk.BlockSize > WipeBufferBytes) {
            return false;
        }

        ulong blocksPerChunk = (ulong)WipeBufferBytes / disk.BlockSize;
        byte[] zeros = new byte[(int)(blocksPerChunk * disk.BlockSize)];
        ulong block = 0;
        while (block < disk.BlockCount) {
            ulong blocksToWrite = Math.Min(blocksPerChunk, disk.BlockCount - block);
            int bytesToWrite = (int)(blocksToWrite * disk.BlockSize);
            disk.WriteBlock(block, blocksToWrite, zeros.AsSpan(0, bytesToWrite));
            block += blocksToWrite;
        }

        disk.Flush();
        StorageManager.RescanPartitions(disk);
        return StorageManager.GetPartitions(disk).Count == 0;
    }

    public static bool TryFormatNewVolume(Partition partition, string filesystem = FatFilesystemName) {
        if (partition.BlockSize != 512 || !IsSupportedFilesystem(filesystem)) {
            return false;
        }

        return Cosmos.Kernel.System.Vfs.VfsManager.TryFormat(filesystem, partition, null);
    }

    private static Partition? FindPartition(IBlockDevice disk, ulong startSector, ulong sectorCount) {
        IReadOnlyList<Partition> partitions = StorageManager.GetPartitions(disk);
        for (int index = 0; index < partitions.Count; index++) {
            Partition partition = partitions[index];
            if (partition.StartSector == startSector && partition.BlockCount == sectorCount) {
                return partition;
            }
        }
        return null;
    }

    private static bool TryCreateMetadataPartitionAt(IBlockDevice disk, ulong startSector, out Partition? metadataPartition) {
        metadataPartition = null;
        if (startSector >= disk.BlockCount || disk.BlockCount - startSector < MetadataPartitionSectors
            || !PartitionManager.Create(disk, startSector, MetadataPartitionSectors, MetadataMbrType, MetadataGptType)) {
            return false;
        }

        StorageManager.RescanPartitions(disk);
        metadataPartition = FindPartition(disk, startSector, MetadataPartitionSectors);
        return metadataPartition is not null;
    }

    private static bool IsBlankDisk(IBlockDevice disk) {
        if (disk.BlockCount < 35) {
            return false;
        }

        byte[] sectors = new byte[34 * 512];
        disk.ReadBlock(0, 34, sectors);
        for (int index = 0; index < sectors.Length; index++) {
            if (sectors[index] != 0) {
                return false;
            }
        }

        byte[] lastSector = new byte[512];
        disk.ReadBlock(disk.BlockCount - 1, 1, lastSector);
        for (int index = 0; index < lastSector.Length; index++) {
            if (lastSector[index] != 0) {
                return false;
            }
        }
        return true;
    }

    private static bool HasGptBackupHeader(IBlockDevice disk) {
        if (disk.BlockSize != 512 || disk.BlockCount == 0) {
            return false;
        }

        byte[] sector = new byte[512];
        disk.ReadBlock(disk.BlockCount - 1, 1, sector);
        return sector[0] == (byte)'E' && sector[1] == (byte)'F' && sector[2] == (byte)'I'
            && sector[3] == (byte)' ' && sector[4] == (byte)'P' && sector[5] == (byte)'A'
            && sector[6] == (byte)'R' && sector[7] == (byte)'T';
    }

    private static ulong AlignUp(ulong value, ulong alignment) {
        ulong remainder = value % alignment;
        return remainder == 0 ? value : value + alignment - remainder;
    }
}