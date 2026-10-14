# N1 — The .NET compatibility suite

N1 is the first step of phase N. It is the compatibility suite of Implementation Strategy §57 and its comparison
across systems (§58). RFC 0015 §9 states the gate: one application runs on Windows, Linux and WitOS, and every
difference it finds is intended and recorded.

## The suite (`tests/DotNetCompatibility`)

**An ordinary application.** The suite is a portable net10.0 console application with nothing for WitOS: no RID, no
WitOS reference, no `#if`. It does not know where it runs except to look up its expectations.

**The areas.** It has Strategy §57's ten areas, plus Platform and, since R4.3, Globalization. Each check exercises the
standard semantics of everyday APIs:

| Area | Checks |
| --- | --- |
| Platform | the identity the runtime reports; whether the system is one of Windows, Linux, macOS and FreeBSD; the processors; `Process.Start` |
| Console | `Console.Out`, `Console.Error`, `SetOut`, `Environment.NewLine`, invariant formatting |
| Tasks | Strategy §55's hundred delays under `WhenAll`, `Task.Run`, continuations, cancellation, completion sources, `Parallel.For`, `AsyncLocal` |
| Threads | start and join, `[ThreadStatic]`, the thread pool, distinct ids, `Sleep`, names |
| Timers | the threading timer, `PeriodicTimer`, `System.Timers.Timer`, `Task.Delay`, `Stopwatch`, `DateTime.UtcNow` |
| Exceptions | catch and finally order, filters, `NullReferenceException` from a fault, `DivideByZeroException`, aggregates, stack traces, `ExceptionDispatchInfo` |
| Reflection | invoke, `Activator`, attributes, generic types, `DynamicMethod`, compiled expression trees |
| FileIO | async text, a `FileStream` seeked and cut, a directory tree, replacing moves, times, `FileSystemWatcher`, under `Path.GetTempPath()` |
| AssemblyLoading | `Assembly.Load` by name, a collectible `AssemblyLoadContext` that unloads, the `Resolving` event, locations |
| GC | weak references, finalizers, a 32 MiB object, the generations, the mode |
| Synchronization | `Monitor` under contention, `SemaphoreSlim`, `ManualResetEventSlim`, `Interlocked`, `ReaderWriterLockSlim`, `Barrier`, `CountdownEvent`, a named `Mutex` |
| Globalization | the specific cultures, `de-DE` numbers, month and day names, culture-aware comparison, Turkish casing, normalization, IDN, a region's currency (R4.3) |

**The run.** Each check runs with a 60 s limit and prints one line with what it observed. The run compares each result
with `expectations.json`, which records, per platform, the checks expected to fail and why:

- a check that fails without a record is unexpected;
- a check that passes where a failure is recorded is unexpected too, so the records stay true;
- the run exits with zero when nothing is unexpected.

## Where it runs

- **Windows and Linux.** The project is part of `WitOS.slnx`. `DotNetCompatibilityTests` in `tests/WitOS.Dev.Tests`
  runs the built suite through `dotnet` on each host that runs the host tests: Windows in CI's `boot` job, Linux in
  `linux-host`.
- **WitOS.** The `runtime-compatibility` scenario (`RuntimeCompatibility`):
  - **The publish.** It publishes the suite with the stock SDK without a RID, from a copy apart from the repository's
    build properties, as R6 publishes its application.
  - **The package.** It boots the package with the device services of `runtime-devices`, the .NET install of R5 and
    the suite under `/app`, with a fresh data disk.
  - **`/bin/init`** (`tests/User/apps_init.c` since N2.1, which shares the package as `RuntimeApps`) makes
    `/data/tmp`, sets `TMPDIR` to it, since the package is read-only and has no `/tmp`, then starts
    `/dotnet/dotnet /app/compatibility/DotNetCompatibility.dll` and waits for it.
  - **What it requires.** No unexpected result, the program's exit with 0, and the services' stop.
  - **How it runs.** `runtime-witos` runs it after `runtime-devices`, and the `runtime-compatibility` command runs it
    alone.

## Published on Windows, run on WitOS (N4.4)

