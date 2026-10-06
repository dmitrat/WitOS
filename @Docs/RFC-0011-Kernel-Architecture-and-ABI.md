# RFC 0011 — Kernel Architecture & ABI

Draft v3, 2026-10-06. Supersedes the v0.x drafts (the last was v0.13, the M2 user ABI). Applies
[ADR 0024](Implementation/ADR-0024-Three-Layers-and-Unix-Form-Runtime.md); written as plan step A2 of
[PLAN.md](../PLAN.md).

**Status.** Normative for phases K (nano-kernel) and S (system layer) of the plan. The ABI-1 call set of §7 is the
target of step K1; its numbers, structure sizes, rights bits and status values are assigned in code and stay unfrozen
until step K8, when ABI-1 1.0 is declared. ABI-2 (§9) is frozen at step N3. The implementation as it stands today
(user ABI v51, boot ABI v4) is described call by call in [ABI-Reference.md](Implementation/ABI-Reference.md), which a
host test keeps equal to the headers; §8 of this document maps each of its 70 calls to the target.

## 1. Objective and scope

WitOS has three layers and two interfaces (ADR 0024). This RFC fixes:

- what the nano-kernel (layer 1) is: its objects, its mechanisms and the hardware boundary inside it (§3, §4);
- **ABI-1**, the interface between the nano-kernel and the system layer: principles, calling convention, object and
  capability model, the families of calls, versioning (§5–§7, §10);
- **ABI-2**, the interface between the system layer and .NET: composition, audiences, process model, versioning (§9,
  §10);
- the disposition of every system call of the current kernel (§8);
- the boot contract and the root task handoff (§7.11).

It does not specify service protocols above channels (RFC 0006 §54 onward), the driver model (RFC 0012, future), the
storage architecture (RFC 0008 and the storage document) or the runtime port (RFC 0015, revised in step A3).

## 2. Terms

| Term | Meaning |
| --- | --- |
| Mechanism, policy | RFC 0001 §8 and RFC 0007 §12. A mechanism is an operation whose meaning depends on no name, format or identity beyond the capabilities the caller presents. |
| Process | An address space, a handle table, threads and quotas: the kernel's execution container. Today the kernel calls it a component and has two slots. The application of RFC 0003 §8 is a layer-3 notion built from processes. |
| Thread | An execution context the kernel schedules: a saved register frame, a kernel stack, a state. |
| Object | Kernel-owned state reached through a handle: process, thread, event, memory object, channel endpoint, device, interrupt, DMA pin. |
| Handle, capability | A process-local, generation-bearing token with a rights mask (RFC 0004 §136). Possession is authority (RFC 0002 §7); there is no ambient authority. |
| Root task | The first process. The kernel starts it from the boot package and gives it the initial capabilities (RFC 0002 §63–64). |
| System layer | Layer 2: the root task, loader, process manager, substrate and service clients, in user mode, compiled per ISA. |
| Substrate | The POSIX-shaped part of layer 2: libc, pthreads, libunwind, libc++abi/libc++, `ld.so`. Private to the system layer and the runtime binaries (§9.2). |
| UHI | The Universal Hardware Interface of RFC 0007: `Kernel.Arch.*` and `Kernel.Platform.*` behind `wit_arch_*` and `wit_platform_*`. |

## 3. Layers

```
Layer 3  upstream .NET unchanged: CoreCLR/JIT, CoreLib, BCL, hosts (dotnet, hostfxr, hostpolicy)
         WitOS services in .NET (NativeAOT): device manager, drivers, storage, network, shell
         ───────────── ABI-2: libc + C++ runtime + WitOS capability API (libwitos); stable for years ─────────────
Layer 2  system layer (user mode, one source, compiled per ISA): root task, ELF loader and ld.so,
         process manager, substrate (libc, pthreads, libunwind, libc++, System.Native glue), service clients
         ───────────── ABI-1: about 50 system calls, mechanisms only; versioned ─────────────────────────────────
Layer 1  nano-kernel: address spaces and memory objects, threads and contexts, events, clocks, fault delivery,
         channels, capabilities, devices (MMIO, interrupts, DMA), processors
         └─ UHI = Kernel.Arch.<isa> + Kernel.Platform.<board>: the hardware personality; candidate for firmware or a chip
```

**Layer 1 knows:** physical memory, address spaces, mappings, threads and their saved frames, events, deadlines and
clocks, hardware faults, channels and messages, capabilities, device memory, interrupts, DMA pins, processors.

**Layer 1 does not know:** image formats (ELF, PE), files, paths, directories, names of threads or processes,
environment variables, the current directory, libraries and symbols, exception formats of languages, services and
protocols, users. Every one of these is a layer-2 or layer-3 concept (RFC 0001 §8).

**Layer 2 owns:** the root task; the loader (static ELF in the root task first, then `ld.so`); the process manager
(creation, initial capabilities, the initial environment and directory, exit reporting); the substrate; the clients
of services (namespace, console) over channels. One source for every platform; recompiled per ISA (goal 3 of ADR
0024).

**Layer 3 is:** upstream .NET without changes, and WitOS's own services written in .NET above it. Applications see
.NET and the `OutWit.OS.*` capability APIs; `OperatingSystem.IsOSPlatform("WitOS")` is true and neither `IsWindows()`
nor `IsLinux()` is.

## 4. Kernel structure

### 4.1 Code layers

The kernel image links layers listed in `build/kernel-<isa>.json`; each kernel C source belongs to exactly one layer
(`build/layers/*.json`).

| Layer | Content today | After step K8 |
| --- | --- | --- |
| `kernel-boot` | kernel entry, boot contract validation, physical page allocator, console formatting | unchanged |
| `kernel-foundation` | boot package parser, storage, ChaCha20 generator | generator; the package is handed to the root task as a memory object and is no longer parsed |
| `kernel-common` | handles, events, user address spaces, threads, waits, APCs, references, contexts, stack leases, exceptions, code memory, PE loader, libraries, files, process state, thread names | handles, memory objects, address spaces, threads, waits, activations, contexts, faults, channels, devices, processes |
| `kernel-arch-<isa>` | `witos/arch.h`: descriptor tables or exception vectors, frames and contexts, page tables, code publication, CPUID or cache | plus secondary processor start, IPIs and remote TLB invalidation (K7) |
| `kernel-platform-<board>` | `witos/platform.h`: console, interrupt controller, tick timer, monotonic clock, test exit | plus device region enumeration and interrupt claim, bind, acknowledge (K3) and the UTC source (K6) |
| self-test layers | tests under `WITOS_SELFTEST`, never in the release kernel | unchanged |

### 4.2 The UHI boundary

`witos/arch.h` declares 64 functions in eight groups (identity and bring-up, mode switching, thread frames, contexts,
faults, address spaces, code publication, processor services) and `witos/platform.h` ten board functions (console
byte, boot devices, tick timer, interrupt claim, monotonic clock, test exit). The common kernel learns the
architecture only from `wit_arch_identity()` and never names a register, selector, instruction or device address;
boards whose interrupts share one vector claim and complete them through `wit_platform_interrupt_claim`, and the
architecture never programs the interrupt controller.

