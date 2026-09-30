# P1.6 remaining module, names and diagnostic contracts

This call-site note was written after UTF conversion. The module portion is now implemented and documented in [P1-Module-Identity](P1-Module-Identity.md); thread names are now implemented in [P1-Thread-Names](P1-Thread-Names.md); diagnostics are now implemented in [P1-Diagnostics](P1-Diagnostics.md); the selected apartment lifecycle is now implemented in [P1-Com-Lifecycle](P1-Com-Lifecycle.md); real runtime attachment/detachment remains P1.8/P3.

## Module identity and optional DLL lookup

Pinned `windows/PalCommon.cpp::PalGetModuleFileName` returns a borrowed UTF-16 module name and a character count (excluding NUL). Null module selects the executable. The direct CoreLib GetModuleFileNameW path copies a process path into a caller-owned buffer. At that checkpoint WitUserImageInfo v1 carried ranges/base/unwind metadata but no resource name. Image descriptor v2 now supplies a real named embedded-resource handoff; do not invent a host path or present an anonymous mapping as a filesystem-backed executable. The published string must remain immutable through native cleanup.

The remaining GetModuleHandleW/GetProcAddress relocations originate in `startup.cpp::RhInitialize`, which probes ntdll!RtlDllShutdownInProgress. That code already retains `RtlDllShutdownInProgressFallback` when lookup fails. The fallback compares the actual runtime thread against g_threadPerformingShutdown, set by upstream OnProcessExit. Thus absence of the optional DLL is a valid upstream path; a synthetic ntdll handle or a fake exported routine is unnecessary. Actual module lookup must recognize only the published executable; missing DLLs/exports should fail with appropriate native errors. Fallback correctness still requires real runtime attachment and ordered process shutdown (P1.8/P1.9/P3).

## Diagnostics and allocation (implemented; see P1-Diagnostics)

`Common/src/Interop/Windows/Kernel32/Interop.FormatMessage.cs::GetMessage` first requests FROM_SYSTEM | IGNORE_INSERTS | ARGUMENT_ARRAY into a 256-character stack buffer. It retries only ERROR_INSUFFICIENT_BUFFER using ALLOCATE_BUFFER, then calls Marshal.FreeHGlobal -> LocalFree in a finally block. The unsupported-message fallback is an actual managed hexadecimal error string.

Implement meaningful messages for WitOS-produced native error codes. Unknown messages must fail, not return invented Windows text. If an allocated buffer is supplied, it needs real bounded native heap ownership and matching LocalFree validation. The current private C++ heap has an external allocation registry; blindly casting arbitrary pointers to delete[] would not establish the LocalFree error contract. No new allocator or heap metadata in payloads is needed solely to close a linker symbol.

EventReporter is an optional Windows event-log path: its no-handle failure branch must be checked and retained. Do not produce fake log handles or pretend persistence. Debugger presence should report an actual supported runtime debugging facility; QEMU host debugging alone does not establish a managed/native guest debugger attachment.

## Thread names and apartments

PalSetCurrentThreadName/PalSetCurrentThreadNameW now use real bounded kernel-owned per-thread names, with Unicode conversion, lifecycle/reuse and failure-preservation tests. See P1-Thread-Names for accepted evidence and limitations.

`Thread.NativeAot.Windows.cs::InitializeCom` defaults to MTA. A negative HRESULT other than RPC_E_CHANGED_MODE throws; E_NOTIMPL specifically throws PlatformNotSupportedException. S_FALSE causes a balancing CoUninitialize. GetCurrentApartmentState also observes implicit MTA when other process threads have initialized COM. These contracts rule out a blanket successful CoInitializeEx stub and a per-thread boolean lacking process-wide lifecycle. PalInitComAndFlsSlot must separately connect the genuine runtime shutdown callback to native thread lifecycle. General COM activation/WinRT and STA message pumping are not supplied by the current kernel.

These findings are from pinned runtime b82454cad0aaaae3db2cf18fbf2cccc36e201ccc and the current readiness relocation report. The selected P1.6 implementations and their acceptance tests have now passed; see the linked implementation checkpoints. Full runtime integration remains separate.

COM lifecycle ordering evidence: pinned ThreadStore::DetachCurrentThread invokes the managed g_threadExitCallback before acquiring its lock and before GC allocation-context cleanup. RuntimeThreadShutdown calls that real detach path. Any apartment cleanup must remain available through the callback and must not replace or bypass GC detach. PalInitComAndFlsSlot initializes the finalizer as MTA before establishing the native shutdown notification; the private WitOS notification must preserve this ordering without a Windows FLS emulation.
