# YggdrasilOS

YggdrasilOS is an x64/ARM64 Cosmos Gen 3 kernel project targeting .NET 10. It currently includes a command shell, a FAT-backed VFS, Windows-style drive-letter path resolution, a DiskManager CLI, and an experimental graphical desktop.

## Requirements

- Windows with PowerShell
- .NET SDK 10 or later
- Cosmos Gen 3 toolchain and QEMU

Install Cosmos using the current Windows installer from the [Cosmos releases](https://github.com/CosmosOS/Cosmos/releases), open a new terminal, and verify the toolchain:

```powershell
cosmos check
```

## Build and Run

Build the default x64 kernel:

```powershell
.\build.ps1
```

This runs `cosmos build`. To build ARM64 directly:

```powershell
dotnet build .\yggdrasilKernel.csproj -c Debug -r linux-arm64 --verbosity minimal
```

Run the kernel in QEMU:

```powershell
.\run.ps1
```

`run.ps1` builds the kernel and starts QEMU with `Fat.C.img` attached as a disk. The kernel can write partition tables, metadata, and filesystems to attached images. Use disposable copies for development and never point the DiskManager at a disk containing data you need.

## Shell

The shell accepts simple commands and Windows, Unix-style, and NT-style paths. Drive names are case-insensitive; one-letter names are assigned first, followed by two-letter names such as `AA:`. The supported name length is controlled by `YVolumeManager.MaxDriveNameLength`. A leading `/` is rooted at the current drive; use a colon-qualified path such as `AA:/Projects` to select another drive. Examples:

```text
C:\> help
C:\> dir
C:\> cd D:\Projects
D:\Projects> cat notes.txt
D:\Projects> diskmanager
AA:\> dir
AA:\> cd /Projects
```

The line editor supports command history, Tab completion, quoted paths, PageUp/PageDown and mouse-wheel scrollback, and numpad navigation. Up/Down navigate command history; with NumLock on, the numpad enters numbers.

Available commands include `ls`/`dir`, `cd`, `pwd`, `cat`/`type`, `tree`, `mkdir`, `rm`/`del`, `rmdir`, `write`, `drives`, `space [drive:]`, `diskmanager`, `clear`, `halt`, and `yindows`.

`drives` and DiskManager volume listings show total capacity without scanning FAT allocation tables. Use `space` or `space D:` to request exact used/free space; the scan runs in the background and reports results when complete.

## DiskManager

Enter `diskmanager` in the shell. Use `help` in DiskManager for the full command list. Common commands:

```text
DiskManager> list
DiskManager> info 0
DiskManager> format
DiskManager> format 0 0
DiskManager> assign 0 0 AA
DiskManager> reset 0
DiskManager> wipe 1
DiskManager> rescan
DiskManager> home
```

Bare `format` opens an operation chooser for formatting a volume, initializing a blank disk, resetting a disk layout, or erasing a disk. `format <drive:> [filesystem]` and `format <disk#> <part#> [filesystem]` format only the selected partition; they do not move or recreate partition entries. The filesystem list currently contains FAT only. Recognized EFI, boot, recovery, reserved, and Yggdrasil metadata partitions require a typed confirmation naming the disk and partition, but can still be formatted.

`init <disk#>` initializes a blank disk. `reset <disk#>` replaces the existing partition layout with the default Yggdrasil layout without overwriting all data sectors. `wipe <disk#>` overwrites every addressable sector and leaves the disk blank; this software operation is not a hardware secure erase. Reset and wipe require typing the operation, disk index, and disk name to confirm. Disks under 1 GiB are treated as removable and do not receive a metadata partition.

Persistent drive assignments use the v3 raw metadata partition when available and `.metaDisk` files on eligible data volumes. FAT volumes larger than 500 MiB receive the file; smaller volumes on disks treated as removable also receive it. The raw registry format is v3-only; recreate older raw metadata partitions with DiskManager if upgrading from a development build that used an earlier format. Sizes are displayed in binary units such as `MiB` and `GiB`.

## Project Layout

- `src/Kernel.cs`: Cosmos kernel lifecycle and top-level mode dispatch
- `src/PreOs/Shell/`: pre-Yindows shell, line editor, scrollback, DiskManager CLI, and disk-space query
- `src/Kernel/Storage/`: disk initialization, metadata layout, and FAT formatting
- `src/Kernel/Vfs/`: volume manager, persistent drive assignments, and path/file wrappers
- `src/PreOs/Desktop/`: Yindows window manager, mouse handling, and generated cursor asset
- `Bootloader/`: Limine boot configuration
- `tools/`: build-time helper scripts

The pre-Yindows shell is a kernel-mode maintenance environment, not firmware and not a user-mode process. The shell, DiskManager, storage layer, and current Yindows desktop all run in the same Cosmos kernel with the same privilege level. Cosmos provides kernel-thread scheduling, but this project does not yet implement isolated user processes or a syscall boundary. Source folders are organized by responsibility; namespaces are declared in the C# files.

## Cosmos Gen 3 References

- [Installation](https://valentinbreiz.github.io/nativeaot-patcher/articles/user/install.html)
- [Kernel startup](https://valentinbreiz.github.io/nativeaot-patcher/articles/user/startup.html)
- [Filesystem](https://valentinbreiz.github.io/nativeaot-patcher/articles/user/filesystem.html)
- [Keyboard](https://valentinbreiz.github.io/nativeaot-patcher/articles/user/keyboard.html)
- [Mouse](https://valentinbreiz.github.io/nativeaot-patcher/articles/user/mouse.html)
- [Graphics](https://valentinbreiz.github.io/nativeaot-patcher/articles/user/graphics.html)
- [Debugging with VS Code and QEMU](https://valentinbreiz.github.io/nativeaot-patcher/articles/user/debugging.html)
