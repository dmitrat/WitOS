# Working on WitOS

## Project direction

The core objective is standard upstream .NET above a minimal native kernel and a hardware-independent system layer. Shells and applications eventually run on that .NET platform. Current guest code includes the M1 foundation, M2 ring-3 isolation, sparse user memory, bounded user threads/TLS, events/deadlines, restricted native PE loading, user-space C bootstrap and a partial native GC OS adapter; host-side C# tooling must not be described as guest .NET support.

Original vision documents live in `@Docs/`. Concrete implementation status and deliberate limitations live in `@Docs/Implementation/`. Preserve original drafts unless the task calls for revising them.

## Boundaries

- Keep UEFI details in `src/Boot.Uefi/`.
- Keep x64 instructions, descriptor tables and QEMU-specific test mechanisms in `src/Kernel.Arch.X64/`.
- Keep the common kernel independent of firmware structure definitions.
- Use ordinary .NET for development tools.
- Do not add speculative public resource APIs, distributed services or GUI work to a boot milestone.
- The kernel now owns its page tables, but firmware memory remains reserved until reclamation is explicitly designed and tested.
- Treat mapping/physical-page operations as serialized bootstrap APIs. Current allocation and mapping setup runs with interrupts disabled.
- Context switching currently preserves baseline x87/SSE state; do not introduce AVX/XSAVE assumptions without extending state management and tests.
- Keep shared kernel mappings supervisor-only. Never reuse the kernel scratch branch as a user page-table branch.
- Validate user handles and complete buffer ranges before copying/output. Close handles and free only component-owned pages on teardown.
- Keep dynamic reservations separate from fixed image/stack mappings. Reservation must not consume backing RAM; failed commit must roll back additions; decommit retains the reservation and recommit zeroes pages.
- Reset must validate one entire committed dynamic range with interrupts disabled before zeroing owned backing. Preserve permissions, reservations and all commitment/accounting; never decommit/recommit to simulate reset. Reject unsupported unlock semantics before mutation.
- Keep memory-discovery snapshots consistent through IF-disabled validation and copy-out; reject the full destination before any write. Report fixed mappings and private tables as ownership costs, and retain no-access commitments in accounting.
- Track committed no-access pages as owned. Invalidate active translations before reuse, reclaim empty private page tables, and keep memory-call work bounded by prototype quotas.
- Validate user return addresses/selectors/flags and the selected thread's owning stacks before IRETQ. Timer returns must preserve condition codes; syscall flags follow the explicit ABI.
- Switch TSS.RSP0, raw FS TLS and optional compiler GS TLS with the selected user thread. Keep FSGSBASE disabled and restore zero FS/GS bases on kernel supervision and idle; the current kernel has no segment-based TLS.
- Validate static PE TLS before allocation; require real directory fixups and reject nonempty callbacks. Capture the relocated seed before publishing the component, preserve raw FS storage, and roll back/reap the extra compiler TLS page. Never treat static template copying as dynamic TLS initialization or runtime attachment.
- Detached creation returns no join capability. Keep its private generation-bearing identity live through user-space TLS cleanup, deny joins, and automatically reap only after entry to the exit syscall. Free user pages, never the currently executing fixed kernel stack; last-thread completion must restore kernel FS/GS/CR3.
- Publish join wait edges and wakeup results with interrupts disabled. Reject cycles before parking, preserve thread resources until exit/join/close, and roll back all partial thread creation.
- Serialize event state checks, parking and completion publication with interrupts disabled. Expire parked deadlines before later signal/close calls; completed waits must not follow a reused event handle.
- Distinguish join edges from event/sleep waits. Idle IRQs must resume their CPL0 frame without replacing saved user contexts; use the adjacent STI/HLT/CLI path with zero kernel FS base.
- Keep legacy delivered-PIT deadlines separate from the HPET monotonic domain. HPET is the explicit q35 profile: supervisor-only UC mapping, comparator IRQs disabled, no reset after publication, and serialized reads with IF clear. Neither clock is UTC; PIT still limits wake scheduling.
- Keep the GC event pool component-private. Release its gate before parking, capture generation-bearing handles, and never dereference a reusable slot after a wait. Serialize lifecycle changes against upstream's plain IsValid read; finite waits use monotonic deadlines with upward rounding and saturation; process both deadline domains before later signal/close operations.
- Keep minipal mutex ownership tied to kernel-provided generation-bearing thread identity, not writable TLS. Release the registry gate before parking; count signaled entrants until they acquire or re-register. Never destroy active locks or hide Crst initialization failure. Mutex lifecycle is externally serialized; contended locks share event quotas and owners must release before thread exit.
- Drive dynamic C++ TLS in user space after validating the whole readonly initializer table. Set the compiler guard before constructors, pop destructors before calls, fail fast on lost cleanup or unbounded re-registration, and use lifecycle-aware thread exit for orderly cleanup. Raw exits/faults remain abrupt; do not claim ThreadStore attachment or managed TLS from this layer.
- Keep native heap metadata outside payloads; serialize allocation/free, publish only after successful commit, preserve shared pages with reference counts and release the last reservation. Never use fake throwing-new/CRT implementations to close the link boundary.
- Keep PAL memory within owned dynamic reservations, roll back reserve/commit failures, and reject unsupported executable/guard/copy-on-write modes. Preserve generation-bearing event waits and monotonic deadlines; alertable/multi-object waits remain unsupported. Report actual yield switching and never hide invalid void frees.
- Native last-error is a caller-writable 32-bit per-thread word in raw FS storage, independent of compiler TLS and kernel status/authority. Keep direct/import bindings equivalent and readonly, preserve prior error on success/normal timeout, and preserve the original failure through rollback. Generate the assembly offset from the shared ABI header.
- Current-thread discovery must use kernel thread/process records, with whole-buffer validation and IF-disabled copy-out. Writable FS/GS data must not define identity or stack bounds. Preserve the explicit incomplete PAL boundary; full ThreadStore detach requires real GC allocation-context cleanup.
- Keep source-built Windows reference and WitOS-overlay native archives in separate build/install directories. Verify the pinned Git revision and clean source tree, and preserve strict incomplete-port linking; a source-built archive is not a runnable guest runtime.
- Calculate source pins from canonical upstream download bytes, not CRLF-converted Git worktree copies. Keep exact-byte verification; do not normalize away a hash mismatch in the audit.
- Compile the NativeAOT adapter against hash-verified upstream headers. Keep unsupported runtime methods unresolved; Windows SDK declarations are not permission to link Windows implementations into the guest.
- Keep NativeAOT Static/Shared intermediate and output paths separate; their same-named libraries are different artifacts. Preserve strict missing-symbol link checks without dummy runtime/OS implementations or forced linking.
- Validate immutable, kernel-owned PE bytes before allocation. Keep parsing in the common kernel and page-table work in the architecture layer; unsupported directories must fail explicitly.
- Publish a PE component only after all sections, fixups and thread state exist. Preserve RX/RO/RW separation, keep image gaps unmapped, and roll back the entire unpublished component on allocation failure.
- Publish the single-image user-space context only from the validated immutable startup descriptor, before native TLS constructors/workers. Module lookup accepts actual headers/declared sections including BSS, not padding, stacks or dynamic allocations. PAL bounds use the inclusive upstream upper endpoint and fail fast before any output on invalid void calls; this is not a loader or managed module registry.
- Publish native environment state only after validating the complete readonly image table and all terminated strings, before TLS constructors/workers. Preserve immutable lookup, native last-error and equivalent direct/import bindings. UTF conversion must use the real native heap with bounded input and caller-owned cleanup; environment transport is not GCConfig or PalInit execution.
- Keep configuration probes tied to hash-verified upstream method bodies and actual headers; record every compiled-source correction. Preserve full runtime dependencies outside the probe. RhConfig string failures must retain caller output and release temporary buffers. Native errno is separate static compiler TLS state, not raw-FS last-error; locale support remains explicitly C-only.
- Keep native initializer execution in user space. Kernel image descriptors are readonly; ReadyToRun/TypeManager/GC-table initialization belongs to the actual runtime, not the kernel.
- Accepted plain unwind metadata is structural evidence only. Reject handler/chained forms and relocation into validated unwind ranges; do not claim stack unwinding or managed exception support from parsing tables.
- Treat the boot handoff and experimental user ABI as evolving contracts, not a frozen public SDK.

## Validation

For native, boot, image or runner changes, run:

```powershell
dotnet build WitOS.slnx --configuration Release
dotnet run --project tools/WitOS.Dev --configuration Release -- test
```

Use `setup` once for the pinned local QEMU package. The test tool must distinguish successful boot, panic, unexpected exit and timeout. Never infer a passing boot from an exit code or log line alone.

For runtime experiment or source-pin changes, run `runtime-audit`, `runtime-probe` and `runtime-target` through the same tool. Their results are hosted Windows evidence, including native C-host bootstrap, never proof that .NET runs in the guest. Preserve the source/package pins and explicit profile limitations unless the task deliberately updates them. `runtime-port` builds and boots the native GC memory adapter in QEMU; it is guest adapter evidence, not managed runtime execution. For full native source-build or overlay changes, also run `runtime-source`: it builds the upstream native libraries, executes the Windows reference and checks unresolved WitOS port requirements.

Keep generated images, binaries, firmware, downloads, logs and credentials out of Git. They belong in ignored `artifacts/` or `.tools/`. Record actual results and limitations in documentation; do not mark a future milestone complete based on M0 boot.
