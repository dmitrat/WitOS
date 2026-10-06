# ADR 0024: Three layers, a nano-kernel of mechanisms and the Unix-form .NET target

**Status:** Accepted 2026-10-06. Supersedes the direction of ADR 0006 for user space and the P6.4 decisions of
2026-10-04 to 2026-10-06 (Windows-form runtime, COM interop, Win32 binding surface).
**Date:** 2026-10-06.
**Scope:** The architecture WitOS is built toward, the boundary of each layer, the form of the .NET runtime that runs
on it and the fate of the code that does not fit. The plan that applies this decision is the root `PLAN.md`.

## Goals this decision serves

1. Three layers: a low-level, hardware-dependent kernel whose hardware part may live in firmware or a dedicated
   chip; a common system layer between that kernel and .NET, compiled for each platform; unchanged upstream .NET.
2. Pure .NET components run without changes or recompilation, with NuGet dependencies, built in Visual Studio on
   Windows and run on WitOS.
3. A new platform (for example RISC-V) costs a new low-level kernel and a recompilation of the system layer.
4. A new .NET release costs little.
5. Several .NET versions can be installed and used at once.

## Context

By 2026-10-06 the implementation had drifted from the vision documents in two places, each step reasonable alone.

**The kernel had absorbed policy.** Of the 9,241 lines of the common kernel, about 3,900 implement what RFC 0001 §8
places above the kernel: a PE loader with import and export resolution, the DLL lifecycle and Windows' TLS callback
order (about 2,900 lines), a file namespace with paths and directories over the boot package (about 640), the
environment and current directory "as kernel32 keeps them" (264), APCs and thread names. Of 70 system calls, about
50 are mechanisms; `FILE`, `STORAGE_QUERY`, `LIBRARY` (14 operations), `PROCESS_STATE` and `THREAD_NAME` are not.
The kernel also lacked families the documents require: channels (RFC 0006 §11), delegation of MMIO, interrupts and
DMA to user space (RFC 0007 §10), more than one process, SMP, a UTC clock.

