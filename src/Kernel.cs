using System;
using CKSys = Cosmos.Kernel.System;
using Cosmos.Kernel.System.Vfs;
using Cosmos.Kernel.System.Filesystems.Fat;
using yggdrasilKernel.PreOs.Desktop;
using yggdrasilKernel.Storage;
using yggdrasilKernel.Vfs;
using yggdrasilKernel.PreOs.Shell;

namespace yggdrasilKernel;

public class Kernel : CKSys.Kernel {
    private readonly PreOsShell _preOsShell = new();
    private KernelMode _mode;

    private enum KernelMode {
        PreOsShell,
        Yindows,
    }

    /// <summary>
    /// Starts the kernel and performs initial setup.
    /// base.Start() calls the OnBoot method.
    /// </summary>
    public override void Start() {
        base.Start();
        //
    }

    /// <summary>
    /// Performs the boot sequence for the kernel, including filesystem registration, partition initialization, and mounting.
    /// base.OnBoot() calls the Run method.
    /// </summary>
    protected override void OnBoot() {
        base.OnBoot();

        FatFilesystemType fat = new();
        if (!VfsManager.RegisterFilesystem("fat", fat)) {
            ShellOutput.WriteLine("The name \"fat\" is already registered.");
            return;
        }
        YVolumeManager.InitializeStorage();
    }

    protected override void BeforeRun() {
        ShellOutput.WriteLine("Cosmos booted successfully!");
        ShellOutput.WriteLine("Type 'help' for commands, or 'diskmanager' to manage disks.");
    }

    protected override void Run() {
        switch (_mode) {
            case KernelMode.PreOsShell:
                PreOsShellRequest request = _preOsShell.Run(Stopped);
                if (request == PreOsShellRequest.LaunchYindows) {
                    ShellOutput.WriteLine("Starting Yindows...");
                    WindowManager.BootIntoYindows();
                    _mode = KernelMode.Yindows;
                } else if (request == PreOsShellRequest.StopKernel) {
                    Stop();
                }
                break;
            case KernelMode.Yindows:
                WindowManager.UpdateGui();
                break;
        }
    }
}