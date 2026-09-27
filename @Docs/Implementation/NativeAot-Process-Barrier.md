# Process memory barriers for GC and PAL

**Status:** Implemented in WitOS 0.0.30, experimental ABI v14.
**Scope:** Process-wide data-memory ordering on the single online logical processor. This is not GC suspension or an SMP implementation.

## Contract and implementation

WIT_CALL_PROCESS_WRITE_BARRIER (28) accepts three zero arguments and returns status plus a zero result. Nonzero arguments return INVALID_ARGUMENT before the fence/counter operation. The call does not allocate resources, park, switch threads or modify native last-error/errno. It executes with interrupts disabled inside the normal validated user syscall path.

The x64 helper in Kernel.Arch.X64 executes MFENCE and returns. All guest threads currently execute on the bootstrap logical processor; no AP is brought online. Therefore a fence on that processor covers preceding memory operations from every guest thread that has run there. The online-processor constant is shared with memory/thread discovery. If this backend count is changed from one, the syscall returns UNSUPPORTED until a real remote-CPU protocol is implemented.

This reasoning follows the [Intel MFENCE definition](https://www.intel.com/content/dam/www/public/us/en/documents/manuals/64-ia-32-architectures-software-developer-vol-2b-manual.pdf): preceding loads/stores become globally visible before subsequent loads/stores. The upstream OS requirement concerns the processors executing the process's threads; [Windows documents an IPI-based implementation](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-flushprocesswritebuffers) for the multiprocessor case. A local fence alone would not satisfy that broader case. No CPU affinity, migration, hotplug or SMP support is implied here.

This is a data-memory barrier. Instruction-cache maintenance, JIT executable-page transitions, DMA coherency and managed safepoints remain separate requirements.

## NativeAOT bindings

GCToOSInterface::FlushProcessWriteBuffers and PalFlushProcessWriteBuffers both call the real kernel primitive, using unchanged upstream declarations. Successful calls preserve native last-error; neither method touches compiler TLS. Unsupported/failing void calls fail fast rather than report synthetic success; the PAL path records its normal native error mapping first.

The operation works before GC OS initialization and without compiler TLS, which avoids creating a circular dependency during runtime startup. Its implementation is in the actual source-built WitOS archive, not only in a test fixture. The Windows reference, upstream source pins and published CoreLib/compiler packages remain unchanged.

The full strict link now has 96 unresolved symbols: six GC environment methods, sixteen PAL methods, five intentionally excluded transport symbols and 69 other runtime/platform requirements. Both write-buffer methods must resolve. The deliberately incomplete small GC link test now requires exactly the missing GetCacheSizePerLogicalCpu dependency, preserving an explicit unsupported boundary.

## Validation

The ordinary PAL fixture checks the wrapper and raw syscall with and without compiler TLS, tests each nonzero argument separately, requires a zero result and preserved last-error, and checks exactly two accepted operations in the kernel-owned per-component counter. The counter resets on component creation and is not a new public ABI field.

The dedicated GC/PAL fixture:

- Executes both upstream bindings before native TLS initialization.
- Repeats both calls from three native workers, yielding between iterations while preserving each thread's last-error, errno and its own data.
- Requires exactly 50 completed kernel barriers: two initial calls plus 48 worker calls. Rejected arguments must not increment the counter.
- Verifies joins/reaps, unchanged memory accounting, empty handles/events and complete page recovery after teardown.
- Executes the early calls in a PE with the compiler TLS directory omitted and requires exactly two barriers without a fault.

Disassembly of the built kernel object confirmed the helper contains 0F AE F0 (MFENCE), followed by RET. The VM tests verify integration, ABI and lifecycle behavior; a uniprocessor stress run alone cannot prove multiprocessor ordering or distinguish every erroneous memory-order implementation. The ordering guarantee relies on the inspected instruction and the single-processor invariant.

runtime-config now requires fifteen dedicated groups, 177 total user groups and the existing 51 contained hardware faults per boot. Its local image is 36,352 bytes with 82 ordinary unwind records. Ordinary regression remains eighteen VM scenarios and 162 groups, with stronger PAL checks. Release build, 58-file source audit, hosted NativeAOT, source/target checks and the 128/512 MiB dedicated boots passed; all eighteen kernel regression scenarios also passed locally.

## Remaining boundary

A memory barrier does not stop a managed thread or enumerate its roots. ThreadStore attachment/detach, suspension/rendezvous, exception integration and actual collector startup remain pending. CPU cache discovery and other unsupported GC services remain unresolved. No managed .NET execution is claimed by this milestone.
