using System;
using CKSys = Cosmos.Kernel.System;
using Cosmos.Kernel.System.Vfs;
using Cosmos.Kernel.System.Filesystems.Fat;

namespace yggdrasilKernel;

public class Kernel : CKSys.Kernel {
    private bool _isGuiMode = false;

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
            Console.WriteLine("The name \"fat\" is already registered.");
            return;
        }
        YVolumeManager.InitializeStorage();
    }

    protected override void BeforeRun() {
        Console.WriteLine("Cosmos booted successfully!");
        Console.WriteLine("Type a command to get it executed.");
    }

    protected override void Run() {
        if (!_isGuiMode) {
            Console.Write($"{YDirectory.GetCurrentDirectory()}> ");
            var input = Console.ReadLine();

            if (string.IsNullOrEmpty(input)) {
                return;
            }

            switch (input.ToLower()) {
                case "help": {
                    Console.WriteLine("Available commands: help, clear, halt, testwrite, yindows");
                    break;
                }
                case "clear": {
                    Console.Clear();
                    break;
                }
                case "halt": {
                    Console.WriteLine("Halting system...");
                    Stop();
                    break;
                }
                case "testwrite": {
                    if (YVolumeManager.TryGetVolumePath('C', out _)) {
                        YDirectory.CreateDirectory(@"C:\System\Logs/DeepFolder\Sub");
                        YFile.WriteAllText(@"C:\welcome.txt", "Welcome to Yindows on C!");
                        Console.WriteLine("Windows: " + YFile.ReadAllText(@"C:\welcome.txt"));
                        Console.WriteLine("Unix: " + YFile.ReadAllText("/c/welcome.txt"));
                        Console.WriteLine("Mixed: " + YFile.ReadAllText(@"C:/System/Logs\..\..\welcome.txt"));
                        Console.WriteLine("NT: " + YFile.ReadAllText("/Device/HarddiskVolume1/welcome.txt"));
                    }
                    break;
                }
                case "yindows": {
                    Console.WriteLine("Starting Yindows...");
                    _isGuiMode = true;
                    WindowManager.BootIntoYindows();
                    break;
                }
                default: {
                    Console.WriteLine($"\"{input}\" is not a command");
                    break;
                }
            }
        } else {
            WindowManager.UpdateGui();
        }
    }
}