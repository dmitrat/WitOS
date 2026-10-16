# Compatibility matrix

The public matrix of Implementation Strategy §145 (plan step N4.5). This file is written by `compatibility-matrix` from
the compatibility suite's own list of checks (`dotnet DotNetCompatibility.dll --list`), the differences
`tests/DotNetCompatibility/expectations.json` records for each platform and `build/compatibility-matrix.json`; a host
test requires it to be current, so it says no more than the gates prove: the suite must end with no unexpected result on
Windows, Linux and WitOS on both ISAs (N1, N4.4), and every recorded difference has its reason in
[Compatibility-Differences.md](Compatibility-Differences.md).

## The areas of the compatibility suite

*Supported*: every check of the area passes on the platform. *Partial*: the checks named are recorded differences, each
failing explicitly with the reason recorded. The Terminal area runs where the suite's input is a terminal (the runner
types in the guest) and reports its checks not applicable elsewhere.

| Area | Checks | Windows | Linux | WitOS (x64, ARM64) |
| --- | --- | --- | --- | --- |
| Platform | 6 | Supported | Supported | Partial: KnownOperatingSystem |
| Console | 5 | Supported | Supported | Supported |
| Tasks | 7 | Supported | Supported | Supported |
| Threads | 6 | Supported | Supported | Supported |
| Timers | 6 | Supported | Supported | Supported |
| Exceptions | 7 | Supported | Supported | Supported |
| Reflection | 6 | Supported | Supported | Supported |
| FileIO | 10 | Supported | Supported | Partial: Watcher |
| AssemblyLoading | 4 | Supported | Supported | Supported |
| GC | 5 | Supported | Supported | Supported |
| Synchronization | 8 | Supported | Supported | Supported |
| Globalization | 8 | Supported | Supported | Supported |
| TimeZones | 4 | Supported | Supported | Supported |
| Terminal | 7 | Supported | Supported | Supported |

89 checks in 14 areas.

## Recorded differences

| Check | Platform | Reason |
| --- | --- | --- |
| `FileIO.Watcher` | WitOS (x64, ARM64) | System.IO.FileSystem.Watcher is not supported: the namespace service sends no change notifications (Compatibility-Differences: File change notifications). |
| `Platform.KnownOperatingSystem` | WitOS (x64, ARM64) | WitOS is neither Windows, Linux, macOS nor FreeBSD: an application that infers Windows from 'not Linux and not macOS' misbehaves there (RFC 0015 §1, ADR 0024). |

## Beyond the suite

*Supported*, *Partial* and *Absent* are facts a plan step verified in the guest on both ISAs; *Planned* names the
milestone or phase that brings the capability. An absent capability fails explicitly, never silently.

| Capability | Status | Step | Fact |
| --- | --- | --- | --- |
| CoreCLR with the JIT, `Reflection.Emit`, `AssemblyLoadContext` | Supported | R3, R7 | Every method is compiled into the double mapping of a shared memory file; upstream's PAL suite and slices of its runtime and library tests pass in the guest. |
| Shared framework through `dotnet App.dll` | Supported | R5, R6 | The install under `/dotnet` as the shared-framework packaging lays it out; an application the stock SDK publishes without a RID runs unchanged. |
| .NET 8 and .NET 10 side by side | Supported | N3 | `release/8.0` with its own patch set beside `release/10.0`; `rollForward` and `--roll-forward` select the framework. |
| NativeAOT | Supported | R2 | The system's own tool for the services of the boot package, not the application contract. |
| Garbage collector | Partial | R2, phase P | The workstation GC with its heap bounded to 75% of what a process may own (`DOTNET_GCTotalPhysicalMemory`); the server GC waits for phase P, since one processor schedules threads. |
| Globalization with ICU | Supported | R4.3 | ICU4C 78.3 built from the pinned source; invariant mode only where an application asks for it. |
| Time zones | Supported | N4.1 | `TimeZoneInfo` from pinned tzdata under `/usr/share/zoneinfo`; `/etc/localtime` is UTC. |
| Files and directories on volumes | Supported | D5, N4.1 | ext4 (written through the journal), FAT and `/tmp` through the namespace service; modes and times as the volume keeps them; the boot package read-only. |
| Memory-mapped files | Partial | D5.2, R4.2 | A private mapping is a copy, a shared one shows the service's pages; `MemoryMappedFile` works, the copy's cost is the file's size. |
| File change notifications (`FileSystemWatcher`) | Absent | — | `PlatformNotSupportedException`: the namespace service sends none. |
| Processes (`Process.Start`, `WaitForExit`, redirected streams, `Kill`) | Partial | N4.2 | `posix_spawn` over the process manager with pipes between processes; no process enumeration, no other process's name or times, no signal but `SIGKILL`, one user, no process groups. |
| Console and terminal | Supported | N4.3 | The terminal service over the virtio console with Linux's line discipline: `ReadLine`, `ReadKey`, `KeyAvailable`, `CancelKeyPress`, the window size; without a terminal the standard streams are the kernel log. |
| PowerShell 7 (upstream, `dotnet pwsh.dll`, PSReadLine) | Planned | C1 | RFC 0020 §56 T2: the shell on the serial terminal of N4.3, its native library built for the witos triples, terminfo from a pinned ncurses. |
| Text console on a display (framebuffer, keyboard) | Planned | C2 | The firmware's framebuffer published as a device region, a virtio keyboard, a NativeAOT display service behind the terminal service; the serial console stays. |
| Named synchronization objects (`Mutex` with a name) | Supported | N4.1 | Under `/tmp/.dotnet` in the namespace service's `/tmp`. |
| Cryptography | Partial | R4.1 | System.Native's random bytes, the managed hashes, HMAC and the KDFs, as in the browser flavor; `PlatformNotSupportedException` for the rest; no OpenSSL. |
| Networking (`Socket`, `HttpClient`) | Planned | M7 | Absent: `System.Net.Sockets` and `System.Net.Security.Native` fail explicitly until the network service of M7. |
| ASP.NET Core | Planned | M7 | Needs networking. |
| Diagnostics (`createdump`, EventPipe, `dotnet-dump`) | Absent | — | Decided with the debugger story. |
| Compression (`System.IO.Compression`) | Supported | R2.3a | zlib-ng and brotli in-tree. |
| Server GC and several scheduling processors | Planned | phase P | The kernel starts every processor at boot and schedules threads on one. |
| Avalonia and other GUI frameworks | Planned | M9 | Needs the presentation layer of RFC 0009. |

## Hardware

The hardware matrix of Strategy §146.

| Platform | Status | Note |
| --- | --- | --- |
| QEMU x64 (q35) | Primary | Every gate runs on it. |
| QEMU ARM64 (virt) | Primary | Every gate runs on it; a step closes only when both ISAs pass. |
| QEMU RISC-V (virt) | Future | Phase V, begun with the measurement V0 (2026-10-16): the pinned QEMU carries qemu-system-riscv64 and EDK II's RISC-V firmware; the pinned lld-link links no RISC-V PE/COFF, so the loader's image format is V0's first decision. |
| Physical reference machine | Future | After phase V; firmware memory reclamation and the hardware matrix of Strategy §146. |
| Generic PC | Future | After the reference machine. |