**User space had become a Windows personality.** The runtime work since ADR 0006 kept Windows x64 code generation,
PE/COFF and the MSVC toolchain, and so the unchanged upstream binaries it hosted (`hostfxr`, `hostpolicy`,
`coreclr.dll`) were Windows builds. Supplying their imports built a Win32 facade: an own vcruntime, UCRT subset and
STL port, then 217 remaining kernel32/ole32/oleaut32/advapi32 functions for CoreCLR, with about 600 Win32 P/Invokes
of the Windows framework behind them (advapi32, bcrypt, crypt32, ws2_32). Applications would have seen Windows:
`OperatingSystem.IsWindows()` true, `\` paths, a registry. RFC 0015 §7 had excluded exactly this ("not a Windows
compatibility personality"), and the Developer Experience Manifesto §12 places Win32 and COM outside the portable
boundary.

The root cause was ordering: the milestone plan puts devices (M4) and storage (M5) before standard .NET (M6). Work
went to M6 first, so the services that should hold files and process state did not exist, and the kernel took them.

Two facts about upstream .NET decide the runtime's form. CoreCLR has exactly two forms: `TARGET_WINDOWS`, which calls
Win32 directly, and `TARGET_UNIX`, which calls its own PAL (`src/coreclr/pal`, 52k lines of C++, shared by every
other platform: Linux, macOS, FreeBSD, illumos, Haiku, Android, iOS). Every port to a new operating system is a
variant of the Unix form; FreeBSD touches 31 coreclr and 62 native-library files in 10.0.8, Haiku 18 and 11, and 101
configure-time checks absorb most variance. And the Unix form is the only one for RISC-V: Windows builds exist for
x64, arm64 and x86 alone, while the Unix form covers x64, arm64, arm, riscv64, loongarch64, ppc64le and s390x. Goal 3
is unreachable through the Windows form.

The framework's view of the operating system differs in the same way: the Unix form reaches the OS through
`libSystem.Native` (253 entry points in 10.0.8) plus optional ICU and OpenSSL; the Windows form through about 600
P/Invokes across security, networking and cryptography subsystems.

## Decision

1. **Three layers with two interfaces.** Layer 1 is a nano-kernel of mechanisms: address spaces, threads and
   contexts, events and time, fault delivery, channels, capabilities, delegation of MMIO, interrupts and DMA. Its
   hardware part, the architecture and platform code behind `wit_arch_*` and `wit_platform_*`, is the Universal
   Hardware Interface of RFC 0007 and the part that may move to firmware or a chip; the kernel's policy code stays
   software, compiled for each architecture. Layer 2 is the system layer in user space: the root task and loader,
   process state, the runtime substrate (libc, threads, unwinding, `System.Native`), the service manager. Layer 3 is
   upstream .NET, unchanged, with WitOS's own services written in .NET (NativeAOT) above it. **ABI-1** between layers
   1 and 2 is narrow and versioned; **ABI-2** between layers 2 and 3 is the C library and WitOS's capability APIs, and
   must stay backward compatible for years, because goal 5 installs runtimes built against it side by side.
2. **The kernel keeps mechanisms only.** The PE loader, DLL lifecycle, TLS callback order, file namespace, boot
   package parsing, environment and current directory leave the kernel for layer 2. The kernel starts one root task
   from a trivial flat image and hands it the boot package as a read-only memory capability. Channels, device
   delegation, multiple processes, a UTC clock and SMP groundwork join the kernel. Image format becomes irrelevant to
   the kernel, which is what makes the next point cheap at that level.
3. **The .NET runtime is the Unix form, `TargetOS=witos`, RID `witos-x64`.** CoreCLR, the JIT, CoreLib, the
   framework's native libraries, the hosts and NativeAOT are built from pinned upstream sources with a recorded patch
   set of the size of the Haiku and FreeBSD ports, aimed at eventual upstream contribution. Applications see
   `OperatingSystem.IsOSPlatform("WitOS")`; neither `IsWindows()` nor `IsLinux()` is true.
4. **The POSIX-shaped substrate is private to layer 2.** It exists because CoreCLR's PAL and `System.Native` need
   it, exactly as on Linux, and applications never see it: they see .NET and WitOS capability APIs. The substrate is
   taken from existing open code, not written: a libc (musl, pending confirmation; mlibc is the alternative), LLVM's
   libunwind, libc++abi and libc++, an ELF loader. Minimal POSIX signals are implemented in the libc over the kernel's
   fault delivery and APCs so that the PAL patch stays small; the PAL is not rewritten without signals.
5. **The toolchain is clang and lld for ELF, SysV x64 and the Itanium C++ ABI** in layers 2 and 3. The kernel is
   expected to follow, so that one toolchain builds everything and the OS builds on a Linux host, where
   dotnet/runtime builds natively. This revises the MSVC ARM64 decision of 2026-10-02.
6. **Files and devices follow the storage architecture document.** Drivers, block composition, filesystems and the
   namespace are managed services above the kernel (M4, M5 of the milestone plan); `System.Native`'s file operations
   are their clients over channels. The boot package is an immutable image, not a filesystem.
7. **The milestone order is restored:** devices and storage (M4, M5) before standard .NET (M6).

## Consequences

**Kept:** the nano-kernel and its UHI for x64 and ARM64 (the ARM64 port cost about 2,300 lines of architecture and
platform code and reused the common kernel, which is the evidence for goal 3 at the kernel level), the boot path,
the tools, CI and the deterministic virtual-time gates, and the methods: canonical-byte pins, patches through
`UpstreamPatches`, differential testing against reference implementations, strict links with recorded inventories.

**Kept as knowledge, rewritten as glue:** the GC and PAL adapters of NativeAOT, written against the Windows form
of its PAL and now needed against the Unix form; the unwind dispatcher, whose tables become DWARF.

**Discarded:** the PE and DLL machinery in the kernel, the own C++ exception runtime, UCRT subset and STL port, the
Windows builds of the hosts and `coreclr.dll` for the guest, the Win32 adapters: about 11,000 lines and the whole of
P6.4's user-space line. That code stays in the tree until NativeAOT on the new substrate reaches the M3 acceptance
again (plan step R2), so the kernel never loses a running workload; then it is removed.

**Costs accepted:** a patch set outside upstream until a `witos` target is contributed; the honesty tax of
applications that assume "not Linux, not macOS, therefore Windows"; the maturity risk of a libc over a new kernel;
and the time of a reset of P6.2 in part and P6.4 in whole. Time is not the constraint of this program.

## Alternatives rejected

- **The Windows form as a permanent target** (the line of 2026-10-04 to 2026-10-06): applications see Windows, the
  Win32 surface is unbounded and grows with every release, and RISC-V is impossible.
- **The Windows form as a stepping stone to a first JIT run:** every artifact of it would be discarded after; the
  kernel already has real workloads for validation.
- **Binary compatibility with `linux-musl-x64`** (official Microsoft binaries unchanged): strongest for goals 4 and
  5, but applications see Linux, and the Linux system call ABI with `/proc`, cgroups, `clone`, `epoll` and signals
  becomes WitOS's internal contract: the same substitution of identity as Windows.
- **NativeAOT only, no CoreCLR:** no JIT, reflection emit or dynamic loading; contradicts goal 2.
- **A bespoke CoreLib or runtime fork:** contradicts RFC 0001 §6 and goal 4.
- **A PAL variant without POSIX signals:** no port has done it; the patch set would grow where it should shrink.

## Open decisions, with the defaults the plan assumes

| Question | Default | Reconsider when |
| --- | --- | --- |
| What may move to a chip | the UHI (hardware personality) alone; the kernel's policy stays software | a hardware partner asks for more |
| libc | musl, proven with .NET on Alpine; its Linux-syscall bottom becomes `sysdeps` over ABI-1 | mlibc proves itself with .NET, or musl's Linux shape leaks upward |
| Kernel toolchain | clang and lld, with Windows parity kept in CI | the kernel's MSVC-only constructs resist |
| Signals | minimal POSIX signals in the libc over fault delivery and APCs | the PAL's signal use turns out larger than its platform files suggest |

**Applied by RFC 0011 v3 (plan step A2, 2026-10-06).** The kernel ABI inventory is fixed: of the 70 current calls 37
stay, 13 merge into them, 6 are removed, 13 move to layer 2 and one splits; with the six families the documents
required and the kernel lacked, ABI-1 counts 54 calls. APCs stay as thread activations delivered with the interrupted
context, which is the mechanism the signals default above rests on; thread names move to the libc; the environment
and current directory live in the libc as on POSIX, with the process manager passing the initial values. The four
open decisions in the table are unchanged.

## Application

The root `PLAN.md` carries the program in phases A (architecture), K (kernel), T (toolchain), S (substrate), R
(runtime), D (devices and storage), N (standard .NET), V (a third ISA), P (SMP), with steps such as K2.1. Slices,
commits and pull requests are named by step. `@Docs/Implementation/Plan-Archive-2026-10-06.md` keeps the plan this
decision replaced, and `P6.4-Plan.md` the frozen host line.
