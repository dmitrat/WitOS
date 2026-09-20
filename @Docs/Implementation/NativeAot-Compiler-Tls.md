# ADR 0014: Static compiler TLS for one PE module

**Status:** Implemented in WitOS 0.0.17; user ABI remains v9.
**Date:** 2026-09-20.
**Scope:** A separately compiled MSVC static TLS image executes in ring 3. This is not dynamic C++ TLS initialization, NativeAOT ThreadStore attachment, managed ThreadStatic or guest .NET execution.

## Source contract and decision

The [Microsoft PE specification](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format#the-tls-section) defines the TLS template, VA-valued directory fields, module-index storage, zero-fill and optional callbacks. The pinned .NET 10.0.8 `src/coreclr/nativeaot/Runtime/amd64/InteropThunksHelpers.asm` uses `_tls_index`, `GS:[0x58]` and a module vector for static TLS access. Local MSVC disassembly of `compiler_tls_access.obj` confirms this addressing for actual `__declspec(thread)` variables in a separate translation unit.

Implement that bounded static data contract alongside the existing raw FS page. Do not route arbitrary Win32/FLS APIs to fake implementations. TLS callbacks are executable user-space lifecycle work and remain explicitly rejected until a real dispatcher exists.

## Accepted PE profile

The existing section/protection/relocation validator additionally accepts a 40-byte PE32+ TLS directory in initialized read-only, non-executable image data. It checks:

- Template start/end and index VAs are within the image; lengths cannot overflow.
- Template bytes are initialized readable, non-executable data. Initialized bytes plus zero-fill fit 3,840 bytes.
- Index storage is aligned, writable and non-executable (0.0.18 also accepts zero-filled BSS) and does not overlap the template.
- The directory cannot overlap the template. Reserved characteristics are zero; requested alignment is at most 256 bytes.
- Callback pointer is null or points to an aligned, initialized read-only empty callback array. Nonempty callbacks are unsupported, not silently skipped.
- For relocatable images, every nonnull directory VA has its exact DIR64 fixup. Relocations cannot alter validated size/flags, the index storage or the empty callback entry. The existing internal-pointer and unwind protections still apply.

One module gets index zero; the loader writes the validated index location. A fixed image without relocations retains the existing preferred-base-only restriction. DLLs, imports, dynamic module registration and handler/chained unwind remain outside this profile.

## Template ownership and thread storage

Before publishing the component, the kernel captures the already-relocated initial template into bounded process-owned kernel storage and appends zero-fill. Future threads use that immutable seed, even if the mapped image's template is later changed. Template copying and thread preparation remain serialized with interrupts disabled.

TLS-enabled threads receive one additional RW/NX page immediately after their raw FS page. The GS page contains a pointer at offset 0x58 to the module vector at 0x80; entry zero points to data at 0x100. The data address is aligned to 256 bytes. This is the minimum static compiler contract, not a full Windows TEB or FLS implementation. The original FS page and its offsets remain unchanged.

Creation allocates four stack pages, the raw FS page and then the compiler TLS page. Any allocation failure closes the temporary handle and rolls back the unpublished thread's mappings. Join/close reaping releases both TLS pages; slot reuse starts from the original template and zero-fill. Sibling threads still share an address space: separate TLS storage is not a security boundary between those siblings.

The x64 context path installs both FS and GS for the selected thread on launch, scheduling and syscall return. Idle and component exit/fault reset both bases to zero; kernel return/idle assertions verify this. FSGSBASE remains disabled. Kernel C does not dereference user GS data, and writable module-vector pointers do not grant supervisor access.

Thread preparation moved to `user_thread.c` to keep the user dispatcher below the repository's file-size limit. No new syscall, startup field or image-descriptor field was introduced.

## Guest evidence

The fixture uses two independently compiled C files with initialized scalar, zero-initialized scalar and relocated image-pointer TLS variables. It defines only its own static `_tls_used`/`_tls_index` metadata and imports no Windows/CRT implementation.

Four required groups cover:

- `CompilerTlsValidation`: malformed directory size/address/protection, invalid template/index ranges, excessive size/alignment, reserved flags, nonempty callbacks and missing/invalid TLS relocations. Rejection must allocate nothing.
- `CompilerTlsRollback`: physical failure at every image-allocation stage and quota failure at each of six child-thread allocations, including the final compiler TLS page. Handles, mappings and free counts must recover.
- `CompilerTlsThreads`: relocated load at both guest bases; initial bytes, zero-fill and pointer relocation; separate TLS values in two timer-preempted children; join/reap/slot reuse; parent TLS preserved; GS restoration after the only runnable thread sleeps through kernel idle. The supervisor corrupts the mapped template after capture to prove that child initialization uses the retained seed. MSVC places that template in read-only .rdata, so the test uses the supervisor alias for this injection.
- `CompilerTlsIsolation`: actual NX fault on TLS data and supervisor-access fault after a forged GS vector, followed by a fresh successful component.

The runner requires 125 user groups and 40 contained user faults in successful boots. Reports include `compiler-tls-build.json`, `compiler-tls-disassembly.log`, `TlsFixture.pe` and serial/outcome logs. Kernel CI uploads these TLS artifacts alongside the existing boot evidence. The local fixture is 4,608 bytes with five plain unwind records; sizes may vary with the native compiler.

Release build, separate runtime-port guest execution, 35-file runtime audit, hosted runtime probe and source-build/runtime-target references passed. The complete VM suite passed all 18 scenarios, including both RAM profiles and the expected timeout, with 125 required user groups and 40 contained user faults in successful boots.

## Remaining runtime boundary

The broad source link remains at 136 unresolved symbols. `_tls_index` and dynamic TLS helper dependencies still appear there because that diagnostic workload has no complete WitOS runtime bootstrap/metadata object. The fixture's definitions are not injected into the runtime link.

The next slice must implement user-space dynamic initialization/termination and runtime thread attachment. The pinned runtime includes dynamically initialized `thread_local ee_alloc_context::PerThreadRandom` and the Windows PAL calls `RuntimeThreadShutdown` from FLS teardown; neither lifecycle is satisfied merely by static template copying. Keep unsupported callback-bearing images rejected until those behaviors have tested implementations. GC rendezvous, exceptions and managed module initialization remain separate blockers; see [the M3 plan](M3-Runtime-Integration-Plan.md).

Version 0.0.18 adds the [user-space dynamic C++ TLS lifecycle](NativeAot-Dynamic-Tls.md), with an empty PE callback list. Real ThreadStore attachment remains pending.
