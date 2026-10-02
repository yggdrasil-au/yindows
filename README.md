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
DiskManager> format 0 0
DiskManager> assign 0 0 AA
DiskManager> rescan
DiskManager> home
```

`format` asks for confirmation and formats a partition as FAT. On fixed disks, it also attempts to ensure a 100 MiB metadata partition is first. It uses existing leading free space, moves the first partition only when the destination is available, or rebuilds a single selected partition as part of the confirmed destructive format. If other partitions prevent a safe layout, it stops without changing the layout. Disks under 1 GiB are treated as removable and do not receive a metadata partition.

Persistent drive assignments use the v3 raw metadata partition when available and `.metaDisk` files on eligible data volumes. FAT volumes larger than 500 MiB receive the file; smaller volumes on disks treated as removable also receive it. The raw registry format is v3-only; recreate older raw metadata partitions with DiskManager if upgrading from a development build that used an earlier format. Sizes are displayed in binary units such as `MiB` and `GiB`.

## Project Layout

- `src/Kernel.cs`: kernel boot and main shell
- `src/subsystems/shell/`: shell editor, scrollback, and DiskManager CLI
- `src/DiskFormatter.cs`: disk partition initialization, metadata layout, and FAT formatting
- `src/YindowsIO.cs`: volume manager, persistent drive assignments, and path/file wrappers
- `src/WindowManager.cs`, `src/MouseHandler.cs`: graphical desktop components
- `Bootloader/`: Limine boot configuration
- `tools/`: build-time helper scripts

C# files use the `yggdrasilKernel` namespace; source-folder names are not namespace components.

## Cosmos Gen 3 References

- [Installation](https://valentinbreiz.github.io/nativeaot-patcher/articles/user/install.html)
- [Kernel startup](https://valentinbreiz.github.io/nativeaot-patcher/articles/user/startup.html)
- [Filesystem](https://valentinbreiz.github.io/nativeaot-patcher/articles/user/filesystem.html)
- [Keyboard](https://valentinbreiz.github.io/nativeaot-patcher/articles/user/keyboard.html)
- [Mouse](https://valentinbreiz.github.io/nativeaot-patcher/articles/user/mouse.html)
- [Graphics](https://valentinbreiz.github.io/nativeaot-patcher/articles/user/graphics.html)
- [Debugging with VS Code and QEMU](https://valentinbreiz.github.io/nativeaot-patcher/articles/user/debugging.html)
