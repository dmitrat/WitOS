# M2 — First isolated native execution

Historical snapshot: initial isolation with extensions through 0.0.14 / ABI v8. Current status, including ABI v14 and later PAL adapters, is in [Next-Steps](Next-Steps.md); authoritative call constants remain in user_abi.h.
Guest version: WitOS 0.0.14 (latest addition: native runtime mutexes and thread identity; ABI v8).

This is the first M2 isolation slice, not a general process platform or a .NET runtime port.

## Observable result

A separately assembled/linked native component runs at CPL3 under its own CR3, writes through a granted console handle and exits. Faulting components stop without panicking the kernel. After each tested failure the kernel successfully runs a new normal component.

The existing M1 kernel-worker demonstration remains intact.

## Controlled user image

`tests/User.X64/entry.asm` is linked separately as `UserFixture.pe`. The host tool validates its x64 native subsystem, fixed entry/base, RX code, lack of imports/relocations and one-page code bound, then embeds its code bytes into the boot image.

The guest copies these bytes into a newly allocated user code page. It never calls that code in ring 0.

This original path remains a known build-time fixture, not a general loader. Version 0.0.8 separately embeds complete PE files for the [bounded guest PE loading path](M2-Pe-Image-Loading.md), which performs its own guest validation. Neither fixture layout is a public application format.

## Address spaces and ownership

Each of two available component slots has:

- a separately allocated PML4 and private user page-table branch;
- private RX code, read-only startup data and two RW/NX data pages;
- up to four user threads, each with four RW/NX stack pages and one raw TLS page;
- unmapped stack boundaries;
- a dedicated, guarded 64 KiB kernel stack per thread from a fixed kernel-owned pool;
- an owned-page list and a process-local handle table.

Only PML4 slot 0 is shared with the kernel, retaining supervisor-only permission. Kernel scratch mappings are not copied. The fixed image occupies a separate 2 MiB arena starting at 512 GiB. Dynamic reservations use a private 64 GiB arena starting at 1 TiB; see [memory semantics](M2-User-Memory.md). No low user mappings are provided.

Every allocated page is zeroed before user exposure. User leaf pages and private page tables are freed after switching back to the kernel CR3. Kernel mappings and fixed kernel-stack pools remain kernel-owned. Repeated execution restores the physical-page count.

Two address spaces can coexist. This slice activates one user component at a time; it does not round-robin multiple user processes.

## CPU boundary

GDT selectors 0x33/0x2B provide user code/data. IRETQ enters ring 3 with IF set and IOPL clear. Interrupts and INT 0x80 use TSS.RSP0 to enter the component's kernel stack.

The launch wrapper saves the supervising kernel's nonvolatile GPRs, x87/SSE state and CR3. Termination restores them and returns to the supervisor with interrupts disabled.

Initial user GPR/SIMD state is cleared. The main thread receives the startup pointer in RCX; created threads receive their supplied argument. Before any return to user execution, the kernel validates CS, SS, executable RIP and the user stack address. Invalid/noncanonical return state terminates the component before IRETQ.

Syscalls reset flags according to the experimental ABI. Timer returns preserve arithmetic flags and DF while removing unsupported/unsafe flag state. Losing condition codes during a timer return is covered by the long-running user-state test.

## Experimental user ABI v8

Authoritative constants and startup prefix: `src/Kernel/include/witos/user_abi.h`. The host generates matching MASM constants from the C headers.

Transport: `INT 0x80`.

- Input: RAX call number; RCX, RDX and R8 arguments.
- Output: RAX status; RDX result.
- Other GPRs and baseline x87/SSE state are preserved across returning syscalls.
- Syscall return flags are 0x202; flags are not a preserved syscall result.
- Public startup prefix: version, byte size, console handle and readonly ImageInfo pointer (24 bytes). ImageInfo is zero for raw fixtures; PE images receive their actual mapped ranges and plain unwind-table location.

| Call | Arguments | Result |
| --- | --- | --- |
| 0 Query | None | ABI version |
| 1 Write | Console handle, user pointer, byte count | Bytes written |
| 2 Exit | Exit status | Does not return |
| 3 Close | Handle | Success or invalid handle |
| 4 Memory reserve | Size, alignment | Reservation base |
| 5 Memory commit | Address, size, protection | Zero |
| 6 Memory decommit | Address, size | Zero |
| 7 Memory protect | Address, size, protection | Zero |
| 8 Memory release | Exact reservation base | Zero |
| 9 Thread create | Entry, argument, zero flags | Thread handle |
| 10 Thread yield | None | Zero |
| 11 Thread exit | Exit code | Does not return to this thread |
| 12 Thread join | Thread handle | Exit code; consumes handle |
| 13 Clock read | None | Delivered tick count |
| 14 Clock frequency | None | Nominal ticks/second |
| 15 Thread sleep | Absolute deadline | Zero |
| 16 Event create | Flags | Event handle |
| 17 Event set | Event handle | Zero |
| 18 Event reset | Event handle | Zero |
| 19 Event wait | Event handle, absolute deadline | Zero |
| 20 Memory query | Writable buffer, exact byte size, snapshot version | 96 bytes copied |
| 21 Monotonic read | None | Monotonic counter |
| 22 Monotonic frequency | None | Counts per second |
| 23 Sleep until | Monotonic absolute deadline, zero, zero | Zero |
| 24 Event wait until | Handle, monotonic absolute deadline, zero | Zero |
| 25 Thread current | Zero, zero, zero | Existing current-thread handle; no allocation |

