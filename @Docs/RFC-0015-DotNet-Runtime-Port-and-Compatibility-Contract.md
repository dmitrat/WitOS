# RFC 0015 — .NET Runtime Port & Compatibility Contract

Draft v2, 2026-10-06. Supersedes the v0.x drafts (the last was v0.10, written against the Windows form of the
runtime). Applies [ADR 0024](Implementation/ADR-0024-Three-Layers-and-Unix-Form-Runtime.md) and
[RFC 0011 v3](RFC-0011-Kernel-Architecture-and-ABI.md); written as plan step A3 of [PLAN.md](../PLAN.md).

**Status.** Normative for phases R (runtime) and N (standard .NET) of the plan. No .NET runtime executes in the guest
yet. The hosted evidence of the frozen Windows-form line (§10) remains evidence of what upstream requires; it is not
guest .NET support and is never described as such.

## 1. The compatibility contract

WitOS promises what the Developer Experience Manifesto §2 promises: a portable managed application, built on another
operating system against a standard TFM, runs on WitOS without recompilation.

| Promise | Meaning |
| --- | --- |
| Unchanged binaries | `App.dll`, `App.deps.json`, `App.runtimeconfig.json` and portable dependencies from NuGet, produced by `dotnet build` or `dotnet publish` without a RID on Windows, Linux or macOS, start with `dotnet App.dll`. The application needs no WitOS reference, no `#if WITOS` and no WitOS project type (Manifesto §7–§8). |
| Upstream runtime | CoreCLR, the JIT, CoreLib and the BCL are upstream .NET built from pinned sources with a recorded patch set (§4). There is no WitOS CoreLib and no fork of managed semantics (RFC 0001 §6). |
| Standard semantics | `Task`, `Thread`, `ThreadPool`, GC, exceptions, timers, `FileStream`, reflection, assembly loading and synchronization behave as on other platforms (Strategy §56). Differences are intentional and recorded by the compatibility suite (Strategy §57–§58; plan step N1). |
| Explicit failure | An unsupported API throws `PlatformNotSupportedException` or fails with a documented error; a stub that pretends success is not compatibility. |
| Several runtimes | Two .NET versions install side by side and the host selects by `runtimeconfig.json` and `rollForward` (§7; goal 5 of ADR 0024). |

Outside the promise (Manifesto §12, RFC 0001 §26): Win32 or Linux P/Invokes, COM, the registry, WPF and WinForms,
native libraries without `witos` assets, foreign apphosts and self-contained `win-x64` or `linux-x64` distributions.
WitOS does not block them; it promises them nothing. An application that infers "not Linux, not macOS, therefore
Windows" misbehaves on WitOS; that cost is accepted by ADR 0024 and is reported by the suite, not hidden.

NativeAOT is the deployment tool of WitOS's own system components (M3 and Manifesto §5) and an option for
applications; it is not the application compatibility contract. The contract is ordinary IL under CoreCLR (M6,
Manifesto §16).

## 2. The runtime form

The runtime is the **Unix form** of upstream .NET: `TARGET_UNIX` with a new `TARGET_WITOS`, built as `TargetOS=witos`
for the runtime identifiers `witos-x64` and `witos-arm64` (later `witos-riscv64`). ADR 0024 records why: CoreCLR has
exactly two forms, every port to a new operating system is a variant of the Unix one, and the Unix form alone covers
RISC-V. The Windows form, with its Win32 binding surface, is the frozen line of `P6.4-Plan.md`.

What the form implies for the layers of RFC 0011:

- **CoreCLR's PAL** (`src/coreclr/pal`) is upstream code compiled for WitOS, not a WitOS-written PAL. It reaches the
  operating system through the C library of layer 2: pthreads, `mmap`, signals, `dlopen`, `clock_gettime`. Its 101
  configure-time checks (`src/coreclr/pal/src/configure.cmake`) read the WitOS sysroot and settle most differences
  without a patch.
- **The GC's OS layer** is `src/coreclr/gc/unix/gcenv.unix.cpp` with 33 configure checks; it uses `mmap`, `mprotect`,
  `madvise`, `sysconf`, `membarrier` or an equivalent, `sched_getaffinity` and `pthread_setaffinity_np`, and reads
  cgroups (`cgroup.cpp`) and NUMA (`numasupport.cpp`) where they exist.
