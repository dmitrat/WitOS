# Compatibility differences

This list covers how a .NET program on WitOS behaves differently from the same program on Linux. RFC 0015 §8
requires every absent or unsupported area to fail explicitly and to appear here. Each row names the behaviour, how a
program observes it, and the step that verified it. Rows change only together with a check that verifies the new
behaviour.

| Area | On WitOS | How a program sees it | Verified by |
| --- | --- | --- | --- |
| Files | The boot package is the file system, read-only. There are no symbolic links. | Reads, `File.Exists`, directory enumeration, `FileStream` seeking and relative paths behave as on Linux. A write, a create or a `mkdir` throws `IOException` or `UnauthorizedAccessException` (`EROFS`, or `EEXIST` for an existing name). | R4.1, `libc_hello.c` |
| File locks | `flock` locks are those of the process's own open file descriptions. | `FileShare` behaves as on Linux within the process. No other process sees the locks. | R4.1, `libc_hello.c` |
| Shared memory | `shm_open` and `memfd_create` files live within the process, on memory objects. | CoreCLR's double mapping works. A `MAP_PRIVATE` mapping of such a file is `ENOSYS`, and reading the file is `EINVAL`. | R3.2b, `libc_hello.c` |
| Time | UTC from the board's real-time clock. There is no tzdata. | `DateTime.UtcNow` is right, and `TimeZoneInfo.Local` is UTC. | R4.1 |
| Globalization | Invariant mode only. There is no ICU. | Every process the system layer starts has `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` in its environment, so an application runs in invariant mode unchanged. A process whose environment lacks it fails fast at the first culture lookup ("Couldn't find a valid ICU package"). | R3.3, R4.1, R6 |
| Cryptography | System.Native's random bytes, plus the managed SHA-1/256/384/512, HMAC, HKDF, PBKDF2 and SP 800-108 implementations the browser also uses. | `RandomNumberGenerator` and the hashes work. `RSA`, `ECDsa`, `AES`, X.509 and every other algorithm throw `PlatformNotSupportedException`. | R4.1 |
| Console | Output goes to the kernel log. Standard input ends at once and is not a terminal. | `Console.WriteLine` works. `Console.ReadLine` returns null, and `Console.IsInputRedirected` is true. | R4.1 |
| Processes | There is no `Process.Start`. | `Process.Start` throws `PlatformNotSupportedException`. `Environment.ProcessId` works. | R4.1 |
| Signals | Terminal and job-control signals install but are never raised, since there is no terminal service yet. | `PosixSignalRegistration.Create` and `Console.CancelKeyPress` succeed, and their handlers never run. | R4.1 |
| Platform identity | `OSPlatform.Create("WITOS")`, `PlatformID.Unix`, and the RIDs `witos-x64` and `witos-arm64`. | `RuntimeInformation.IsOSPlatform(OSPlatform.Create("WITOS"))` is true. Under `corerun`, the RID arrives as a property. | R2.3a, R3.3, R4.1 |
| Processors | One processor schedules threads until phase P. | `Environment.ProcessorCount` is 1. | R2.2, R4.1 |
| Threads and resources | A process may run 32 threads and hold 256 handles, 64 events, 128 memory objects and 1024 descriptors. Thread stacks are 1.5 MiB, committed when the thread starts. | Beyond a quota, thread creation fails with `OutOfMemoryException` and `open` with `EMFILE`. A stack overflow on a thread ends the thread without the "Stack overflow" report. | R6 |
| Hosts | `dotnet`, `hostfxr` and `hostpolicy` are upstream's, unpatched, in the shared-framework layout under `/dotnet`. The framework's deps.json is written by WitOS's tool, since upstream's CoreCLR runtime pack needs crossgen. | `dotnet App.dll` works as on Linux. The SDK's commands are not installed. | R5 |
| Code | Every method is JIT-compiled. No ReadyToRun code exists for `witos`. | Startup is slower than on Linux, and the behaviour is the same. | R3.2 |
| Networking | Absent until the network milestone. | Not exercised yet. | — |
| Diagnostics | No `createdump`, no EventPipe transport and no diagnostic IPC. | The runtime's diagnostic server fails to open its socket and runs without it. | R3.2a (trace) |
