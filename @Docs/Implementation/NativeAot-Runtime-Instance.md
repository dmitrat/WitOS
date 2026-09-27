# Upstream RuntimeInstance and ThreadStore creation

**Status:** Implemented in WitOS 0.0.29; ABI v13 and the compiler TLS layout are unchanged.
**Scope:** Native runtime object creation before collector initialization. No managed execution or thread attachment is claimed.

## What now executes

The dedicated runtime-config image runs PalInit, the already tested interface-dispatch initialization, InitializeGCEventLock, RestrictedCallouts::Initialize and RuntimeInstance::Initialize. The latter allocates the actual upstream RuntimeInstance and ThreadStore classes, runs their real constructors and publishes the upstream singleton only after both allocations succeed.

The selected native input is standalonegc-disabled.lib. Here disabled means dynamic selection/loading of a separate GC library is disabled: the built-in workstation collector remains required. Its upstream InitializeGCEventLock is genuinely empty; no replacement implementation was invented. The tool verifies this selection. RestrictedCallouts initialization creates its real Crst; registration/invocation of GC callbacks is outside this probe.

## Replacing the Windows TLS assumption

The pinned [ThreadStore source](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/threadstore.cpp) calls SaveCurrentThreadOffsetForDAC during construction. Its Windows implementation follows a TEB pointer and TLS vector. WitOS supplies a single compiler TLS page, not a complete Windows TEB.

The WitOS source overlay changes only this method's platform lookup:

- Query the entire current-thread record from the kernel; verify result size/version, a nonzero page-aligned compiler TLS address, arithmetic bounds and the supported single-module TLS index zero.
- Locate the actual upstream tls_CurrentThread symbol, then require its entire RuntimeThreadLocals storage to fit inside the current thread's compiler TLS data area.
- Publish the genuine _tls_index address and the checked module-relative TLS offset only after validation. Writable raw FS hints do not supply thread identity or bounds.
- Reject an unavailable compiler TLS page through native fail-fast before dereferencing compiler TLS.

WIT_COMPILER_TLS_DATA_OFFSET names the existing 256-byte prefix. The kernel allocation/copy code and adapter now share that value; no offset or syscall layout changes. These are useful metadata breadcrumbs, not support for Windows DAC/TEB debugger traversal.

The complete generated threadstore.witos.cpp goes into Runtime.WorkstationGC.lib. The unchanged Windows reference retains the original file. The source audit verifies 58 canonical-byte files, adding RuntimeInstance/ThreadStore headers, RestrictedCallouts files and the selected GC glue source. The upstream checkout remains clean, and the compiled adapted object is checked byte-for-byte in the archive.

## Probe boundary

The guest uses exact source slices of [RuntimeInstance](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/RuntimeInstance.cpp) constructors/destructor/accessors/Initialize/Destroy, ThreadStore creation/destruction and the adapted metadata method. It also includes unchanged Thread::IsInitialized and the two startup initialization methods. Real upstream headers define every runtime class and TLS layout.

The separate archive grows to eleven verified objects. Its excluded attachment, suspension, collector and exception methods are not replaced with successful stubs. Complete dependencies remain in the actual runtime archive. Startup source generation and its hashes are recorded separately in startup-provenance.json.

Creation is a serialized, one-time startup operation before worker publication. The upstream release initializer is not a general concurrent/reentrant factory. The successfully published instance and its empty ThreadStore live until component teardown in this milestone; an orderly managed shutdown is still pending.

## Validation

RuntimeInstanceStartup runs at both relocated image bases and checks:

1. Exhausted native allocation descriptors: first allocation fails, singleton/TLS metadata stay unpublished, and memory accounting is unchanged.
2. Exactly one free descriptor: RuntimeInstance allocation succeeds, ThreadStore allocation fails, and the real NewHolder/destructor path reclaims the unpublished instance without changing memory accounting.
3. Released pressure: initialization succeeds with the real image handle, nonnull ThreadStore and valid TLS offset even after writable raw-FS identity hints are cleared.
4. Three native workers observe the same RuntimeInstance and ThreadStore, but different actual per-thread RuntimeThreadLocals storage. Kernel-confirmed bounds and the module-relative offset match on every worker across yields. The upstream IsInitialized method confirms none of these records has been attached to the runtime.
5. Three joins/reaps, the expected two retained memory reservations from the combined dispatch/runtime startup, empty kernel handle/event tables, and complete physical-page recovery after component destruction.

ThreadStoreTlsPrerequisite uses the same image with its PE TLS directory deliberately omitted. It reaches the intended metadata method and fails with the expected native exit code, without a hardware fault or retained allocation.

The guest image is 35,328 bytes with 77 plain unwind records on the local compiler; it has no managed header or OS/CRT imports. runtime-config now requires thirteen dedicated groups and 175 total user groups per boot, preserving 51 contained hardware faults. Ordinary regression remains eighteen VM scenarios and 162 groups.

Release build, the 58-file audit, hosted NativeAOT, runtime-target/source, the final 128/512 MiB guest runs and all eighteen kernel regression scenarios passed locally. The full native archive retains 83 members and the strict port link retains 98 unresolved symbols; this milestone executes existing runtime internals rather than merely removing symbols.

## Remaining work

An empty ThreadStore is not an attached managed thread. RuntimeThreadShutdown/DetachCurrentThread still requires real GC allocation-context cleanup, so it has not been simulated. Full RhInitialize/InitDLL still needs explicit Windows startup/diagnostic handling, hardware exception integration and collector startup. Managed module registration, GC rendezvous, managed allocations and exceptions remain pending. Upstream CoreLib and the long-term unchanged-IL/CoreCLR application contract are preserved.