- **NativeAOT's runtime** uses `src/coreclr/nativeaot/Runtime/unix` (`PalUnix.cpp`, `HardwareExceptions.cpp`,
  `NativeContext.cpp`, `UnixSignals.cpp`, `UnixNativeCodeManager.cpp`, `UnwindHelpers.cpp` over the vendored
  `llvm-libunwind`; 18 configure checks). WitOS's current NativeAOT adapters, written against the Windows PAL, are
  knowledge for this port and are deleted with the frozen line (plan step R2).
- **The framework's native libraries** (`src/native/libs`) are the Unix set: `System.Native` (253 entry points in
  10.0.8), `System.Globalization.Native` over ICU, `System.Security.Cryptography.Native` over OpenSSL,
  `System.Net.Security.Native` over GSSAPI, `System.IO.Compression.Native` with in-tree zlib-ng and brotli; 148
  configure checks in `src/native/libs/configure.cmake`.
- **The hosts** (`src/native/corehost`) are the Unix builds: `dotnet`, `libhostfxr.so`, `libhostpolicy.so` over
  `hostmisc/pal.unix.cpp`.
- **The JIT** is the Unix x64 and ARM64 code generation with the SysV ABI; it has no operating-system code beyond
  `TARGET_UNIX`.
- **NativeAOT compilation** is ILC with `--targetos` naming WitOS, ELF output from the managed object writer
  (`ILCompiler.Compiler/Compiler/ObjectWriter/ElfObjectWriter.cs`) and a link through
  `Microsoft.NETCore.Native.Unix.targets` with clang and lld against the WitOS sysroot.

## 3. Platform identity

| Where | Value | How it is set |
| --- | --- | --- |
| `OperatingSystem.IsOSPlatform("WitOS")` | true | `OSPlatformName` in `System.Private.CoreLib/src/System/OperatingSystem.cs` gains `#elif TARGET_WITOS "WITOS"`, the one place the identity is spelled; `IsWindows()`, `IsLinux()`, `IsFreeBSD()` and the others are false. No `IsWitOS()` is added to the public surface: a new public API needs upstream review and ordinary code uses `IsOSPlatform`. |
| `RuntimeInformation.RuntimeIdentifier` | `witos-x64`, `witos-arm64` | the RID graph `Microsoft.NETCore.Platforms/src/runtime.json`: `witos-x64` imports `witos` and `unix-x64`, as `freebsd-x64` does. No OS version in the identifier, as `linux-musl-x64` has none. |
| `RuntimeInformation.OSDescription` | `WitOS <version>` | `System.Native`'s `uname` path over the libc's `uname`, which the system layer fills from the kernel's identity |
| `Environment.OSVersion.Platform` | `PlatformID.Unix` | as on FreeBSD and macOS; `PlatformID` has no finer value and this is the one API where the form is visible |
| `Path.DirectorySeparatorChar`, `Environment.NewLine` | `/`, `\n` | the Unix form |
| `TargetOS` of ILC | `WitOS` | `TargetDetails.cs` enum and the `--targetos` parser; `Microsoft.NETCore.Native.Unix.targets` learns the triple `<arch>-unknown-witos` and the lld flavor |
| Build | `TargetOS=witos` | `eng/build.sh` (`--os witos`), `eng/native/build-commons.sh`, `eng/native/configureplatform.cmake` (`CLR_CMAKE_TARGET_WITOS`, `CLR_CMAKE_TARGET_UNIX`), `Directory.Build.props` conditions, `System.Private.CoreLib.Shared.projitems` (`TARGET_WITOS` define) |

NuGet resolves a package's native assets through the RID graph: `witos-x64 → witos → unix-x64 → unix → any`. A
package that ships `linux-x64` natives only has none for WitOS; that is the "native libraries without WitOS assets"
case of Manifesto §12 and is a package author's port, not a WitOS compatibility shim.

## 4. The patch set

Upstream is taken by canonical-byte pin (today `dotnet/runtime` `v10.0.8`, commit `b82454ca`, with the VMR commit of
the published packages recorded in `experiments/NativeAotProbe/upstream.lock.json`). Every change is a file in
`patches/runtime/` with its `Source`, `Output`, `Before` and `After` header and a purpose, applied only through
`UpstreamPatches`, which has no fuzz. The 15 patches there today belong to the Windows-form NativeAOT overlay and are
removed with that line at R2; the `witos` patch set starts empty at R1.

**Budget.** The size of the FreeBSD and Haiku ports in 10.0.8: FreeBSD touches 31 files of CoreCLR and 62 of the
native libraries, Haiku 18 and 11 (ADR 0024). The patch set is measured and recorded at every step of phase R, and
R8 rehearses carrying it to the next 10.0.x and to a .NET 11 preview.