Phase K extends the UHI, never the kernel's knowledge of devices:

| Step | UHI addition | What the kernel does with it |
| --- | --- | --- |
| K3 | enumeration of device regions and interrupt lines (PCI and ECAM on q35, device tree on virt); interrupt bind, mask, acknowledge | publishes a read-only descriptor table to the root task; mints device capabilities on request (§7.7) |
| K6 | UTC source (RTC on q35, PL031 on virt) | one read at boot, then UTC = boot value + monotonic delta (§7.10) |
| K7 | secondary processor start (MADT, PSCI), IPI, remote TLB invalidation | per-CPU state, process-wide barrier by IPI, TLB shootdown (§7.9) |

The UHI is the part of layer 1 that may move into firmware or a dedicated chip (ADR 0024, open decision 1). The
kernel's policy above it (which thread runs, what a system call means, how a fault is handled) stays software.

### 4.3 Invariants kept from the current kernel

- A call validates its whole input, including every byte of a destination buffer, before it changes any state or
  writes any byte; a failed call changes nothing.
- An object is published only after all of its resources exist; a failed creation rolls back completely.
- Until K7 every call runs on the one online processor with interrupts disabled and is not preempted; blocking calls
  park the thread after complete validation. K7 replaces the single-CPU invariant with explicit per-CPU state and
  cross-CPU fencing, not with a big lock pretending to be one.
- Deadlines are absolute values of the monotonic clock; zero polls, all ones waits forever.
- Code pages are published (`wit_arch_publish_code_page`, `wit_arch_publish_code`) before they may execute.
- Quotas live in `src/Kernel/include/witos/limits.h` only, and after K8 that file holds mechanism quotas only
  (handles, threads, pages, reservations, message bytes and handles per message, queue depth, pinned pages,
  interrupts) at values derived from real limits, not from fixtures (K5).

## 5. ABI-1 principles

1. **Mechanisms only.** A call enters ABI-1 when no composition of existing calls provides the mechanism and a
   layer-2 consumer with a test needs it. A call leaves when its meaning depends on a name, a format or a lifecycle
   that layer 2 can own.
2. **No ambient authority.** Every operation on an object requires a handle with the needed right. Operations on the
   caller's own address space, threads and process use the pseudo-handles `PROCESS_SELF` and `THREAD_SELF`; no
   handle of another process is reachable except through a channel.
3. **Handles are capabilities.** A handle is unforgeable (its generation changes on every reuse of a slot), process
   local, attenuable (`HANDLE_DUPLICATE` with a subset of the rights), transferable over a channel when it carries the
   `TRANSFER` right (moved, never copied, RFC 0006 §35), and closable. Closing a handle never terminates the object's
   activity: a thread or process runs on without a handle to it. The exception is a channel endpoint: closing its
   last handle closes the endpoint and the peer observes `PEER_CLOSED`.
4. **One wait.** Every object that has a state to wait for (event, thread, process, channel endpoint, interrupt) is
   waited for through `OBJECT_WAIT`, with one ordering and one deadline domain.
5. **Explicit failure.** An unimplemented mode returns `UNSUPPORTED`; the kernel never pretends success and never
   applies an invalid request in part.
6. **Versioned structures.** A structure passed by pointer starts with `Version` and `Size`; an unknown version is
   `UNSUPPORTED`, a wrong size `INVALID_ARGUMENT`; `Reserved` fields are zero; new fields are appended.
7. **Per-ISA parts are explicit.** Only the register block of a thread context and the register assignment of the
   calling convention differ between ISAs. Everything else is identical, and layer 2 is recompiled, not rewritten.
8. **Narrow.** About 50 calls (§7.12). Composite calls with an `Operation` field are allowed when the operations share
   one object and one request structure.
9. **Kernel diagnostics use kernel data.** A fault or an exit is reported on the kernel log with identifiers, the
   program counter, the vector and the address; names belong to layer 2.
10. **The kernel and the system layer of one OS image are built together,** yet the version is checked at run time
    (§10.1) so that a mismatch fails at startup and not in a call.

## 6. Calling convention, statuses and rights

### 6.1 Transport

| ISA | Instruction | Number | Arguments | Status | Result | After return |
| --- | --- | --- | --- | --- | --- | --- |
| x64, today | `INT 0x80` (DPL 3) | RAX | RCX, RDX, R8 | RAX | RDX | other GPRs and x87/SSE state preserved; RFLAGS = 0x202 |
| x64, after K5 | `SYSCALL` | RAX | RDI, RSI, RDX | RAX | RDX | as above; R11 and RCX are clobbered by the instruction |
| ARM64 | `SVC #0` | x8 | x0, x1, x2 | x0 | x1 | other registers and FP/SIMD state preserved; PSTATE flags cleared |

A call takes at most three register arguments; larger requests are a structure with `Version` and `Size`. The x64
change in K5 follows the SysV entry convention adopted for user code in the same step and gives the register roles of
Linux x64 so that the libc's system call stubs (step S1) differ from musl's by the status and result registers alone.
A status is a small nonnegative integer, never a negative errno; layer 2 maps statuses to errno.

### 6.2 Statuses

| Value | Name | Meaning |
| --- | --- | --- |
| 0 | `OK` | success |
| 1 | `UNSUPPORTED` | unknown call, version, operation or mode |
| 2 | `BAD_HANDLE` | handle absent, closed or of a stale generation |
| 3 | `DENIED` | handle lacks the right |
| 4 | `BAD_ADDRESS` | a user range is not mapped with the needed access |
| 5 | `TOO_LARGE` | a size limit is exceeded |
| 6 | `INVALID_ARGUMENT` | the request violates its format |
| 7 | `WRONG_TYPE` | handle of another object kind |
| 8 | `NO_MEMORY` | a quota or physical memory is exhausted |
| 9 | `NOT_RESERVED` | address outside a reservation |
| 10 | `NOT_COMMITTED` | page not committed |
| 11 | `DEADLOCK` | a wait would cycle |
| 12 | `BUSY` | the object is in use; retry is possible |
| 13 | `TIMED_OUT` | deadline passed, or no signal on a poll |
| 14 | `CLOSED` | the waited object was closed |
| 15 | `INTERRUPTED` | the wait or sleep ended because an activation was delivered (today `APC_PENDING`) |
| 16 | `NOT_FOUND` | no object at the index or address |
| 17 | retired | `INITIALIZATION_FAILED`, a DLL attach result; leaves with the library family |
| 18 | `PEER_CLOSED` | the channel peer is closed (K2) |

### 6.3 Rights

Rights are a mask on the handle. Every object kind defines its subset; `HANDLE_DUPLICATE` may only remove bits.

