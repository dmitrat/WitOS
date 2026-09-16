# M2 — First isolated native execution

Status: implemented and locally verified on 2026-09-16.
Guest version: WitOS 0.0.5 (initial isolation in 0.0.4; memory extension in 0.0.5).

This is the first M2 isolation slice, not a general process platform or a .NET runtime port.

## Observable result

A separately assembled/linked native component runs at CPL3 under its own CR3, writes through a granted console handle and exits. Faulting components stop without panicking the kernel. After each tested failure the kernel successfully runs a new normal component.

The existing M1 kernel-worker demonstration remains intact.

## Controlled user image

`tests/User.X64/entry.asm` is linked separately as `UserFixture.pe`. The host tool validates its x64 native subsystem, fixed entry/base, RX code, lack of imports/relocations and one-page code bound, then embeds its code bytes into the boot image.

The guest copies these bytes into a newly allocated user code page. It never calls that code in ring 0.

This is a known build-time fixture, not a general untrusted PE loader. Its fixed layout and test extension are not a public application format.

## Address spaces and ownership

Each of two available component slots has:

- a separately allocated PML4 and private user page-table branch;
- private RX code, read-only startup data, two RW/NX data pages and four RW/NX user-stack pages;
- unmapped stack boundaries;
- a dedicated, guarded 64 KiB kernel stack from a fixed kernel-owned pool;
- an owned-page list and a process-local handle table.

Only PML4 slot 0 is shared with the kernel, retaining supervisor-only permission. Kernel scratch mappings are not copied. The fixed image occupies a separate 2 MiB arena starting at 512 GiB. Dynamic reservations use a private 64 GiB arena starting at 1 TiB; see [memory semantics](M2-User-Memory.md). No low user mappings are provided.

Every allocated page is zeroed before user exposure. User leaf pages and private page tables are freed after switching back to the kernel CR3. Kernel mappings and fixed kernel-stack pools remain kernel-owned. Repeated execution restores the physical-page count.

Two address spaces can coexist. This slice activates one user component at a time; it does not round-robin multiple user processes.

## CPU boundary

GDT selectors 0x33/0x2B provide user code/data. IRETQ enters ring 3 with IF set and IOPL clear. Interrupts and INT 0x80 use TSS.RSP0 to enter the component's kernel stack.

The launch wrapper saves the supervising kernel's nonvolatile GPRs, x87/SSE state and CR3. Termination restores them and returns to the supervisor with interrupts disabled.

Initial user GPR/SIMD state is cleared. The startup pointer is passed in RCX. Before any return to user execution, the kernel validates CS, SS, executable RIP and the user stack address. Invalid/noncanonical return state terminates the component before IRETQ.

Syscalls reset flags according to the experimental ABI. Timer returns preserve arithmetic flags and DF while removing unsupported/unsafe flag state. Losing condition codes during a timer return is covered by the long-running user-state test.

## Experimental user ABI v2

Authoritative constants and startup prefix: `src/Kernel/include/witos/user_abi.h`. The host generates matching MASM constants from the C headers.

Transport: `INT 0x80`.

- Input: RAX call number; RCX, RDX and R8 arguments.
- Output: RAX status; RDX result.
- Other GPRs and baseline x87/SSE state are preserved across returning syscalls.
- Syscall return flags are 0x202; flags are not a preserved syscall result.
- Public startup prefix: version, byte size and console handle (16 bytes).

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

Statuses: 0 success, 1 unsupported call, 2 invalid handle, 3 denied rights, 4 invalid address, 5 excessive length, 6 invalid argument, 7 wrong object type, 8 resource exhaustion, 9 range not reserved by this component, 10 range not fully committed. Returning errors have a zero result. The version/startup field is now 2; this replaces the experimental v1 fixture contract.

Write accepts at most 256 input bytes per call. The diagnostic UART output adds a [USER] prefix and translates line endings; this is not a general file/Stream contract. A zero-length write validates the handle but does not dereference the pointer. Nonempty writes validate the entire range before copying or output. Copying uses verified physical translations through supervisor aliases, so a bad user pointer never becomes an unchecked kernel dereference. Cross-page buffers are tested.

All table/mapping work is serialized on one CPU with interrupts disabled at the call boundary. These rules are not yet a concurrent copy-from-user contract.

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

All 17 VM scenarios passed locally. Ordinary successful boots now additionally require 32 M2 check groups, including:

- actual ring-3 execution and ABI/handle checks;
- two live private address spaces, foreign live handles and an inaccessible peer-only page;
- kernel read/write, CLI, port I/O, NX, stack guards, code/startup-data writes, null read and UD2 containment;
- rejection of a noncanonical stack before kernel-to-user return;
- timer termination and condition-code/GPR/SIMD preservation;
- actual physical-page reuse with zero-fill and stale-handle rejection;
- handle closure and physical-page accounting after teardown;
- eleven [memory groups](M2-User-Memory.md): sparse/private memory, quota rollback, invalid reservations, physical OOM, ring-3 lifecycle and six hardware access faults.

For faults the guest checks CPU error codes, CR2 where meaningful, CS/SS and kernel canaries. Every failure is followed by a normal component. The host requires the full M2 marker sequence and ring-3 fault selectors before accepting boot success.

## Limits and next work

Two fixed slots, one active user thread, one x64 CPU, known one-page code images, a fixed image layout, eight dynamic reservations and at most 128 owned physical pages per component (including tables and fixed mappings). No general executable loader, filesystem, IPC channels, transferable capabilities, per-thread TLS, blocking/waking, dynamic user-thread scheduling or managed runtime exists yet.

The memory primitive slice is implemented; next implement the thread/TLS/wait substrate in RFC 0015. A real NativeAOT memory adapter and scalable commitment limits are still required. The interrupt transport and fixed layout are experimental and can change with executable tests.