**Order of preference** when WitOS differs from Linux:

1. a configure-time check that already exists reads the sysroot and turns the code off (`HAVE_*` in
   `config.h.in`); no patch;
2. a runtime configuration knob upstream already has (`System.Globalization.Invariant`, `DOTNET_EnableWriteXorExecute`,
   `DOTNET_gcServer`), set in `runtimeconfig.json` or the environment by the system layer; no patch;
3. a `TARGET_WITOS` branch beside the existing `TARGET_FREEBSD`, `TARGET_OSX` or `TARGET_HAIKU` branches, where the
   Unix form already differs by platform (double mapping, install location, CPU count, dumps);
4. never a change to CoreLib semantics, the GC algorithm, the JIT, managed BCL behavior or the hosts' resolution logic.

**Where the patches are expected** (verified paths at the pinned commit): the build and identity files of §3;
`src/coreclr/pal/src` where a facility is absent (process creation in `thread/process.cpp`, dumps, `sysinfo.cpp` for
memory and CPU figures); `src/coreclr/gc/unix` for cgroups, NUMA and the process barrier;
`src/coreclr/minipal/Unix/doublemapping.cpp` for the W^X double mapping (`memfd_create` on Linux, `shm_open` on
FreeBSD, a memory object on WitOS); `src/coreclr/nativeaot/Runtime/unix` for `cgroupcpu.cpp` and `PalCreateDump.cpp`;
`src/native/libs/System.Native` for `pal_process.c` (`vfork`, `execve`), `pal_signal.c` (terminal signals),
`pal_mount.c`, `pal_uid.c`, `pal_sysctl.c` and `pal_networking.c`; `src/native/corehost/hostmisc/pal.unix.cpp` only if
the install-location file of §7 does not suffice. Each patch names the plan step that introduced it and the
alternative of the preference order it could not use.

**Contribution.** The patch set is aimed at an upstream `witos` target; when to propose it (after R7 or after N1) is
an open question of the plan.

## 5. The substrate the runtime needs

The POSIX-shaped substrate of layer 2 (RFC 0011 §9) exists because the Unix form calls it. The table maps what
upstream calls to the ABI-2 piece that serves it and the ABI-1 mechanism beneath; applications never see this column
of the table.

