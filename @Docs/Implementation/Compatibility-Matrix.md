# Compatibility matrix

The public matrix of Implementation Strategy §145 (plan step N4.5). This file is written by `compatibility-matrix` from
the compatibility suite's own list of checks (`dotnet DotNetCompatibility.dll --list`), the differences
`tests/DotNetCompatibility/expectations.json` records for each platform and `build/compatibility-matrix.json`; a host
test requires it to be current, so it says no more than the gates prove: the suite must end with no unexpected result on
Windows, Linux and WitOS on its three ISAs (N1, N4.4, V3), and every recorded difference has its reason in
[Compatibility-Differences.md](Compatibility-Differences.md).

## The areas of the compatibility suite

*Supported*: every check of the area passes on the platform. *Partial*: the checks named are recorded differences, each
failing explicitly with the reason recorded. The Terminal area runs where the suite's input is a terminal (the runner
types in the guest) and reports its checks not applicable elsewhere.

| Area | Checks | Windows | Linux | WitOS (x64, ARM64, riscv64) |
| --- | --- | --- | --- | --- |
| Platform | 6 | Supported | Supported | Partial: KnownOperatingSystem |
| Console | 5 | Supported | Supported | Supported |
| Tasks | 7 | Supported | Supported | Supported |
| Threads | 6 | Supported | Supported | Supported |
| Timers | 6 | Supported | Supported | Supported |
| Exceptions | 7 | Supported | Supported | Supported |
| Reflection | 6 | Supported | Supported | Supported |
| FileIO | 11 | Supported | Supported | Partial: Watcher |
| AssemblyLoading | 4 | Supported | Supported | Supported |
| GC | 5 | Supported | Supported | Supported |
| Synchronization | 8 | Supported | Supported | Supported |
| Globalization | 8 | Supported | Supported | Supported |
| TimeZones | 4 | Supported | Supported | Supported |
| Terminal | 7 | Supported | Supported | Supported |
| Network | 19 | Supported | Supported | Supported |
| Cryptography | 11 | Supported | Supported | Supported |
| Ipc | 5 | Supported | Supported | Supported |

125 checks in 17 areas.

## Recorded differences

| Check | Platform | Reason |
| --- | --- | --- |
| `FileIO.Watcher` | WitOS (x64, ARM64, riscv64) | System.IO.FileSystem.Watcher is not supported: the namespace service sends no change notifications (Compatibility-Differences: File change notifications). |
| `Platform.KnownOperatingSystem` | WitOS (x64, ARM64, riscv64) | WitOS is neither Windows, Linux, macOS nor FreeBSD: an application that infers Windows from 'not Linux and not macOS' misbehaves there (RFC 0015 §1, ADR 0024). |

## Beyond the suite

*Supported*, *Partial* and *Absent* are facts a plan step verified in the guest on the three ISAs unless the fact names
fewer; *Planned* names the milestone or phase that brings the capability. An absent capability fails explicitly, never
silently.

