using System.Reflection.PortableExecutable;
using WitOS.Dev.Host;

namespace WitOS.Dev.Kernel;

/// <summary>
/// What differs between the kernel's target architectures in the tools: the kernel's triple, the MSVC tools of the
/// frozen line's fixtures, EFI file, PE machine and the QEMU board that boots it.
/// </summary>
/// <param name="Name">Architecture name of build/kernel-&lt;name&gt;.json.</param>
/// <param name="MsvcTarget">Directory of the cross tools under MSVC bin/Hostx64; also the link /machine value.</param>
/// <param name="MsvcComponent">Visual Studio component that installs those tools.</param>
/// <param name="Assembler">Assembler executable in that directory.</param>
/// <param name="Machine">PE machine of the linked EFI image.</param>
/// <param name="EfiName">Removable-media boot file name under EFI/BOOT.</param>
/// <param name="Qemu">System emulator of the pinned QEMU, without an extension.</param>
/// <param name="QemuMachine">QEMU machine of the board.</param>
/// <param name="ExitDevice">QEMU arguments of the board's test exit.</param>
/// <param name="LinkOptions">Architecture-specific link options; ARM64 images are always relocatable.</param>
/// <param name="Firmware">EDK II code image under the QEMU share directory.</param>
/// <param name="FirmwareVariables">EDK II variables template under the QEMU share directory.</param>
/// <param name="DefaultCpu">QEMU CPU model of the base profile.</param>
/// <param name="Triple">clang target triple of layer 2 (plan step T1): ELF, the SysV calling convention, the Itanium C++ ABI.</param>
/// <param name="ElfMachine">ELF e_machine of that triple.</param>
/// <param name="ClangOptions">Architecture-specific clang options of layer 2 code.</param>
/// <param name="KernelTriple">clang target triple of the kernel and its EFI loader (plan step T3): PE/COFF and the
/// Microsoft calling convention, linked by lld-link.</param>
internal sealed record KernelArchitecture(string Name, string MsvcTarget, string MsvcComponent, string Assembler,
    Machine Machine, string EfiName, string Qemu, string QemuMachine, string[] ExitDevice, string[] LinkOptions, string Firmware,
    string FirmwareVariables, string DefaultCpu, string Triple, ushort ElfMachine, string[] ClangOptions, string KernelTriple)
{
    #region Fields

    /// <summary>
    /// The x64 kernel on the q35 board; the exit is QEMU's isa-debug-exit port.
    /// </summary>
    public static readonly KernelArchitecture X64 = new("x64", "x64", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
        "ml64.exe", Machine.Amd64, "BOOTX64.EFI", "qemu-system-x86_64", "q35,hpet=on",
        ["-device", "isa-debug-exit,iobase=0xf4,iosize=0x04"], ["/dynamicbase:no"], "edk2-x86_64-code.fd", "edk2-i386-vars.fd",
        "qemu64", "x86_64-unknown-linux-musl", 62, [], "x86_64-unknown-windows");

    /// <summary>
    /// The ARM64 kernel on the QEMU virt board with GICv3; the exit is Arm semihosting. The board runs without ACPI
    /// so that the firmware publishes the device tree the platform enumerates its devices from (plan step K3.1).
    /// </summary>
    public static readonly KernelArchitecture Arm64 = new("arm64", "arm64",
        "Microsoft.VisualStudio.Component.VC.Tools.ARM64", "armasm64.exe", Machine.Arm64, "BOOTAA64.EFI",
        "qemu-system-aarch64", "virt,gic-version=3,acpi=off", ["-semihosting-config", "enable=on,target=native"], [],
        "edk2-aarch64-code.fd", "edk2-arm-vars.fd", "cortex-a72", "aarch64-unknown-linux-musl", 183,
        ["-ffixed-x18"], // x18 is the kernel's compiler TLS register, set on every return to EL0
        "aarch64-unknown-windows");

    #endregion

    #region Functions

    /// <summary>
    /// Finds an architecture by name.
    /// </summary>
    /// <param name="name">x64 or arm64.</param>
    /// <returns>The architecture.</returns>
    /// <exception cref="ArgumentException">The name is not a kernel architecture.</exception>
    public static KernelArchitecture Find(string name) => name switch
    {
        "x64" => X64,
        "arm64" => Arm64,
        _ => throw new ArgumentException($"Unknown kernel architecture '{name}'; expected x64 or arm64.", nameof(name))
    };

    /// <summary>
    /// The first log line of the root task fixture (tests/User/root.c): the pinned compiler and the ISA of the triple.
    /// </summary>
    public string RootStartedLine => $"[ROOT] started by clang {Toolchain.LLVM_VERSION} for {Triple[..Triple.IndexOf('-')]}";

    /// <summary>
    /// The last log line of the first libc program (tests/User/libc_hello.c) when every check passed.
    /// </summary>
    public string LibcPassedLine => $"[LIBC] musl {Substrate.MuslLibc.VERSION} on {Triple[..Triple.IndexOf('-')]}: ";

    /// <summary>
    /// The summary line of the libc-test runner (tests/User/libc_test_runner.c) when every run of every selected test
    /// passed (S1.3, S7.1).
    /// </summary>
    public string LibcTestPassedLine => $"[LIBC-TEST] musl {Substrate.MuslLibc.VERSION} on {Triple[..Triple.IndexOf('-')]}: all ";

    /// <summary>
    /// The line the C++ program of the cxx scenario prints when its checks ran (S4).
    /// </summary>
    public string CxxPassedLine => $"[CXX] C++ runtime on {Triple[..Triple.IndexOf('-')]}: ";

    /// <summary>
    /// The line the root task of the spawn scenario prints when every started program ended as it should (S5.2, S5.3).
    /// </summary>
    public string SpawnPassedLine => $"[SPAWN] ELF loader on {Triple[..Triple.IndexOf('-')]}: ";

    /// <summary>
    /// The line the dynamic program of the spawn scenario prints when its checks passed: libraries, dlopen and TLS
    /// modules under musl's dynamic linker (S5.3).
    /// </summary>
    public string DynamicPassedLine => $"[DYNAMIC] dynamic program on {Triple[..Triple.IndexOf('-')]}: ";

    /// <summary>
    /// The line the acceptance program of phase S prints when its checks passed: a dynamic C++ program with threads,
    /// exceptions across modules and a signal over the shared C++ runtime (S5.4).
    /// </summary>
    public string PhaseSPassedLine => $"[PHASE-S] dynamic C++ program on {Triple[..Triple.IndexOf('-')]}: ";

    /// <summary>
    /// The line /bin/init of the process scenario prints when its children started and ended as they should (S6.1).
    /// </summary>
    public string InitPassedLine => $"[INIT] process manager on {Triple[..Triple.IndexOf('-')]}: ";

    /// <summary>
    /// The line /bin/init of the sysroot scenario prints when its checks ran: a C++ program clang's driver built against
    /// the system layer's sysroot (R1.2a).
    /// </summary>
    public string SysrootPassedLine => $"[SYSROOT] clang driver program on {Triple[..Triple.IndexOf('-')]}: ";

    /// <summary>
    /// The last line of NativeAOT's M3 acceptance (tests/Runtime.Witos/Acceptance) when every run passed (R2.2).
    /// </summary>
    public string AcceptancePassedLine =>
        $"[M3] NativeAOT on {Triple[..Triple.IndexOf('-')]}: {Runtime.RuntimeWitos.ACCEPTANCE_RUNS} runs passed, 0 failed";

    /// <summary>
    /// The line the system layer's root task prints when /bin/init ended with zero (S6.1).
    /// </summary>
    public string RootTaskPassedLine => "[ROOT-TASK] /bin/init exited with 0";

    /// <summary>
    /// The first line of the program the spawn scenario starts: a static position-independent program relocated at its
    /// start, with its arguments, in a process of its own (S5.2).
    /// </summary>
    public string SpawnChildLine => $"[CHILD] started on {Triple[..Triple.IndexOf('-')]} with 3 arguments";

    /// <summary>
    /// Finds the MSVC tools that build this architecture.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Tool directory with cl, link and the assembler.</returns>
    public Task<string> FindMsvcAsync(string root) => Toolchain.FindMsvcAsync(root, MsvcTarget, MsvcComponent, Assembler);

    /// <summary>
    /// Path of the QEMU system emulator.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Executable path.</returns>
    public string QemuPath(string root) => Toolchain.QemuExecutable(root, Qemu);

    /// <summary>
    /// Path of the EDK II code image.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Firmware path.</returns>
    public string FirmwarePath(string root) => Path.Combine(Toolchain.QemuShareDirectory(root), Firmware);

    /// <summary>
    /// Path of the EDK II variables template.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Template path.</returns>
    public string FirmwareVariablesPath(string root) => Path.Combine(Toolchain.QemuShareDirectory(root), FirmwareVariables);

    /// <summary>
    /// Throws unless the emulator and the firmware of this architecture are installed.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <exception cref="InvalidOperationException">QEMU or EDK II is missing.</exception>
    public void RequireQemu(string root)
    {
        if (!File.Exists(QemuPath(root)) || !File.Exists(FirmwarePath(root)) || !File.Exists(FirmwareVariablesPath(root)))
            throw new InvalidOperationException($"QEMU/EDK II for {Name} are missing. Run: dotnet run --project tools/WitOS.Dev -- setup");
    }

    #endregion
}