| Upstream need | Where upstream calls it | ABI-2 | ABI-1 (RFC 0011 §7) |
| --- | --- | --- | --- |
| Reserve, commit, decommit, reset, protect | `gcenv.unix.cpp`, `pal/src/map/virtual.cpp`: `mmap` with `PROT_NONE` and `MAP_NORESERVE`, `mprotect`, `madvise`, `munmap` | libc `mmap` family | `MEMORY_RESERVE`, `MEMORY_COMMIT`, `MEMORY_PROTECT`, `MEMORY_RESET`, `MEMORY_DECOMMIT`, `MEMORY_RELEASE` |
| W^X code | `executableallocator.cpp` over `doublemapping.cpp`: one file descriptor mapped twice | a `TARGET_WITOS` branch creating a memory object; `mmap` of it twice | `MEMORY_OBJECT_CREATE`, `MEMORY_OBJECT_MAP`, `CODE_PUBLISH` |
| Physical memory and CPU count | `sysconf(_SC_PHYS_PAGES, _SC_AVPHYS_PAGES, _SC_NPROCESSORS_ONLN, _SC_PAGESIZE, _SC_LEVEL*_CACHE_SIZE)`; cgroups and `/proc` where present | libc `sysconf`; cgroups and `/proc` compiled out | `MEMORY_QUERY`, `PROCESSOR_QUERY` |
| Threads | pthreads: create, join, TLS, affinity, `sched_yield` | libc pthreads (S2) | `THREAD_CREATE`, `THREAD_EXIT`, `OBJECT_WAIT`, `THREAD_SET_TLS`, `THREAD_YIELD`, `THREAD_AFFINITY` |
| Waits | `pthread_mutex`, `pthread_cond`, PAL synchronization manager, `nanosleep` | libc over a futex-equivalent | `EVENT_*`, `OBJECT_WAIT`, `SLEEP_UNTIL` |
| Process-wide barrier | `FlushProcessWriteBuffers` in `gcenv.unix.cpp`: `membarrier` or a `mprotect` trick | a `TARGET_WITOS` branch calling `libwitos` | `PROCESS_WRITE_BARRIER` |
| Hardware faults | `pal/src/exception/signal.cpp` and `nativeaot/Runtime/unix/HardwareExceptions.cpp`: `SIGSEGV`, `SIGBUS`, `SIGFPE`, `SIGILL`, `SIGTRAP` with `ucontext`, `sigaltstack` | libc signals (S3) | `EXCEPTION_REGISTER`, `EXCEPTION_QUERY`, `EXCEPTION_CONTINUE` |
| Thread activation | `INJECT_ACTIVATION_SIGNAL` (`SIGRTMIN`, else `SIGUSR1`) sent with `pthread_kill`, handled with the interrupted context | libc `pthread_kill` and a real-time signal (S3) | `THREAD_ACTIVATE` through the fault callback |
| Unwinding | PAL `seh-unwind.cpp` over `libunwind` (`unw_init_local`, `unw_step`); NativeAOT over `llvm-libunwind` | LLVM libunwind, libc++abi (S4); the PAL may use the vendored copy | none; DWARF tables are user data |
| Time | `minipal/time.c`, `pal/src/misc/time.cpp`: `clock_gettime(CLOCK_MONOTONIC)`, `CLOCK_REALTIME`, `nanosleep` | libc clocks | `CLOCK_READ`, `CLOCK_FREQUENCY`, `SLEEP_UNTIL` |
| Entropy | `minipal/random.c`, `System.Native/pal_random.c`: `getrandom`, `arc4random_buf` or `/dev/urandom` | libc `getrandom` | `RANDOM` |
| Dynamic loading | PAL `loader/module.cpp`, `pal_dynamicload.c`, the hosts: `dlopen`, `dlsym` | `ld.so` (S5) | `MEMORY_OBJECT_MAP` of the image |
| Files | `pal_io.c`, PAL file and mapping APIs: `open`, `read`, `pread`, `fstat`, `readdir`, `mmap` of files | libc over the package object (S1), then the namespace client (D5) | memory objects; channels |
| Environment, directory, exit | `pal_environment.c`, `environ`, `getcwd`, `exit` | libc (S6) | `PROCESS_EXIT` |
| Console | `pal_console.c`: `termios`, `ioctl`, `isatty`, `SIGWINCH`, `SIGTTOU` | write-only console over the kernel log first; the terminal of RFC 0020 later | `DEBUG_WRITE`; channels |
| Processes | `pal_process.c`: `vfork`, `execve`, `pipe2`, `waitpid`; PAL `process.cpp` for dumps | unsupported at first (R4); then a `posix_spawn`-shaped API over the process manager | `PROCESS_CREATE`, channels |
| Sockets | `pal_networking.c`: BSD sockets, `epoll` or `kqueue` | none until the network milestone (M7) | channels to the network service |

Rules this table fixes:

- **The substrate is private.** No application API of WitOS exposes POSIX; `libwitos` and `OutWit.OS.*` present
  capabilities (RFC 0011 §9.4). A runtime binary is the only client of `libc.so` the platform supports.
- **Signals are the minimal set:** the five synchronous ones, the activation signal, `SIGABRT` from `abort`, and
  `SIGCHLD` once processes exist. Terminal and job-control signals (`SIGINT`, `SIGQUIT`, `SIGTERM`, `SIGHUP`,
  `SIGWINCH`, `SIGTTOU`, `SIGTTIN`, `SIGTSTP`, `SIGCONT`) can be installed and are never raised until a terminal
  service exists; `kill` to another process is absent. The runtime's handlers for them install without error, which
  is what the PAL's `SEHInitializeSignals` requires.
- **No `fork`.** `Process.Start` is unsupported until the process manager exists; then `pal_process.c` gets a
  `TARGET_WITOS` branch over `posix_spawn`, never an emulated `fork`.
- **Memory figures come from the kernel**, not from `/proc` or cgroups: the GC's `GetPhysicalMemoryLimit` and
  `GetMemoryStatus` read `sysconf` values the libc derives from `MEMORY_QUERY`.
- **File mappings are memory objects.** `mmap` of a file maps the object the namespace service hands over; for the
  boot package this is zero-copy. Until the service exists, `mmap` of a file is `ENODEV` and the loader reads instead.

## 6. Memory, threading and exception semantics

The semantic requirements of the v0.x drafts stand; the Unix form states them in its own terms.