| Capability | Status | Step | Fact |
| --- | --- | --- | --- |
| CoreCLR with the JIT, `Reflection.Emit`, `AssemblyLoadContext` | Supported | R3, R7 | Every method is compiled into the double mapping of a shared memory file; upstream's PAL suite and slices of its runtime and library tests pass in the guest. |
| Shared framework through `dotnet App.dll` | Supported | R5, R6 | The install under `/dotnet` as the shared-framework packaging lays it out; an application the stock SDK publishes without a RID runs unchanged. |
| .NET 8 and .NET 10 side by side | Supported | N3 | `release/8.0` with its own patch set beside `release/10.0`; `rollForward` and `--roll-forward` select the framework. On x64 and ARM64: `release/8.0` is not built for riscv64, whose upstream port came after it (V3). .NET 8 keeps the browser's cryptography; its support ends on 10 November 2026, and with the pin's move to .NET 11 the versions side by side become 10 and 11. |
| NativeAOT | Supported | R2 | The system's own tool for the services of the boot package, not the application contract. |
| Garbage collector | Supported | R2, P2 | The workstation and the server collector, the server one with a heap per processor (P2), each with its heap bounded to 75% of what a process may own (`DOTNET_GCTotalPhysicalMemory`). |
| Globalization with ICU | Supported | R4.3 | ICU4C 78.3 built from the pinned source; invariant mode only where an application asks for it. |
| Time zones | Supported | N4.1 | `TimeZoneInfo` from pinned tzdata under `/usr/share/zoneinfo`; `/etc/localtime` is UTC. |
| Files and directories on volumes | Supported | D5, N4.1 | ext4 (written through the journal), FAT and `/tmp` through the namespace service; modes and times as the volume keeps them; the boot package read-only. |
| Memory-mapped files | Partial | D5.2, R4.2 | A private mapping is a copy, a shared one shows the service's pages; `MemoryMappedFile` works, the copy's cost is the file's size. |
| File change notifications (`FileSystemWatcher`) | Absent | — | `PlatformNotSupportedException`: the namespace service sends none. |
| Processes (`Process.Start`, `WaitForExit`, redirected streams, `Kill`) | Partial | N4.2 | `posix_spawn` over the process manager with pipes between processes; no process enumeration, no other process's name or times, no signal but `SIGKILL`, one user, no process groups. |
| Console and terminal | Supported | N4.3 | The terminal service over the virtio console with Linux's line discipline: `ReadLine`, `ReadKey`, `KeyAvailable`, `CancelKeyPress`, the window size; without a terminal the standard streams are the kernel log. |
| Pseudo-terminals | Partial | E4.1 | `/dev/ptmx` and `/dev/pts/<n>` over the terminal service with the slave's line discipline: musl's `posix_openpt`, `grantpt`, `unlockpt` and `ptsname`, the size and SIGWINCH, the termios calls through the master, the hangup both ways, and `posix_spawn` with the slave at a child's standard descriptors, which its own children inherit; no `epoll` of such a descriptor, no job control or process groups, 16 at once (the `runtime-terminal` scenario's `/bin/pty-check`). |
| PowerShell 7 (upstream, `dotnet pwsh.dll`, PSReadLine) | Supported | C1.3 | 7.6.7's framework-dependent archive runs unchanged from the data volume with libpsl-native built from the pinned source: the version table, pipelines, files on volumes, `Get-Date`, `Get-ChildItem` and an interactive session under PSReadLine on the terminal (RFC 0020 §57.2–57.5); process enumeration fails explicitly, as the Processes row says. |
| C# in the console (CSharpRepl) and the .NET SDK | Partial | C2.5 | CSharpRepl 0.9.2 runs unchanged from the data volume's layer over the .NET SDK 10.0.300 laid there as a base application, which `dotnet` finds through `/data/global.json` (the `runtime-csharprepl` scenario: expressions, classes, files of the volume, `#load`, a compiler's error); `dotnet build` in a session is measured, not gated, and `#r "nuget:"` waits for a feed on the volume or the network (W). The SDK's publication is per RID (its ReadyToRun code over the IL), so the layer takes the linux-x64 archive on x64 and the linux-arm64 one on ARM64; the scenario passes on x64, waits on ARM64 for S8.2 (CoreCLR relocates the arm64 images' code through a writable and executable window WitOS never grants), and riscv64, for which no SDK is published, has none. |
| Text console on a display (framebuffer, keyboard) | Supported | C2.1, C2.2, C2.3 | The firmware's framebuffer (ramfb, 800 by 600 on both boards) is a device of the firmware bus (C2.1), the display service draws the terminal on it with a pinned bitmap font (Spleen 8 by 16, 100 by 37 cells) and takes the virtio keyboard's keys (C2.2), the terminal mirrors its one session on the display and the serial console, which stays the recovery console, and upstream PowerShell runs on that screen with the keyboard on both ISAs (C2.3, the `runtime-display` scenario); the layout is US until the input model of M9, and colours are drawn in a 16-colour palette (C2.6). |
| Full-screen console applications (TUI) | Supported | C2.6, C4.2 | The display's emulator draws SGR's colours and attributes, the alternate screen and the cursor's visibility, swallows the string sequences and answers the device attributes and the size (C2.6); Arlecchino.Commander, built from its source as a base application, lists, views and copies files on the screen through the keyboard (C4.2, the `runtime-commander` scenario) and, from the author's fork, picks a volume, copies from a USB drive and runs commands and interactive programs under a pseudo-terminal (E4.2, the `runtime-file-manager` scenario); Terminal.Gui's UICatalog 2.5.0 runs with `--disable-cm`, since its configuration watcher needs file change notifications, and CSharpFar needs `HttpClient` at its start, which waits for networking. |
| Named synchronization objects (`Mutex` with a name) | Supported | N4.1 | Under `/tmp/.dotnet` in the namespace service's `/tmp`. |
| Cryptography | Supported | W3.1 | OpenSSL 3.5, the long-term support series, built by the system layer from its pinned release for each ISA (no-asm, no engines of the kernel): the algorithms, X.509 and chains through System.Security.Cryptography's unix flavour, as on Linux (the suite's Cryptography area); the root store is Mozilla's CA bundle at `/etc/ssl/certs/ca-certificates.crt`, which the store reads as one file. |
| Networking (`Socket`, `HttpClient`) | Partial | W4.3 | IPv4 and IPv6 sockets over the network service's managed stack: TCP, UDP, the loopback, dual-stack `AF_INET6` sockets with IPv4 peers in the mapped form, a link-local and a global IPv6 address from the router's advertisement (W4.3), names through musl's resolver, `HttpClient` over HTTP/1.1 (W2.2) and `SslStream` and HTTPS over the OpenSSL the system layer builds (W3.1; the suite's Network area). Not served: multicast group membership, ancillary data, raw sockets, keep-alive probes. Unix domain sockets since W4.1 (the next row). |
| Local sockets and named pipes (`UnixDomainSocketEndPoint`, `NamedPipeServerStream`) | Supported | W4.1 | AF_UNIX stream and sequenced-packet sockets and socketpair, the libc's own: a path below a mount point or an abstract name, connections within a process and between processes, named pipes as on Unix (the suite's Ipc area). A socket's name lives in the namespace service, beside the volume's entries, and is gone at the next boot; no descriptors pass over a socket. |
| ASP.NET Core | Supported | W3.2 | The shared framework Microsoft.AspNetCore.App in the install, built IL alone from its pinned source for every ISA; an ordinary Kestrel application published without a RID serves HTTP and HTTPS over the network service's sockets and answers the runner through the port forward (`runtime-aspnetcore`). No ReadyToRun code: the framework starts under the JIT. |
| Diagnostics (`createdump`, EventPipe, `dotnet-dump`) | Partial | W4.1 | The runtime's diagnostic IPC over a Unix domain socket: a tool reaches a process through the port it starts with (`DOTNET_DiagnosticPorts`; the Ipc area's DiagnosticsIpc). Every process's id is 1, so the default port has one name and the first .NET process takes it. No `createdump`; the debugger story decides the rest. |
| Compression (`System.IO.Compression`) | Supported | R2.3a | zlib-ng and brotli in-tree. |
| Server GC and several scheduling processors | Supported | P1, P2 | Threads run on every processor of the board with their affinity honoured (P1); `Environment.ProcessorCount`, `Thread.GetCurrentProcessorId`, the server collector's heaps and the thread pool see every processor, and the scenarios of N1 and N2 run on four (P2). |
| Network interfaces and their changes (System.Net.NetworkInformation) | Partial | W4.2 | `NetworkInterface.GetAllNetworkInterfaces`, an interface's addresses, hardware address, index, MTU and DNS servers, `GetIsNetworkAvailable` and `NetworkChange`'s subscriptions work as on Linux: the libc answers the netlink route socket and the `SIOCGIF*` ioctls from the network service's configuration (the loopback and `eth0` with the lease), and a change of the lease reaches the sockets that listen. The gateways are empty and the statistics fail, since Linux's files read them from `/proc/net`, which is not emulated; a link's loss is not reported (Compatibility-Differences: Network interfaces). |
| Avalonia and other GUI frameworks | Planned | M9 | Needs the presentation layer of RFC 0009. |

## Hardware

The hardware matrix of Strategy §146.

| Platform | Status | Note |
| --- | --- | --- |
| QEMU x64 (q35) | Primary | Every gate runs on it. |
| QEMU ARM64 (virt) | Primary | Every gate runs on it; a step closes only when the three ISAs pass. |
| QEMU RISC-V (virt) | Primary | Since V3: the kernel (V1), layer 2 (V2.1–V2.3) and the Unix-form runtime (V2.4) pass there, every scenario of runtime-witos among them, and the applications published on Windows run unchanged with every check of the suite as recorded (runtime-published); a step closes only when the three ISAs pass. The NuGet libraries' own suites (N2.2) and .NET 8 beside .NET 10 (N3) stay on x64 and ARM64, caveats until a RISC-V board is a release target. |
| Physical reference machine | Future | After phase V; firmware memory reclamation and the hardware matrix of Strategy §146. |
| Generic PC | Future | After the reference machine. |