| Right | Applies to | Grants |
| --- | --- | --- |
| `WAIT` | event, thread, process, endpoint, interrupt | `OBJECT_WAIT` |
| `SIGNAL` | event | `EVENT_SET`, `EVENT_RESET` |
| `QUERY` | thread, process, memory object, device | the object's query call |
| `GET_CONTEXT`, `SET_CONTEXT` | thread | context read and write |
| `SUSPEND_RESUME` | thread | suspension |
| `ACTIVATE` | thread | `THREAD_ACTIVATE` |
| `KILL` | process | `PROCESS_KILL` |
| `MAP`, `WRITE`, `EXECUTE` | memory object | mapping with that access; a device memory object is never `EXECUTE` |
| `PIN` | memory object | `DMA_PIN` |
| `SEND`, `RECEIVE` | endpoint | the channel calls |
| `BIND` | device | `DEVICE_MEMORY`, `INTERRUPT_BIND` |
| `WRITE` | kernel log | `DEBUG_WRITE` |
| `DUPLICATE` | every kind | `HANDLE_DUPLICATE` |
| `TRANSFER` | every kind | moving the handle in a message |

The bit values of the existing rights (`WRITE` 1, `WAIT` 4, `SIGNAL` 8, `QUERY` 16, `GET_CONTEXT` 32,
`SET_CONTEXT` 64, `SUSPEND_RESUME` 128) stay; `JOIN` 2 is retired because joining is a wait. New bits are assigned in
K1 and K2.

## 7. ABI-1 families

Each family lists its calls as they exist after step K1 (or the step named), the rights involved, what the kernel
does not do and which current calls feed it. Names with no "today" entry are new.

### 7.1 Kernel, handles and the own process

| Call | Arguments | Result | Today |
| --- | --- | --- | --- |
| `QUERY` | — | ABI-1 version and the feature mask (§10.1) | `QUERY` 0 |
| `PROCESS_EXIT` | exit code | does not return; every thread of the process ends | `EXIT` 2 |
| `HANDLE_CLOSE` | handle | 0 | `CLOSE` 3 |
| `HANDLE_DUPLICATE` | handle, output pointer, rights (0 = same) | new handle with the subset of rights | `THREAD_REFERENCE_DUPLICATE` 34, generalized to every kind |
| `DEBUG_WRITE` | kernel-log handle, buffer, length ≤ 64 KiB | bytes written | `WRITE` 1 and `CONSOLE_WRITE` 41 |

`DEBUG_WRITE` writes through the board console that the UHI provides as a boot device. It is the kernel's last-resort
output and the root task's output before any service exists; it is not the terminal of RFC 0020, which is a layer-3
service over a delegated UART (phase D). The kernel never formats, buffers or interprets what it writes.

### 7.2 Memory

| Call | Arguments | Result | Today |
| --- | --- | --- | --- |
| `MEMORY_RESERVE` | size, alignment or fixed address, flags | base | 4 |
| `MEMORY_COMMIT` | base, size, protection | 0 | 5 |
| `MEMORY_DECOMMIT` | base, size | 0 | 6 |
| `MEMORY_PROTECT` | base, size, protection | 0 | 7 |
| `MEMORY_RELEASE` | reservation base | 0 | 8 |
| `MEMORY_RESET` | base, size, 0 | 0; committed pages are zeroed, commitment and protection kept | 26 |
| `MEMORY_QUERY` | buffer, size, version | physical totals, quotas, the user address range, page size | 20 |
| `MEMORY_PRESSURE_EVENT` | — | wait-only manual event the kernel sets under physical or quota pressure | 31 |
| `MEMORY_OBJECT_CREATE` | size, flags | handle to an anonymous memory object | `CODE_MEMORY.RESERVE`/`MAP_SPARSE` 65 (new form) |
| `MEMORY_OBJECT_MAP` | request: object, offset, size, address or 0, protection, target process or `PROCESS_SELF` | address of the mapping | `CODE_MEMORY.ALIAS` 65 (new form) |
| `CODE_PUBLISH` | base, size | 0; the pages may execute afterward | `CODE_MEMORY.PUBLISH` 65 |

Protection is `NONE`, `READ`, `READ|WRITE`, `READ|EXECUTE`; `WRITE|EXECUTE` is never granted (RFC 0004 §60). A JIT
that writes code maps one memory object twice, a writable view and an executable view, as today's `CODE_ALIAS` does
and as CoreCLR's executable allocator does on Linux with a dual mapping. A mapping of a memory object is a reservation
and is released with `MEMORY_RELEASE`.

A memory object is the one mechanism behind several policies: the boot package (read-only object given to the root
task), zero-copy loading of ELF segments (`MEMORY_OBJECT_MAP` with a fixed address), a child's image and stack (mapped
into another process before its first thread runs), the JIT's dual mapping, device memory (§7.7) and DMA buffers.
Memory objects are the shared memory of RFC 0006 §43–45: a holder maps with at most its own rights and transfers an
attenuated handle.

The fixed addresses of the current component layout (`WIT_USER_BASE`, the image window, the near-code arena and the
1 TiB data arena) are a controlled profile, not an application promise. After K5 a process has one user address range
reported by `MEMORY_QUERY`, and `MEMORY_RESERVE` accepts a fixed address inside it.

### 7.3 Threads and contexts

| Call | Arguments | Result | Today |
| --- | --- | --- | --- |
| `THREAD_CREATE` | request: process handle or `PROCESS_SELF`, entry, stack pointer, TLS base, argument, flags (`SUSPENDED`) | thread handle with `WAIT`, `QUERY`, `GET_CONTEXT`, `SET_CONTEXT`, `SUSPEND_RESUME`, `ACTIVATE`, `DUPLICATE`, `TRANSFER` | `THREAD_CREATE` 9 and `THREAD_CREATE_REFERENCE` 64 |
| `THREAD_EXIT` | exit code, reservation to release after exit or 0 | does not return | `THREAD_EXIT` 11 and `THREAD_COMPLETE` 63 |
| `THREAD_YIELD` | — | 1 when another thread ran | 10 |
| `THREAD_SET_TLS` | TLS base | 0 | new; the ELF thread pointer of the main thread (FS on x64; TPIDR_EL0 is writable at EL0 and ARM64 reports `UNSUPPORTED`) |
| `THREAD_QUERY` | thread handle or `THREAD_SELF`, buffer, size | identity, state, exit code, suspend count, processor | `THREAD_QUERY` 27, `THREAD_REFERENCE_QUERY` 35, `THREAD_NATIVE_ID` 36 |
| `THREAD_SUSPEND`, `THREAD_RESUME` | thread handle | previous suspend count | 47, 48 |
| `THREAD_CONTEXT_GET`, `THREAD_CONTEXT_SET` | thread handle, buffer, size | 0; the target must be suspended or be the caller | 46, 49 |
| `CONTEXT_PROFILE` | buffer, size, version | ISA, context layout, floating-point state format, enabled state | `CPU_CONTEXT_QUERY` 45 and `THREAD_CONTEXT_METADATA` 51 |
| `THREAD_ACTIVATE` | thread handle, callback, argument | 0 | `APC_QUEUE` 38, reshaped (§7.5) |
| `THREAD_AFFINITY` (K7) | thread handle, mask pointer, set or get | 0 | new |