**Memory.** `GCToOSInterface` separates reserve, commit, decommit, reset and release. A reservation keeps virtual
identity without backing; commit yields zero pages; decommit discards content and keeps the reservation; reset
(`VirtualReset`, `madvise(MADV_DONTNEED)` or `MADV_FREE`) discards content without changing commitment; recommit never
shows stale data; release ends the reservation. Out of memory in a process never panics the kernel. Write-watch is
unsupported in the pinned Unix GC and is not invented. The null area is one page (`PAL` on Unix: 4 KiB); the user
range of a process starts above it. Executable memory is W^X: a JIT writes through one view and executes through
another, and `DOTNET_EnableWriteXorExecute=0` is not a supported configuration on WitOS.

**Threads and GC suspension.** The Unix form suspends threads for the GC cooperatively: `FlushProcessWriteBuffers`,
return-address hijacking and the activation signal that runs a callback with the interrupted context; it never calls a
`SuspendThread` and never reads another thread's registers through the kernel. The kernel's `THREAD_SUSPEND` and
`THREAD_CONTEXT_GET` serve debuggers and diagnostics, not the GC. The finalizer thread, the ThreadPool's threads and
timer threads are ordinary pthreads. Concurrent and server GC are configuration, not platform features; the first
profile runs workstation non-concurrent GC and enables the rest when the SMP phase makes them meaningful.

**Exceptions.** Managed and C++ exceptions are user-space business of the Itanium model: the runtime's own dispatcher
over `libunwind` and DWARF tables. Hardware faults arrive as signals with a `ucontext`; the PAL's `HandleHardwareException`
converts them and continues or unwinds in user space; the kernel's part is delivery and validated continuation
(RFC 0011 §7.5). `RtlRestoreContext` and `RtlCaptureContext` are assembly in the runtime, not system calls.

**TLS.** ELF TLS through the thread pointer (`FS` on x64, `TPIDR_EL0` on ARM64), initialized by the libc and the
loader; the runtime's thread statics live in ordinary `__thread` storage. No TEB, no compiler TLS page of the kernel.

**Instruction sets.** The kernel saves x87 and SSE state on x64 and the full FP/SIMD state on ARM64; `AVX` and later
extensions are enabled for the JIT and NativeAOT only when the kernel's context profile (`CONTEXT_PROFILE`) reports
the matching state saved. `minipal/cpufeatures.c` reports what CPUID says; the runtime masks it with the kernel's
profile through the libc's `sysconf` or an `elf_aux` equivalent decided in R3.

## 7. Several .NET versions side by side

Goal 5 of ADR 0024 is the ordinary shared-framework layout under `/dotnet`, the root AGENTS.md fixes for the
installation:

```
/dotnet/dotnet
/dotnet/host/fxr/<hostfxr version>/libhostfxr.so
/dotnet/shared/Microsoft.NETCore.App/8.0.x/   (libcoreclr.so, libclrjit.so, libhostpolicy.so, System.Private.CoreLib.dll, ...)
/dotnet/shared/Microsoft.NETCore.App/10.0.x/
/etc/dotnet/install_location, install_location_x64, install_location_arm64   (each containing /dotnet)
```

- The host finds the installation through `/etc/dotnet/install_location` and the per-architecture variants that
  `hostmisc/pal.unix.cpp` reads, so the package ships those files and `pal.unix.cpp` is not patched.
- `App.runtimeconfig.json` names the framework version and `rollForward`; `hostfxr` resolves as on every platform.
- Each runtime version is built from its release branch (`release/8.0`, `release/10.0`) with its own patch set under
  `patches/runtime/<branch>/`; the `witos` target does not exist upstream for 8.0 and is a backport maintained for
  as long as that release is supported.
- What makes them coexist is ABI-2 stability (RFC 0011 §10.2): every runtime links the same `libc.so`, C++ runtime and
  `libwitos.so.1`, and an older runtime never needs a symbol the system layer lacks.
- Side-by-side is proven at plan step N3 with .NET 8 LTS and .NET 10 in one image.

## 8. Feature profile by phase