Goal 2 (Strategy §57) says that a program built on Windows with the stock SDK runs unchanged on WitOS. The scenario
above publishes the suite on the Linux host that built the runtime; plan step N4.4 makes the claim literal.

- **`publish-apps [--output <directory>]`** publishes the suite and R6's portable application on the host it runs on,
  with the stock SDK and no RID, each from a copy of its project apart from the repository's build properties (the same
  MSBuild as Visual Studio's "Publish" to a folder), and writes `published.json` beside them: the host, the SDK's
  version and every file with its size and SHA-256. A Windows publish holds the Windows apphosts
  (`DotNetCompatibility.exe`, `PortableApp.exe`), which WitOS never runs, beside the assemblies, the `deps.json` and the
  `runtimeconfig.json`; the package carries every file as published, unchanged.
- **`runtime-published [--arch <isa>] [--published <directory>]`** requires the directory to hold exactly the files the
  manifest names, byte for byte, builds the `runtime-published` package (the device services, the .NET install and the
  two applications under `/app`, each run by `apps_init`), boots it with the terminal as `runtime-compatibility`
  boots, and then compares each check's verdict in the guest's log with the record `runtime-compatibility` wrote on this
  runtime (`artifacts/runtime-witos/compatibility-<arch>.json`: the host that published, its SDK and each check's
  pass, fail or not applicable). A check that ended otherwise, or ran on one side alone, fails the command; the report
  goes to `artifacts/runtime-witos/compatibility-published-<arch>.json`. Without `--published` the command publishes
  on this host first, a check of the tool itself.
- **CI.** The `windows-publish` job (Windows) publishes and uploads `witos-published-apps-windows`; the
  `published-on-windows` job, one per ISA after `linux-host` and `windows-publish`, downloads that upload beside the
  install, the services and the record its `linux-host` leg uploaded, and runs `runtime-published` on it. Host tests
  check the manifest, the reading of the checks, the comparison and that the workflow has both jobs.

## Results

| System | Checks | Recorded differences | Unexpected |
| --- | --- | --- | --- |
| Windows (x64, .NET 10.0.8) | 72 | 0 | 0 |
| Linux (x64, Ubuntu 24.04, .NET 10.0.8) | 72 | 0 | 0 |
| WitOS x64 | 72 | 4 | 0 |
| WitOS ARM64 | 72 | 4 | 0 |

N1 ran 64 checks; R4.3 added the eight of Globalization, which pass on every system once WitOS has ICU.

The four differences on WitOS, each recorded in `expectations.json` and in `Compatibility-Differences.md`:

| Check | What WitOS does | Why |
| --- | --- | --- |
| `Platform.KnownOperatingSystem` | None of `IsWindows`, `IsLinux`, `IsMacOS` and `IsFreeBSD` | WitOS is a new operating system. An application that infers Windows from "not Linux and not macOS" misbehaves, a cost ADR 0024 accepts and RFC 0015 §1 records |
| `Platform.ProcessStart` | `PlatformNotSupportedException` | `System.Diagnostics.Process` waits for the libc's `posix_spawn` over the process manager (RFC 0015 §8) |
| `FileIO.Watcher` | `PlatformNotSupportedException` | The namespace service sends no change notifications |
| `Synchronization.NamedMutex` | `IOException`: `stat("/tmp/")` is `ENOENT` | .NET keeps named synchronization objects under the fixed `/tmp/.dotnet`, whatever `TMPDIR` says, and WitOS has no `/tmp` |

Every other check behaves as on Windows and Linux, on both ISAs. That covers tasks, threads, timers, exceptions from
hardware faults, emitted code, collectible contexts, the GC, synchronization and the BCL's file APIs over the ext4
volume.

## Limits kept explicit

- **The suite is a sample.** Each area holds the semantics an ordinary application relies on, not the BCL's whole
  surface; upstream's library tests (R7.3) go deeper.
- **`TMPDIR` is the scenario's.** A process the system starts has no temporary directory unless something names one.
  The application environment is N4's.
- **The differences are recorded, not fixed.** `Process.Start`, file notifications and `/tmp` are each later work:
  the process manager's .NET path, the namespace service's notifications, and N4's environment.