Thread/TLS lifetime and blocking behavior are specified in the [thread decision](M2-User-Threads-and-Tls.md).

Statuses: 0 success, 1 unsupported call, 2 invalid handle, 3 denied rights, 4 invalid address, 5 excessive length, 6 invalid argument, 7 wrong object type, 8 resource exhaustion, 9 range not reserved by this component, 10 range not fully committed, 11 join deadlock, 12 busy thread, 13 timed out, 14 closed event. Returning errors have a zero result. ThreadCurrent borrows the current generation-bearing token from kernel state, independent of writable TLS; normal handle lifetime and rights still apply. The version/startup field in this snapshot is 8; this replaces the earlier experimental fixture contracts.

Write accepts at most 256 input bytes per call. The diagnostic UART output adds a [USER] prefix and translates line endings; this is not a general file/Stream contract. A zero-length write validates the handle but does not dereference the pointer. Nonempty writes validate the entire range before copying or output. Copying uses verified physical translations through supervisor aliases, so a bad user pointer never becomes an unchecked kernel dereference. Cross-page buffers are tested.

All table/mapping work is serialized on one CPU with interrupts disabled at the call boundary. Memory query validates the whole writable range before copying a consistent [allocator snapshot](NativeAot-Gc-Discovery.md). These rules are not yet a concurrent copy-from/to-user contract.

## Handles

Handles contain an owner tag, generation and slot. Authority comes from a live entry in the current component's private table, its type and its rights; encoded identity alone does not authorize access.

The fixture receives a writable console handle. Test-only extensions also provide a read-only console handle, a self-type handle and foreign/stale values to exercise rejection paths.

Closing invalidates an entry and increments its generation. Exhausted generations are not reissued. Exit, fault, invalid return and budget expiry close all remaining handles. There is no transfer/delegation protocol yet.

## Faults and time budget

CPL3 faults record vector/error/address/selectors and terminate the current component. CPL0 faults retain the existing fatal kernel diagnostic path. Unhandled user faults are not yet translated into managed exceptions.

Only the syscall interrupt gate is callable from ring 3. Privileged CLI and port I/O are rejected by the CPU; user code cannot access QEMU's exit port.

The timer enforces ten delivered ticks per activation for the controlled test workload. Infinite loops and the condition-code/SIMD stress loop are stopped and followed by a successful new activation. This is a test execution budget, not calibrated CPU accounting or a real-time guarantee.

## Validation

```powershell
dotnet build WitOS.slnx --configuration Release
dotnet run --project tools/WitOS.Dev --configuration Release -- test
```

All 18 VM scenarios passed locally. At version 0.0.14, ordinary successful boots additionally required 113 user check groups, including:

- actual ring-3 execution and ABI/handle checks;
- two live private address spaces, foreign live handles and an inaccessible peer-only page;
- kernel read/write, CLI, port I/O, NX, stack guards, code/startup-data writes, null read and UD2 containment;
- rejection of a noncanonical stack before kernel-to-user return;
- timer termination and condition-code/GPR/SIMD preservation;
- actual physical-page reuse with zero-fill and stale-handle rejection;
- handle closure and physical-page accounting after teardown;
- eleven [memory groups](M2-User-Memory.md): sparse/private memory, quota rollback, invalid reservations, physical OOM, ring-3 lifecycle and six hardware access faults;
- ten [thread groups](M2-User-Threads-and-Tls.md): preemption/TLS, join/reuse, cycle rejection, capacity/creation failures and child-fault/exit cleanup;
- fourteen [wait groups](M2-Events-and-Deadlines.md): event state/rights, wakeup/close/deadline ordering, handoff, idle and resource limits;
- sixteen [image groups](M2-Pe-Image-Loading.md): guest PE validation, section mapping, relocations, zero-fill/private data, failure rollback and access faults;
- eleven [native bootstrap groups](M2-Native-Module-Bootstrap.md): C entry, image handoff, metadata validation, constructor/cleanup order, run-once behavior and failure containment;
- twenty-nine native runtime adapter groups: memory operations/protection plus [discovery, atomic snapshot copies and physical pressure](NativeAot-Gc-Discovery.md), seven [event/lifetime/yield groups](NativeAot-Gc-Events.md), five [time/deadline/isolation groups](NativeAot-Gc-Time.md), and seven [mutex/Crst/identity groups](NativeAot-Mutexes.md). A separate wait-model group checks clock-domain separation.

For faults the guest checks CPU error codes, CR2 where meaningful, CS/SS and kernel canaries. Every failure is followed by a normal component. The host requires the full M2 marker sequence and ring-3 fault selectors before accepting boot success.

## Limits and next work

Two component slots, one active component, four threads per component, one x64 CPU, legacy one-page fixtures and a bounded native PE profile, fixed startup/thread regions, eight dynamic reservations and at most 128 owned physical pages per component (including tables, user stacks and TLS). No general Windows/DLL loader, filesystem, IPC channels, transferable capabilities, compiler/managed TLS, multi-object waits or managed runtime exists yet.

Memory, thread/TLS/join, events/deadlines and bounded PE image loading are implemented. The native image/bootstrap contract is implemented; the [NativeAOT backend direction is selected and first memory adapter implemented](NativeAot-Gc-Memory-Port.md). The [full native source build](NativeAot-Source-Build.md) is now established; remaining platform adapters and scalable commitment limits are still required. The interrupt transport and fixed layout are experimental and can change with executable tests.
