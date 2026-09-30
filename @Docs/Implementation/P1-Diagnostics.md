# P1.6 diagnostic messages and native buffer ownership

The initial native diagnostic profile supplies FormatMessageW, LocalFree, honest Windows Event Log failure paths and debugger-presence discovery. It uses ordinary upstream CoreLib and the actual source-built native allocator. No Windows implementation library is linked into the guest.

## Messages required by CoreLib

Pinned `Interop.FormatMessage.cs::GetMessage` requests FROM_SYSTEM | IGNORE_INSERTS | ARGUMENT_ARRAY into a 256-character stack buffer. If the error is ERROR_INSUFFICIENT_BUFFER it retries with ALLOCATE_BUFFER and frees the result through Marshal.FreeHGlobal -> LocalFree. Unknown-message failures reach the existing managed hexadecimal fallback. These managed methods remain unchanged.

The WitOS catalogue contains 27 meaningful English messages (including suspend-count overflow added by P1-Thread-Suspension) covering the currently emitted native adapter errors and message-resource errors. These are WitOS descriptions, not copied Windows resource text. Return counts exclude the terminating UTF-16 NUL. Insufficient caller capacity fails without changing the destination. Default language and en-US are supported; other requested languages fail explicitly. Unknown codes report ERROR_MR_MID_NOT_FOUND rather than an invented successful message.

System lookup works before image publication and without compiler TLS. FROM_HMODULE combined with FROM_SYSTEM also supports the actual published image: the current kernel PE profile rejects resource directories, so the system catalogue is the real fallback. Module-only lookup reports missing resources; unknown module bases fail. FROM_STRING, substitution formats and arbitrary line wrapping are outside this bring-up path. Known unsupported modes fail; unknown flag bits report ERROR_INVALID_FLAGS. Width zero and 255 are supported for the single-line catalogue. None of the catalogue messages contains insertion placeholders, so no argument pointer is read.

## Allocated messages and LocalFree

ALLOCATE_BUFFER allocates at least max(nSize, message length + 1) UTF-16 units from the existing bounded native heap. Arithmetic uses size_t; requests beyond the real arena quota fail without publishing a pointer. Text and NUL exist before the returned allocation is published. Native caller buffers and pointer slots must be valid, as for the native CRT routines; this is not a kernel atomic-copy service or managed exception boundary.

The heap's existing external allocation registry now distinguishes C++ allocations from fixed local-message allocations. Both families share the existing lock, reservations, commitment rollback and per-page reference counts. LocalFree accepts only an exact live allocation of the matching family. It does not read metadata from a caller-controlled payload. Null succeeds without changing last-error; invalid/interior/non-live/wrong-family pointers return the original pointer and ERROR_INVALID_HANDLE. C++ invalid delete continues to fail fast. Successful frees preserve native last-error, and errno remains independent. This does not introduce movable local handles, LocalReAlloc or a general Windows heap API.

Allocations belong to the component, not to their allocating thread. They can be freed after that thread has exited. As with ordinary native pointer allocation, an address reused for a later live allocation is not a generation-bearing handle; no stale-pointer detection across address reuse is claimed.

## Optional Windows mechanisms

There is no Windows Event Log provider/service in this profile. RegisterEventSourceW returns null with ERROR_NOT_SUPPORTED; CoreLib EventReporter explicitly returns on that branch. DeregisterEventSource and ReportEventW reject nonexistent event-source handles without reading payloads or consuming ordinary console/event handles. They do not pretend to record or persist a log entry. Existing console/fatal diagnostics remain available.

IsDebuggerPresent returns false because the current kernel has no guest process debugger-attachment facility. Host QEMU debugging is not a guest process attachment. This is a declared platform capability, not a claim of managed debugging support. Exception/context services remain unresolved separately.

## Evidence

Guest modes 76/77 use exact archived diagnostic and native heap objects. Tests cover all catalogue codes, expected text, direct/import equivalence, readonly slots, caller-buffer size boundaries, guard-page endpoints, current-module/system fallback, unknown code/language/flags, allocation minimum size, shared-page preservation, LocalFree null/interior/double/wrong-family/foreign-address errors, oversize allocation, real allocation-registry exhaustion with unchanged caller pointer and recovery, optional service failures, independent error/errno state and cross-thread allocation lifetime. Both load bases and no-compiler-TLS execution are covered. Supervisor checks enforce native thread lifecycle, no unintended console writes and full resource recovery.

Passed: Release build, runtime-audit/probe/target/source/readiness and four QEMU runtime profiles with 239 user groups and 54 expected contained faults each. Full source archive: 118 members; configuration archive: 35 exact objects. Minimal/broad strict link: 25/31 unresolved dependencies. RuntimeThreadFixture: 45056 bytes, 106 plain unwind records; the unchanged loader cap is 128. All 20 ordinary boot scenarios also passed. P1.6 still requires COM lifecycle, and no guest managed execution is claimed.

Logs are artifacts/p1-diagnostics-config.log, artifacts/p1-diagnostics-test.log, artifacts/p1-diagnostics-audit.log and artifacts/p1-diagnostics-probe.log. Source/object hashes and commands remain in runtime-source/runtime-config reports.

References: [FormatMessageW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-formatmessagew), [LocalFree](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-localfree), and pinned Interop.FormatMessage.cs/EventReporter.cs at b82454cad0aaaae3db2cf18fbf2cccc36e201ccc.