The stack and the TLS block of a thread belong to the caller after K5: pthreads allocate them from reservations with a
guard page and on-demand commitment, and `THREAD_EXIT` may name the reservation the kernel releases once the thread no
longer runs on it (what musl's `__unmapself` does). The kernel-chosen raw TLS block with its self pointer, handle,
argument and last-error word, and the compiler TLS page, leave the ABI: ELF TLS is layer 2's.

Thread identity in `THREAD_QUERY` is the kernel's generation-bearing identifier, which is also the native thread id
layer 2 reports; a thread without any handle to it is reaped by the kernel when it exits (today's detached threads).
Joining is `OBJECT_WAIT` on the thread handle followed by `THREAD_QUERY` for the exit code.

A thread context is a structure with a common prefix (`Version`, `Size`, `ThreadId`, `StackLow`, `StackHigh`, `State`,
`Flags`) and a per-ISA register block: on x64 the general registers, `RIP`, `RSP`, `RFLAGS`, segment selectors and an
FXSAVE64 image; on ARM64 `X0`–`X30`, `SP`, `PC`, `PSTATE`, the 32 vector registers, `FPCR` and `FPSR`. `CONTEXT_PROFILE`
reports which block and which floating-point state are present. Validation on `SET` and on `EXCEPTION_CONTINUE` keeps
the user-mode profile (flags, selectors or exception level, canonical addresses) as today.

### 7.4 Events, waits, time and entropy

| Call | Arguments | Result | Today |
| --- | --- | --- | --- |
| `EVENT_CREATE` | flags (`MANUAL_RESET`, `INITIAL_SIGNALED`), rights | event handle | `EVENT_CREATE` 16 and `EVENT_CREATE_RIGHTS` 40 |
| `EVENT_SET`, `EVENT_RESET` | event handle | 0 | 17, 18 |
| `OBJECT_WAIT` | request: handles (≤ quota), count, flags, absolute monotonic deadline | index of the object that completed | `OBJECT_WAIT` 37, `EVENT_WAIT_UNTIL` 24, `EVENT_WAIT_ANY_UNTIL` 30, `THREAD_JOIN` 12 |
| `SLEEP_UNTIL` | absolute monotonic deadline | 0 | 23 |
| `CLOCK_READ` | clock (`MONOTONIC`, `UTC` after K6) | counter value | `MONOTONIC_READ` 21, `MONOTONIC_QUERY` 32 |
| `CLOCK_FREQUENCY` | clock | counts per second | `MONOTONIC_FREQUENCY` 22 |
| `RANDOM` | buffer, size ≤ 64 KiB, 0 | bytes written | 33 |

`OBJECT_WAIT` validates every handle before it consumes any signal, completes exactly one object and publishes the
winner atomically; `ALL` stays `UNSUPPORTED` (no port needs wait-all; the PAL composes it). Any wait or sleep ends
with `INTERRUPTED` when an activation was delivered to the thread, as a signal interrupts a Linux wait; the libc
restarts the wait where POSIX restarts it. The `ALERTABLE` flag disappears with that rule.

Waitable states: an event is signaled; a thread or process has exited; an endpoint has a message or its peer closed;
an interrupt is pending. Expired deadlines are processed before later signal and close operations, as today.

### 7.5 Faults and activations

| Call | Arguments | Result | Today |
| --- | --- | --- | --- |
| `EXCEPTION_REGISTER` | callback or 0, alternate stack or 0, flags | 0; one handler per process | 55 |
| `EXCEPTION_QUERY` | token, buffer, size | the fault record: vector, error, address, interrupted context | 56 |
| `EXCEPTION_CONTINUE` | token, transfer request: context, retire-through token | does not return on success | `EXCEPTION_CONTINUE` 57 and `EXCEPTION_UNWIND` 62 |
| `EXCEPTION_REJECT` | token | does not return; the process ends and the kernel reports the fault | 58 |

Delivery is the current protocol: the kernel enters the registered callback on the faulting thread with a token, the
callback reads the record by `EXCEPTION_QUERY`, and `EXCEPTION_CONTINUE` resumes a validated context while retiring
the record and any abandoned ancestors (the retire-through semantics of today's `EXCEPTION_UNWIND`). Nested deliveries
are bounded by a quota. Only hardware faults and activations are delivered: software exceptions of a language (C++,
managed) are user-space business in the Itanium model, so `EXCEPTION_BEGIN` leaves.

An **activation** is the asynchronous case of the same path. `THREAD_ACTIVATE` marks a target thread; at the target's
next return to user mode (from a tick, a call or a wait) the kernel enters the handler with a record whose vector is
`ACTIVATION` and whose context is the interrupted one, and whose payload is the requester's callback and argument. The
handler may run the callback, inspect or modify the context and `EXCEPTION_CONTINUE`. This is what a POSIX signal
delivers on Linux and what CoreCLR's `InjectActivation` and the libc's `pthread_kill` (step S3) need: a preemptive
callback with the interrupted context, not a queue the target drains (today's `APC_DEQUEUE`). A thread parked in a
wait is unparked and its wait returns `INTERRUPTED` after the handler continues.

### 7.6 Channels (K2)

| Call | Arguments | Result |
| --- | --- | --- |
| `CHANNEL_CREATE` | output pointer for two handles, flags | two endpoint handles with `SEND`, `RECEIVE`, `WAIT`, `DUPLICATE`, `TRANSFER` |
| `CHANNEL_SEND` | endpoint, message: inline bytes ≤ quota, handles ≤ quota, flags | 0; the handles are moved out of the sender's table atomically with the message |
| `CHANNEL_RECEIVE` | endpoint, buffer: inline bytes, handle slots | bytes and handles received; `TIMED_OUT` when empty, `PEER_CLOSED` when empty and the peer is closed |

Messages keep their boundaries (RFC 0006 §14). A message is a small inline payload plus transferred capabilities
(RFC 0006 §13); large data travels as a memory object handle, never through the kernel. Transfer is atomic with
delivery (RFC 0006 §33), moves authority (§35) and is attenuated by `HANDLE_DUPLICATE` before sending (§34). A handle
without `TRANSFER` cannot be sent. Quotas bound the queue depth and bytes per endpoint; an exhausted queue fails `SEND`
with `BUSY` and the sender waits for the endpoint to become writable. The kernel does not know method names,
interfaces or serialization (RFC 0006 §11).

### 7.7 Devices (K3)

| Call | Arguments | Result |
| --- | --- | --- |
| `DEVICE_ACQUIRE` | descriptor index | device handle with `BIND`, `QUERY`, `DUPLICATE`, `TRANSFER`; root task authority |
| `DEVICE_MEMORY` | device handle, region index | memory object of kind device: uncached, never executable, mappable with `MEMORY_OBJECT_MAP` |
| `INTERRUPT_BIND` | device handle, line index, event handle | interrupt handle; the kernel sets the event on each interrupt and masks the line |
| `INTERRUPT_ACK` | interrupt handle | 0; the line is unmasked |
| `DMA_PIN` | memory object with `PIN`, offset, size, output for physical or device-address ranges | pin handle; pages stay resident and unchanged in address while pinned |
| `DMA_UNPIN` | pin handle | 0 |

The platform enumerates regions and interrupt lines (PCI with ECAM on q35, the device tree on virt); the kernel
publishes them to the root task as a read-only descriptor table (RFC 0007 §13) and knows no device class, protocol
or driver. Descriptors describe existence; `DEVICE_ACQUIRE` and the handles derived from it are the authority (RFC
0007 §14). Interrupt handling in user space is a wait on the event, which is why `OBJECT_WAIT` serves drivers too.

DMA without an IOMMU (the first step) is a trust boundary made explicit: `DMA_PIN` hands physical addresses only for
memory objects the driver holds with `PIN`, and the kernel cannot stop a device programmed with another address. The
`PIN` right is therefore granted only by the root task to the drivers of the device manager (RFC 0004 §57). With an
IOMMU the same call returns device addresses the kernel maps, and the descriptor says which model is in force. Port
I/O stays `UNSUPPORTED` until a target needs it; the first target, virtio on QEMU, is memory mapped on both boards.

### 7.8 Processes (K5)

| Call | Arguments | Result |
| --- | --- | --- |
| `PROCESS_CREATE` | request: endpoint handle to install in the child, quotas, flags | process handle with `WAIT`, `QUERY`, `KILL`, `DUPLICATE`, `TRANSFER`; the result also carries the child-local value of the installed endpoint handle |
| `PROCESS_KILL` | process handle, code | 0; every thread of the process ends |
| `PROCESS_QUERY` | process handle, buffer, size | state, exit code, owned pages |

A new process is an empty address space with one capability: the channel endpoint its creator chose (RFC 0004
§137: no implicit inheritance). The creator maps the image and the first stack into it with `MEMORY_OBJECT_MAP`,
starts the first thread with `THREAD_CREATE` naming the process handle, and sends every other capability, the
environment and the arguments over the channel in the protocol of the process manager (layer 2). The kernel reads no
image format, no name and no path; whether the image is ELF, a flat blob or a JIT's output is invisible to it.

Quotas of a child are set at creation within kernel-wide defaults; hierarchical accounting against the creator's own
quota is an open question (§12). The kernel has N processes bounded by memory, not by two slots.

### 7.9 Processors and barriers (K7)

| Call | Arguments | Result | Today |
| --- | --- | --- | --- |
| `PROCESSOR_QUERY` | buffer, size, version | current processor; the online set; topology, cache sizes and ISA features per processor as the UHI reports them | `PROCESSOR_QUERY` 42 and `CPU_CACHE_SIZE` 29 |
| `PROCESS_WRITE_BARRIER` | — | 0 after every processor running a thread of the process has executed a fence | 28 |
| `THREAD_AFFINITY` | see §7.3 | | new |

Topology is descriptive (RFC 0005 §9); affinity is the mechanism of placement level 3 (RFC 0005 §10) and policy
stays in layer 2 and above. The kernel schedules threads, never managed tasks (RFC 0005 §52). Until K7 the barrier
keeps its single-processor invariant and refuses an unsupported topology rather than reporting success.

### 7.10 Clocks (K6)

`CLOCK_READ` and `CLOCK_FREQUENCY` take a clock identifier. `MONOTONIC` is today's domain (HPET on q35, the generic
counter on virt), never UTC and never the delivered tick count. `UTC` is nanoseconds since 1970-01-01 as the UHI's
real-time clock reported it at boot plus the monotonic delta since; its frequency is 10^9. Setting UTC is a policy
with a capability the root task holds; the call and the holder are decided in K6 (§12). The delivered-tick clock of
calls 13–15 and 19 is removed: nothing above the kernel may depend on the tick rate.

### 7.11 Boot and the root task (K4)

The boot contract `WitBootInfo` (today v4, 112 bytes: architecture, memory map, image sections, entropy seed, the
boot package's extents) gains the extent of the root task image in v5. The kernel maps the root task from a trivial
flat format: a header with the entry point and at most four page-aligned segments (file offset, file size, memory
size, protection), no names, no imports, no relocations, validated whole before any page is mapped. The package is
not parsed by the kernel; it becomes a read-only memory object.

The root task starts with a startup descriptor in its first thread's argument register (RDI on x64 after K5, x0 on
ARM64): version, size, the ABI-1 version and feature mask, the user address range, and a table of initial handles:
the kernel log (`DEBUG_WRITE`), the boot package memory object, the device descriptor table (read-only memory object),
the UTC capability (K6) and the authority to acquire devices. Everything else the system layer builds for itself.
Today's `WitUserStartup` (version, size, console handle, image info) and the embedded resource namespace of
`WitUserImageInfo` leave with the PE loader.

### 7.12 The target set

| Family | Calls | Count |
| --- | --- | --- |
| kernel, handles, own process | `QUERY`, `PROCESS_EXIT`, `HANDLE_CLOSE`, `HANDLE_DUPLICATE`, `DEBUG_WRITE` | 5 |
| memory | `MEMORY_RESERVE`, `MEMORY_COMMIT`, `MEMORY_DECOMMIT`, `MEMORY_PROTECT`, `MEMORY_RELEASE`, `MEMORY_RESET`, `MEMORY_QUERY`, `MEMORY_PRESSURE_EVENT`, `MEMORY_OBJECT_CREATE`, `MEMORY_OBJECT_MAP`, `CODE_PUBLISH` | 11 |
| threads | `THREAD_CREATE`, `THREAD_EXIT`, `THREAD_YIELD`, `THREAD_SET_TLS`, `THREAD_QUERY`, `THREAD_SUSPEND`, `THREAD_RESUME`, `THREAD_CONTEXT_GET`, `THREAD_CONTEXT_SET`, `CONTEXT_PROFILE`, `THREAD_ACTIVATE`, `THREAD_AFFINITY` | 12 |
| events, waits, time, entropy | `EVENT_CREATE`, `EVENT_SET`, `EVENT_RESET`, `OBJECT_WAIT`, `SLEEP_UNTIL`, `CLOCK_READ`, `CLOCK_FREQUENCY`, `RANDOM` | 8 |
| faults and activations | `EXCEPTION_REGISTER`, `EXCEPTION_QUERY`, `EXCEPTION_CONTINUE`, `EXCEPTION_REJECT` | 4 |
| channels | `CHANNEL_CREATE`, `CHANNEL_SEND`, `CHANNEL_RECEIVE` | 3 |
| devices | `DEVICE_ACQUIRE`, `DEVICE_MEMORY`, `INTERRUPT_BIND`, `INTERRUPT_ACK`, `DMA_PIN`, `DMA_UNPIN` | 6 |
| processes and processors | `PROCESS_CREATE`, `PROCESS_KILL`, `PROCESS_QUERY`, `PROCESSOR_QUERY`, `PROCESS_WRITE_BARRIER` | 5 |
| | | **54** |

## 8. Inventory of the 70 current calls

Classes today are those of ABI-Reference.md (core, runtime-pal, legacy, experimental). Dispositions:

- **stays**: the mechanism remains in ABI-1, possibly under the target name;
- **merged**: the call disappears as a number and its mechanism lives on in the named target call;
- **removed**: the mechanism itself goes (legacy clock domain, pull-style delivery, a call replaced by a constant);
- **leaves**: the function moves to layer 2 and no kernel call replaces it;
- **split**: operations of a composite call go different ways.

| № | Call | Today | Disposition | Target or new owner | Step |
| --- | --- | --- | --- | --- | --- |
| 0 | `QUERY` | core | stays | `QUERY` with feature mask | K1 |
| 1 | `WRITE` | core | merged | `DEBUG_WRITE` | K1 |
| 2 | `EXIT` | core | stays | `PROCESS_EXIT` | K1 |
| 3 | `CLOSE` | core | stays | `HANDLE_CLOSE` | K1 |
| 4 | `MEMORY_RESERVE` | core | stays | gains fixed-address mode in K5 | K1 |
| 5 | `MEMORY_COMMIT` | core | stays | | K1 |
| 6 | `MEMORY_DECOMMIT` | core | stays | | K1 |
| 7 | `MEMORY_PROTECT` | core | stays | gains `READ|EXECUTE` for pages a memory object backs; `WRITE|EXECUTE` never | K5 |
| 8 | `MEMORY_RELEASE` | core | stays | also releases a memory-object mapping | K1 |
| 9 | `THREAD_CREATE` | core | merged | `THREAD_CREATE` (request form, caller's stack and TLS); `DETACHED` becomes closing the handle; `LIBRARY_NOTIFICATIONS` leaves with DLLs | K5 |
| 10 | `THREAD_YIELD` | core | stays | | K1 |
| 11 | `THREAD_EXIT` | core | stays | `THREAD_EXIT`; 63 merges into it, and the coordinated-profile distinction is layer 2's lifecycle | K1 |
| 12 | `THREAD_JOIN` | core | merged | `OBJECT_WAIT` on the thread handle + `THREAD_QUERY` | K1 |
| 13 | `CLOCK_READ` | legacy | removed | `CLOCK_READ(MONOTONIC)`; fixtures migrate | K1 |
| 14 | `CLOCK_FREQUENCY` | legacy | removed | `CLOCK_FREQUENCY(MONOTONIC)` | K1 |
| 15 | `THREAD_SLEEP` | legacy | removed | `SLEEP_UNTIL` | K1 |
| 16 | `EVENT_CREATE` | core | merged | `EVENT_CREATE` with rights (40) | K1 |
| 17 | `EVENT_SET` | core | stays | | K1 |
| 18 | `EVENT_RESET` | core | stays | | K1 |
| 19 | `EVENT_WAIT` | legacy | removed | `OBJECT_WAIT` | K1 |
| 20 | `MEMORY_QUERY` | core | stays | arenas become one user range in K5 | K1 |
| 21 | `MONOTONIC_READ` | core | stays | `CLOCK_READ` with a clock argument | K6 |
| 22 | `MONOTONIC_FREQUENCY` | core | stays | `CLOCK_FREQUENCY` with a clock argument | K6 |
| 23 | `SLEEP_UNTIL` | core | stays | returns `INTERRUPTED` on activation | K1 |
| 24 | `EVENT_WAIT_UNTIL` | core | merged | `OBJECT_WAIT` | K1 |
| 25 | `THREAD_CURRENT` | core | removed | the constant `THREAD_SELF` replaces a call that borrowed a handle | K1 |
| 26 | `MEMORY_RESET` | core | stays | | K1 |
| 27 | `THREAD_QUERY` | core | stays | `THREAD_QUERY` of self or by handle; TLS fields leave | K1 |
| 28 | `PROCESS_WRITE_BARRIER` | runtime-pal | stays | IPI-based in K7 | K7 |
| 29 | `CPU_CACHE_SIZE` | runtime-pal | merged | `PROCESSOR_QUERY` descriptor | K7 |
| 30 | `EVENT_WAIT_ANY_UNTIL` | core | merged | `OBJECT_WAIT` | K1 |
| 31 | `MEMORY_PRESSURE_EVENT` | runtime-pal | stays | | K1 |
| 32 | `MONOTONIC_QUERY` | runtime-pal | merged | `CLOCK_READ`; the no-TLS copy-out form is unnecessary once the raw TLS block leaves | K6 |
| 33 | `RANDOM` | core | stays | | K1 |
| 34 | `THREAD_REFERENCE_DUPLICATE` | runtime-pal | stays | `HANDLE_DUPLICATE` for every object kind; endpoints join in K2 | K1 |
| 35 | `THREAD_REFERENCE_QUERY` | runtime-pal | merged | `THREAD_QUERY` | K1 |
| 36 | `THREAD_NATIVE_ID` | runtime-pal | merged | `THREAD_QUERY` | K1 |
| 37 | `OBJECT_WAIT` | runtime-pal | stays | the one wait; `INTERRUPTED` replaces `APC_PENDING`; `ALERTABLE` disappears; `ALL` stays unsupported | K1 |
| 38 | `APC_QUEUE` | runtime-pal | stays | `THREAD_ACTIVATE`: preemptive delivery through the fault callback with the interrupted context | K1 |
| 39 | `APC_DEQUEUE` | runtime-pal | removed | delivery is push, not pull | K1 |
| 40 | `EVENT_CREATE_RIGHTS` | runtime-pal | stays | `EVENT_CREATE` | K1 |
| 41 | `CONSOLE_WRITE` | runtime-pal | stays | `DEBUG_WRITE`; the terminal becomes a layer-3 service over a delegated UART in phase D | K1 |
| 42 | `PROCESSOR_QUERY` | runtime-pal | stays | `PROCESSOR_QUERY` with topology | K7 |
| 43 | `THREAD_NAME_SET` | runtime-pal | leaves | libc (`pthread_setname_np`); kernel diagnostics print identifiers | K8 |
| 44 | `THREAD_NAME_QUERY` | runtime-pal | leaves | libc | K8 |
| 45 | `CPU_CONTEXT_QUERY` | runtime-pal | stays | `CONTEXT_PROFILE` | K1 |
| 46 | `THREAD_CONTEXT_GET` | runtime-pal | stays | | K1 |
| 47 | `THREAD_SUSPEND` | runtime-pal | stays | | K1 |
| 48 | `THREAD_RESUME` | runtime-pal | stays | | K1 |
| 49 | `THREAD_CONTEXT_SET` | runtime-pal | stays | | K1 |
| 50 | `THREAD_CONTEXT_RESTORE` | runtime-pal | leaves | restoring the own thread's context is user code (libunwind); fault frames resume through `EXCEPTION_CONTINUE` | K8 |
| 51 | `THREAD_CONTEXT_METADATA` | runtime-pal | merged | `CONTEXT_PROFILE` | K1 |
| 52 | `STACK_LEASE_ACQUIRE` | runtime-pal | leaves | scanning a suspended thread's stack is same-address-space user code; the GC reads it directly | K8 |
| 53 | `STACK_LEASE_QUERY` | runtime-pal | leaves | | K8 |
| 54 | `STACK_LEASE_RELEASE` | runtime-pal | leaves | | K8 |
| 55 | `EXCEPTION_REGISTER` | runtime-pal | stays | gains the alternate stack | K1 |
| 56 | `EXCEPTION_QUERY` | runtime-pal | stays | | K1 |
| 57 | `EXCEPTION_CONTINUE` | runtime-pal | stays | absorbs the retire-through transfer of 62 | K1 |
| 58 | `EXCEPTION_REJECT` | runtime-pal | stays | | K1 |
| 59 | `EXCEPTION_BEGIN` | runtime-pal | leaves | software exceptions are Itanium-ABI user code (libc++abi, the runtime) | K8 |
| 60 | `FATAL_ARM` | runtime-pal | leaves | the libc's abort and the runtime's fail-fast write through `DEBUG_WRITE` and end with `PROCESS_EXIT` | K8 |
| 61 | `FATAL_REPORT` | runtime-pal | leaves | | K8 |
| 62 | `EXCEPTION_UNWIND` | runtime-pal | merged | `EXCEPTION_CONTINUE` | K1 |
| 63 | `THREAD_COMPLETE` | core | merged | `THREAD_EXIT` | K1 |
| 64 | `THREAD_CREATE_REFERENCE` | runtime-pal | stays | `THREAD_CREATE`, the one form | K5 |
| 65 | `CODE_MEMORY` | runtime-pal | split | `RESERVE`, `ALIAS`, `MAP_SPARSE`, `RESET_SPARSE`, `PROTECT` → memory objects and mappings with `EXECUTE`; `PUBLISH` → `CODE_PUBLISH`; `VALIDATE` (PE unwind tables) leaves | K5, K8 |
| 66 | `FILE` | experimental | leaves | the libc over the package memory object (S1), then the namespace service over channels (D5) | K8 |
| 67 | `STORAGE_QUERY` | experimental | leaves | as 66 | K8 |
| 68 | `LIBRARY` | experimental | leaves | `ld.so` (S5): loading, symbols, module paths, TLS modules, lifecycle | K8 |
| 69 | `PROCESS_STATE` | experimental | leaves | `environ` and the current directory live in the libc as on POSIX; the process manager passes the initial values at creation (S6) | K8 |

| Disposition | Calls | Count |
| --- | --- | --- |
| stays | 0, 2, 3, 4, 5, 6, 7, 8, 10, 11, 17, 18, 20, 21, 22, 23, 26, 27, 28, 31, 33, 34, 37, 38, 40, 41, 42, 45, 46, 47, 48, 49, 55, 56, 57, 58, 64 | 37 |
| merged | 1, 9, 12, 16, 24, 29, 30, 32, 35, 36, 51, 62, 63 | 13 |
| removed | 13, 14, 15, 19, 25, 39 | 6 |
| leaves | 43, 44, 50, 52, 53, 54, 59, 60, 61, 66, 67, 68, 69 | 13 |
| split | 65 | 1 |
| | | **70** |

The 37 calls that stay are the 37 target calls today's kernel already provides, each under its target name; the 13
merged calls fold into them. `CODE_PUBLISH` from the split call, `THREAD_SET_TLS`, `MEMORY_OBJECT_CREATE`,
`MEMORY_OBJECT_MAP`, the three channel calls, the six device calls, the three process calls and `THREAD_AFFINITY` are
the 17 new ones: 54 in all (§7.12). The 50 calls that stay or merge are the "about 50 mechanisms" ADR 0024 counted.

The operations of the composite calls that leave are: `FILE` (`OPEN`, `LENGTH`, `READ_AT`, `READ`, `SEEK`),
`STORAGE_QUERY` (`STAT`, `LIST`), `LIBRARY` (`LOAD`, `SYMBOL`, `UNLOAD`, `QUERY`, `FIND`, `PATH`, `ACQUIRE_READER`,
`RELEASE_READER`, `QUERY_READER`, `FINISH_LIFECYCLE`, `SHUTDOWN`, `THREAD_ENTER`, `THREAD_LEAVE`, `MODULE_PATH`) and
`PROCESS_STATE` (`ENV_GET`, `ENV_SET`, `ENV_BLOCK`, `CWD_GET`, `CWD_SET`): 26 operations, every one a name, a format or
a lifecycle.

What else leaves the ABI with them: the kernel-chosen raw TLS layout (`WIT_TLS_*`), the compiler TLS page and its
data offset, `WitUserStartup` and `WitUserImageInfo` with the `boot:/` resource namespace, the library lifecycle plans,
the `LIBRARY_NOTIFICATIONS` and coordinated-profile thread flags, the fixed component address layout, the PE quotas of
`limits.h` and the handle kinds `FILE`, `LIBRARY`, `LIBRARY_READER` and `LIBRARY_LIFECYCLE`. The kernel sources named
by plan step K8 (`pe*`, `user_library*`, `package`, `files`, `user_files`, `user_process_state`, `user_thread_name`) and
the ones this inventory adds (`user_stack_lease`, the fatal and software-exception paths of `user_exception`,
`user_apc`'s dequeue, the context restore of `user_thread_context`, the validation operation of `user_code`) are
removed in that step.

## 9. ABI-2: the system layer as .NET sees it

### 9.1 Composition

ABI-2 is what a runtime binary built for `TargetOS=witos` links against and what a WitOS service or application may
additionally use:

| Part | Content | Source |
| --- | --- | --- |
| C library | `libc.so`: the C standard library, POSIX threads, mathematics, `dlopen`/`dlsym`, ELF TLS, errno; one library as in musl | musl by pin, patched only through `UpstreamPatches` (S1, S2) |
| C++ runtime | `libunwind`, `libc++abi`, `libc++` in the Itanium ABI | LLVM by pin (S4) |
| Dynamic loader | `ld.so`: ELF program interpreter, symbol resolution, TLS modules | with the libc (S5) |
| WitOS capability API | `libwitos.so`: handles, channels, memory objects, waits, process creation through the process manager, device access through the device manager; the `witos_` prefix in C | WitOS's own (S6 onward) |
| Managed capability API | `OutWit.OS.*` packages over `libwitos` (RFC 0002, 0005, 0006 naming) | WitOS's own (phases D, N) |

The sysroot of the `*-unknown-witos` toolchain triple (steps T1, T4) is the authoritative header set of ABI-2. The
runtime's own native libraries (`libcoreclr`, `libclrjit`, `libSystem.Native`, `libhostfxr`, `libhostpolicy`,
NativeAOT output) are consumers of ABI-2, not parts of it.

### 9.2 Two audiences

**Runtime binaries and WitOS services** see the whole of §9.1. The POSIX-shaped substrate exists for them, exactly
as on Linux, because CoreCLR's PAL and `System.Native` need it (ADR 0024, decision 4).

**Applications** see .NET and `OutWit.OS.*`. Portable managed code never reaches the substrate; the Developer
Experience Manifesto §12 places platform P/Invokes outside the portable boundary. An application that P/Invokes
`libc` is platform-specific code: WitOS does not prevent it and promises it nothing beyond what runtime binaries need.
Neither POSIX nor Win32 is an external contract of WitOS (ADR 0024).

### 9.3 Process model, signals and services at ABI-2

- **Processes**: created through the process manager by a `posix_spawn`-shaped API over `PROCESS_CREATE`; no `fork`;
  `exec` replaces the own image only when a runtime scenario needs it (step R4 starts without it). Each process gets
  the initial environment, arguments, current directory and capabilities from its creator over its first channel;
  `environ` and the current directory then live in the libc as on every POSIX system, and a change by one module is
  seen by every module because one `libc.so` serves the process. Child handles are never inherited implicitly (RFC
  0004 §137); compatibility profiles for standard streams are the process manager's (RFC 0004 §138).
- **Threads**: pthreads over `THREAD_CREATE`, futex-equivalent waits over events, ELF TLS through the thread pointer,
  per-thread errno, `__cxa_thread_atexit` (S2).
- **Signals**: the synchronous set (`SIGSEGV`, `SIGBUS`, `SIGFPE`, `SIGILL`, `SIGTRAP`) from fault delivery with a
  `ucontext`, `pthread_kill` of a real-time signal as the activation of §7.5, `sigaltstack`; no terminal or
  job-control signals, no `kill` to other processes (S3).
- **Files**: over the boot package memory object first (S1, R4), then the namespace service over channels (D5), which
  is what `System.IO` reaches through `System.Native`. The boot package is an immutable image, not a filesystem.
- **Time**: `CLOCK_MONOTONIC` over `MONOTONIC`, `CLOCK_REALTIME` over `UTC` (K6); `clock_getres` reports the real
  frequency.
- **Entropy**: `getrandom` over `RANDOM`.
- **Explicitly absent**, failing with `ENOSYS` or an explicit error rather than a pretended success: `fork`, `ptrace`,
  `/proc` and `/sys`, cgroups, `epoll` and sockets until the network milestone (M7), `mmap` of files until the
  namespace service exists, System V IPC.

### 9.4 What ABI-2 is not

Not a promise of POSIX completeness, not a Linux or Windows personality, not a stable binary contract for managed
applications (theirs is .NET's), and not the raw ABI-1: `libwitos` presents the system layer's objects (a process is
created through the process manager, a device through the device manager), and the kernel's call numbers never appear
above layer 2.

## 10. Versioning

### 10.1 ABI-1

1. **One version number**, reported by `QUERY` together with a **feature mask** of the families present (`CHANNELS`,
   `DEVICES`, `PROCESSES`, `UTC`, `SMP`). During phase K the version is the draft counter `WIT_ABI_VERSION` as today
   and families arrive one by one; step K8 declares **ABI-1 1.0**.
2. **After K1 a call number, a status value and a rights bit are never reused**; K1 is the last renumbering.
3. **After 1.0 changes are additive:** a new call, a new operation of a composite call, a new appended field under a
   new structure version, a new feature bit. Removing or changing the meaning of a call needs a major version and an
   ADR.
4. **Unknown is refused:** an unknown call, version or flag returns `UNSUPPORTED`; the kernel never guesses.
5. **The root task checks** the version and the feature mask in its startup descriptor and refuses to run on an
   unknown major version.
6. **Per-ISA parts** (register blocks, calling convention) are versioned with the whole and described by
   `CONTEXT_PROFILE`; adding an ISA adds a register block and a row in §6.1, nothing else.
7. **The reference stays executable:** `ABI-Reference.md` keeps one row per call and is checked against the headers by
   a host test; a change to this RFC's §7 that is not in the headers is a plan step, not a fact.

### 10.2 ABI-2

1. **The C library keeps musl's rule:** no symbol versioning, and an exported symbol never changes meaning or
   disappears. WitOS adds no symbols to the libc namespace; everything WitOS-specific is in `libwitos`.
2. **`libwitos.so.1`** carries its major in the soname and ELF symbol versions `WITOS_1.0`, `WITOS_1.1`, … for
   additions; request structures carry a size for extension; errors are explicit codes.
3. **`OutWit.OS.*`** follows semantic versioning and binds to `libwitos` by symbol version; standard .NET never depends
   on it (RFC 0005 §5).
4. **The runtime identifier is `witos-x64` and `witos-arm64`** without an OS version, as `linux-musl-x64` is. A runtime
   built against ABI-2 1.x runs on every later 1.y system layer (goal 5: .NET 8 and .NET 10 side by side); the reverse
   holds only when the runtime uses no symbol newer than the system layer, and the loader reports the missing symbol
   explicitly.
5. **The freeze point is step N3**, the first image with two runtimes; before it the R phase may change ABI-2 and
   records each change in the plan.

## 11. Decisions this revision makes

Recorded here so that the plan and ADR 0024 can refer to them:

1. **APCs stay as activations** (§7.5): the kernel's only way to make another thread run a callback with its
   interrupted context; the pull queue goes. The signals default of ADR 0024 rests on this mechanism.
2. **Thread names leave** the kernel; diagnostics use identifiers.
3. **Environment and current directory live in the libc** (§9.3); the process manager passes initial values; the
   kernel keeps neither.
4. **Joining is waiting**: no consuming join handle; a thread with no handle to it is reaped at exit.
5. **One wait, one deadline domain, interruptible by activations**; the tick-based clock and `ALERTABLE` go.
6. **Memory objects** are the one mechanism behind the package, ELF loading, the JIT's dual mapping, child images,
   device memory and DMA buffers; the current `CODE_MEMORY` family dissolves into them.
7. **Stack leases, fatal reports, software exception scopes and context restore leave**: in the Itanium model they
   are same-address-space user code.
8. **Processes receive one capability** at creation, a channel endpoint; everything else arrives in messages.
9. **x64 adopts `SYSCALL` and SysV register roles in K5**, together with the SysV entry convention; ARM64 is already
   Linux-shaped.
10. **Port I/O is unsupported** until a target needs it.
11. **ABI-1 counts 54 calls** against the 70 of today, with six families the documents required and the kernel
    lacked.

## 12. Open questions

Carried into PLAN.md §7:

- Hierarchical quotas: whether a child's pages and handles are charged to its creator (K5).
- The holder of the UTC-set capability and whether the kernel needs a `CLOCK_SET` call at all (K6).
- Whether fault records stay pulled by token or are pushed onto the handler's alternate stack (K1); the token form
  keeps today's tested path and is the default.
- On ARM64, whether `CODE_PUBLISH` stays necessary once the kernel enables EL0 cache maintenance (`SCTLR_EL1.UCI`), or
  stays as the uniform form (K5).
- The DMA boundary without an IOMMU beyond "root grants `PIN` to drivers alone" (K3).
- The flat image format of the root task and the exact content of its startup descriptor (K4).

## 13. History

- v0.1 (2026-09-16): the UEFI boot contract (`WitBootInfo` v1) and M0.
- v0.2–v0.3 (2026-09-16): M1, kernel-owned stacks, CPU exception diagnostics, physical pages, protected paging and
  timer-driven kernel contexts.
- v0.4–v0.9 (2026-09-16 to 2026-09-17): M2, the first ring-3 component and the experimental user ABI, sparse user
  memory, threads and TLS, events and deadlines, PE image loading and the native bootstrap handoff.
- v0.10–v0.13 (2026-09-17 to 2026-09-20): allocator snapshots, monotonic deadlines, mutexes, committed-memory reset,
  current-thread identity, detached workers and the native last-error word (ABI v6–v13). Evidence for all of these
  is in `Implementation/M0-Boot.md`, the `M1-*.md` and `M2-*.md` documents and the `NativeAot-*.md` documents. The
  kernel then went on to ABI v51 without this RFC being revised; ABI-Reference.md covers that span.
- v3 (2026-10-06): this document, after ADR 0024.
