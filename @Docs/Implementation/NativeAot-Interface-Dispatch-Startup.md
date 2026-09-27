# Upstream allocation and interface-dispatch startup

**Status:** Implemented in WitOS 0.0.28; ABI v13 unchanged.
**Scope:** Real upstream AllocHeap and the first initialization call inside InitDLL. This is native runtime infrastructure, not managed interface dispatch or a running collector.

## Executed code and provenance

The runtime-config guest probe now executes InterfaceDispatch_Initialize, InterfaceDispatch_InitializePal, InterfaceDispatch_AllocDoublePointerAligned and InterfaceDispatch_AllocPointerAligned from the pinned .NET 10.0.8 source. Their method bodies are unchanged. The actual NativeAOT build profile enables FEATURE_CACHED_INTERFACE_DISPATCH.

The complete [AllocHeap source](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/allocheap.cpp) is compiled into both the WitOS runtime archive and the guest probe. It uses the real WitOS nothrow heap, PAL memory operations, Crst and minipal mutex implementation. There is no replacement collector or custom managed CoreLib.

Four new canonical-byte SHA-256 pins cover allocheap.cpp, allocheap.h, [CachedInterfaceDispatch.cpp](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/runtime/CachedInterfaceDispatch.cpp) and [CachedInterfaceDispatch_Aot.cpp](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/CachedInterfaceDispatch_Aot.cpp). The source audit now verifies 52 files; the runtime/compiler/package revisions are unchanged.

The dedicated probe extracts the initialization function plus its exact static lock declaration and the AOT source prefix containing the allocator pointer and three methods. These use the real upstream headers and native compile profile. Complete dispatch sources retain references to assembly helpers for actual dispatch, and could not be linked into this narrow probe without those dependencies. They remain complete in Runtime.WorkstationGC; no helper stubs or forced links were added. The separate probe archive now has nine exact, byte-verified objects.

## AllocHeap lifecycle correction

The pinned AllocHeap destructor frees its block list but does not destroy m_lock. Crst has no destructor that does this implicitly. WitOS has a bounded mutex registry, so repeated heap lifetimes would retain registry slots.

The WitOS generated copy adds exactly one operation, m_lock.Destroy(), after releasing all blocks. The source correction uses a unique checked anchor, preserves the upstream license, records its provenance and is applied to the actual runtime archive as well as the probe. The Windows reference and upstream checkout remain unchanged. Destruction requires no concurrent users of the heap.

## Guest checks

RuntimeAllocHeap verifies:

- Forty complete heap lifetimes, exceeding the sixteen-slot mutex registry capacity; each returns owned/committed/reserved memory and page-table accounting to its prior snapshot.
- Small and multi-page allocations, 16-byte alignment, zeroed fresh pages, content preservation and Contains checks.
- Exhaustion of all 128 native allocation descriptors. A real PAL allocation then succeeds but BlockListElem allocation fails; the unpublished block must be released. Releasing the pressure allows the same heap to allocate again.
- Three native workers allocating from one shared heap, with yields, distinct allocation addresses, correct contents, six total joined/reaped workers across the combined configuration scenario, and complete heap/mutex cleanup.

InterfaceDispatchInit executes in a fresh image at both relocation bases. With the native heap exhausted, the real initialization returns false without changing memory accounting. After pressure is released, retry succeeds and the upstream pointer/double-pointer aligned allocators return writable, distinct, zero-initialized storage. Their heap and two locks intentionally live until process teardown; the supervisor verifies the two retained reservations and complete physical-page recovery after destruction.

The probe covers AllocHeap's default Init() path and controlled positive allocation sizes. The overload using externally supplied initial memory, partial reserve/commit semantics, general invalid-alignment/overflow inputs, actual interface-call cache lookup/update and assembly dispatch are not validated by this milestone.

## Validation and limits

The guest configuration/startup image is 32,768 bytes with 66 plain unwind entries on the local compiler. It has no OS/CRT imports or managed header. The two new required groups bring runtime-config to eleven dedicated groups, 173 user groups per boot, retaining 51 contained hardware user faults. Ordinary kernel regression remains eighteen scenarios and 162 user groups.

The native source build retains 83 Workstation archive members, eleven minipal members and 98 strict-link unresolved symbols. The unchanged symbol count is expected: this milestone executes an existing upstream subsystem on the implemented adapters. It is not a missing-symbol percentage measure. Reports verify the generated AllocHeap object and reject the original object in the WitOS profile.

Local checks: Release build, 52-file runtime audit, hosted NativeAOT, runtime-target/source, and runtime-config at 128/512 MiB passed. All eighteen kernel regression scenarios also passed.

RhInitialize/InitDLL as a whole, RuntimeInstance, ThreadStore attachment, GC initialization, managed allocations and managed interface dispatch remain pending. The source investigation also found that NativeAOT's CMake forcibly enables tracing features for non-Wasm targets; passing a parent CMake flag alone would not establish a reduced diagnostic profile. No such profile change is claimed.

## Next

Continue through InitDLL's GC-event lock and restricted-callout initialization, and RuntimeInstance/ThreadStore creation. ThreadStore's SaveCurrentThreadOffsetForDAC assumes a Windows TEB; the current compiler-TLS vector is not a general TEB implementation. Address that explicitly before claiming RuntimeInstance initialization, then proceed to real collector startup and exception/GC coordination.
