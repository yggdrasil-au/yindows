# Contributing

Thanks for contributing to YggdrasilOS. Keep changes focused, follow the repository conventions, and verify kernel changes with the Cosmos toolchain.

## Before You Start

- Read [AGENTS.md](AGENTS.md) for repository build and documentation guidance.
- Follow [Style.md](Style.md) for C# formatting and error-handling conventions.
- Consult the [Cosmos Gen 3 documentation](https://valentinbreiz.github.io/nativeaot-patcher/) when using kernel, VFS, input, graphics, storage, or timer APIs.
- Use C# for current implementation work. C and C# files may live side by side when a component later needs both.

## Source Layout

Keep source beneath `src/` and organize files by subsystem where practical. The current shell files live in `src/subsystems/shell/`; disk formatting, kernel startup, and volume management live directly under `src/`.

Use the file-scoped `yggdrasilKernel` namespace for project code. Do not add `src` or folder names to namespaces. Follow the existing K&R braces, four-space indentation, and always-braced control statements described in `Style.md`.

## Build and Validate

The standard x64 build is:

```powershell
cosmos build
```

The repository scripts wrap the common workflows:

```powershell
.\build.ps1
.\run.ps1
```

`run.ps1` builds and boots QEMU with `Fat.C.img`. To check the ARM64 target:

```powershell
dotnet build .\yggdrasilKernel.csproj -c Debug -r linux-arm64 --verbosity minimal
```

There is no separate automated test project in the current workspace. For changes that affect boot or hardware-facing behavior, build the relevant architecture and perform a focused QEMU smoke test when safe to do so. Describe any checks that could not be run in the pull request.

## Disk Safety

Storage commands can modify partition tables and filesystem contents. `DiskManager`'s `format` and `init` operations require confirmation, but confirmation does not make a disk disposable. Test only with disposable QEMU disk images; do not attach real disks or images containing data you need. The standard `run.ps1` attaches `Fat.C.img`, which the guest may modify.

When changing partition handling, account for both MBR and GPT, rescan storage after changing a partition table, and preserve existing user data unless the command explicitly confirms a destructive format. Keep removable-media metadata behavior consistent with the volume-manager rules in `src/YindowsIO.cs`.

## Pull Requests

- Explain the user-visible behavior and any storage-layout changes.
- Include the build targets and runtime checks you performed.
- Call out destructive behavior, limitations, and any unverified hardware behavior.
- Keep generated build outputs, disk images, and unrelated local changes out of the change unless they are required.
