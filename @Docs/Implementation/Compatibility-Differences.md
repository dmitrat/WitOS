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
| Globalization | Invariant mode only. There is no ICU. | The program must run with `System.Globalization.Invariant=true`. Without it, the first culture lookup fails fast with "Couldn't find a valid ICU package". | R3.3, R4.1 |
| Cryptography | System.Native's random bytes, plus the managed SHA-1/256/384/512, HMAC, HKDF, PBKDF2 and SP 800-108 implementations the browser also uses. | `RandomNumberGenerator` and the hashes work. `RSA`, `ECDsa`, `AES`, X.509 and every other algorithm throw `PlatformNotSupportedException`. | R4.1 |
| Console | Output goes to the kernel log. Standard input ends at once and is not a terminal. | `Console.WriteLine` works. `Console.ReadLine` returns null, and `Console.IsInputRedirected` is true. | R4.1 |
| Processes | There is no `Process.Start`. | `Process.Start` throws `PlatformNotSupportedException`. `Environment.ProcessId` works. | R4.1 |
| Signals | Terminal and job-control signals install but are never raised, since there is no terminal service yet. | `PosixSignalRegistration.Create` and `Console.CancelKeyPress` succeed, and their handlers never run. | R4.1 |
| Platform identity | `OSPlatform.Create("WITOS")`, `PlatformID.Unix`, and the RIDs `witos-x64` and `witos-arm64`. | `RuntimeInformation.IsOSPlatform(OSPlatform.Create("WITOS"))` is true. Under `corerun`, the RID arrives as a property. | R2.3a, R3.3, R4.1 |
| Processors | One processor schedules threads until phase P. | `Environment.ProcessorCount` is 1. | R2.2, R4.1 |
| Hosts | There is no `dotnet` host, `hostfxr` or `hostpolicy` until R5. | Programs run under NativeAOT, or under `corerun` with the properties a runtimeconfig would give on its command line. | R3.2 |
| Code | Every method is JIT-compiled. No ReadyToRun code exists for `witos`. | Startup is slower than on Linux, and the behaviour is the same. | R3.2 |
| Networking | Absent until the network milestone. | Not exercised yet. | — |
| Diagnostics | No `createdump`, no EventPipe transport and no diagnostic IPC. | The runtime's diagnostic server fails to open its socket and runs without it. | R3.2a (trace) |