| Area | First guest profile (R2–R6) | Later |
| --- | --- | --- |
| GC | workstation, non-concurrent | concurrent and server GC with SMP (phase P) |
| Globalization | invariant mode (`System.Globalization.Invariant`); ICU absent | ICU from source when a scenario needs culture data |
| Cryptography | `PlatformNotSupportedException`; no OpenSSL | a provider decided with the storage of keys and the network |
| Networking | absent; `System.Net.Security.Native` absent | M7 over the network service |
| Files | read-only boot package, then the namespace service (D5) | persistent storage (D6) |
| Processes | `Process.Start` unsupported | `posix_spawn` over the process manager |
| Console | write-only over the kernel log | the terminal of RFC 0020 |
| Diagnostics | no `createdump`, no EventPipe transport | decided with the debugger story |
| Compression | zlib-ng and brotli in-tree | unchanged |
| Time | monotonic; UTC after K6 | time zones from a package of tzdata |
| Dynamic code | JIT, `Reflection.Emit`, `AssemblyLoadContext` | unchanged |

Every "absent" and "unsupported" cell fails explicitly and appears in the compatibility suite's recorded differences.

## 9. Gates

Each gate is a plan step; a gate passes on guest acceptance, on x64 and ARM64 where the plan says so, never on a
hosted result.

| Step | Gate | Evidence |
| --- | --- | --- |
| R1 | the Unix form builds for `TargetOS=witos` on a Linux host: `clr.runtime`, `libs.native`, `host`, `nativeaot` | build logs, the patch set size against the budget of §4 |
| R2 | NativeAOT passes the M3 acceptance on the new substrate: GC, exceptions, finalization, threads and TLS, waits | the `NativeAotBoot` probes in QEMU on both ISAs; the Windows line is deleted afterward |
| R3 | `coreclr_initialize` and a managed `Main` through the JIT in the guest | guest markers; `clrjit` Unix x64 and ARM64 |
| R4 | `System.Native` over the package, time, environment, invariant globalization, explicit refusals | guest markers; the differences list |
| R5 | `dotnet App.dll` through `hostfxr` and `hostpolicy` from the shared-framework layout of §7 | guest markers |
| R6 | an application built in Visual Studio on Windows without a RID runs unchanged | the first IL through the JIT in the guest, on x64 |
| R7 | upstream tests in QEMU: the PAL suite (`src/coreclr/pal/tests/palsuite`), slices of `src/tests` (JIT, GC, EH), slices of the library tests | a runner and a report |
| R8 | the patch set rebased on the next 10.0.x and on a .NET 11 preview | measured size and effort |
| N1 | the compatibility suite of Strategy §57 on Windows, Linux and WitOS | recorded, intentional differences only |
| N2 | NUnit, xUnit, MessagePack, MemoryPack, Math.NET, Roslyn | their own tests in the guest |
| N3 | .NET 8 and .NET 10 side by side | selection by `rollForward` |
| N4 | UTC, `Environment`, part of `Process`, the console of RFC 0020 | guest markers |

Gate B of the Strategy (§61), "WitOS runs .NET", follows N4.

## 10. Evidence of the frozen line

The hosted experiments of the Windows form remain in the tree until R2 and keep their meaning as evidence about
upstream, never about the guest:

- `runtime-audit` verifies the pinned sources and package provenance; `runtime-probe` runs a Windows NativeAOT
  reference workload (GC, finalization, exceptions, threads and TLS, waits, Tasks, clocks) and reports its native
  imports; `runtime-target` enters the real runtime from a C host on Windows with strict link boundaries;
  `runtime-source` builds the full native libraries in a Windows reference and an incomplete WitOS-overlay profile;
  `runtime-port` boots the Windows-form GC memory adapter in QEMU.
- Their documents (`Implementation/NativeAot-*.md`, `P6-*.md`, `P6.4-Plan.md`) describe the Windows-form adapters:
  the order of runtime initialization, the GC and PAL requirements, the unwind dispatcher, the acceptance protocols of
  M3 and P5. They are carried into phase R as knowledge (ADR 0024) and their code is removed at K8 and R2.

Nothing in this section is guest .NET support; AGENTS.md forbids describing host-side tooling as such.

## 11. History

- v0.1–v0.3 (2026-09-16): the evidence baseline (`v10.0.8` pins), the hosted NativeAOT probe, the inventory of
  integration surfaces beyond `Pal.h`, the M2 ABI implications.
- v0.4–v0.10 (2026-09-17 to 2026-09-20): the target and bootstrap experiment, the guest PE loading path, the GC
  memory, discovery, event, time and mutex adapters written against the Windows form; ADR 0006 selected Windows x64
  code generation with a WitOS source adapter.
- v2 (2026-10-06): this document, after ADR 0024 reversed that selection: the Unix form, `TargetOS=witos`, the
  substrate private to layer 2, the patch set, platform identity and side-by-side runtimes.
