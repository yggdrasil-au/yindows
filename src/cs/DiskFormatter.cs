using System;
using System.Collections.Generic;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.System.Storage;
using Cosmos.Kernel.System.Filesystems.Fat;

namespace yggdrasilKernel;

public static class DiskFormatter {
    public const ulong MetadataPartitionSectors = 204800;
    private const ulong FirstPartitionSector = 2048;
    private const ulong AlignmentSectors = 2048;
    private const byte Fat32MbrType = 0x0C;
    private const byte MetadataMbrType = 0xDA;
    private static readonly Guid MetadataGptType = new("7E6D4A31-6E59-4D53-9A61-594D45544131");

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

    public static bool TryFormatNewVolume(Partition partition) {
        if (partition.BlockSize != 512) {
            return false;
        }

        FatType type = partition.BlockCount < 32768 ? FatType.Fat12
            : partition.BlockCount < 1048576 ? FatType.Fat16
            : FatType.Fat32;

        return Cosmos.Kernel.System.Vfs.VfsManager.TryFormat("fat", partition, new FatFormatOptions {
            Type = type,
            VolumeLabel = "YINDOWS",
        });
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

    private static ulong AlignUp(ulong value, ulong alignment) {
        ulong remainder = value % alignment;
        return remainder == 0 ? value : value + alignment - remainder;
    }
}