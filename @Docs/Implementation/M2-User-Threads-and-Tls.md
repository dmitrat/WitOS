# ADR 0001: Bounded user threads and TLS

**Status:** Accepted and implemented in WitOS 0.0.6; extended by [events/deadlines](M2-Events-and-Deadlines.md) in 0.0.7.
**Date:** 2026-09-16.
**Scope:** M2 native execution; no guest .NET runtime yet.

## Context

The pinned NativeAOT runtime needs ordinary threads, TLS, finalizer execution and coordinated waits. The preceding WitOS slice could activate one user context and provided sparse per-component memory, but could not run sibling threads or wait for their completion.

The current backend remains one x64 CPU, two component slots, interrupt-gate syscalls and a 128-frame quota per address space. New behavior must preserve process isolation, atomic allocation failure and the existing VM tests.

## Decision and alternatives

| Option | Benefit | Cost / decision |
| --- | --- | --- |
| One shared kernel stack with copied user contexts | Smaller static stack pool | Requires copying every saved context and a separate blocked-continuation design; deferred |
| Dedicated guarded kernel stack per thread | Saved interrupt/syscall frames remain on their owning stacks; straightforward dispatch and join | Chosen with four bounded thread slots per component; static kernel-stack storage remains reserved |
| Full dynamic kernel-stack allocator and cross-process scheduler | Scales beyond fixed slots | Adds allocation/scheduling policy unrelated to proving the first user-thread boundary; deferred |

One component runs at a time. Inside it, a round-robin dispatcher selects ready threads on PIT ticks, explicit yield, blocking join or thread exit. The existing kernel-worker scheduler remains separate.

Each thread receives four zeroed user stack pages (16 KiB), unmapped lower/upper guards, a zeroed TLS page and a guarded 64 KiB kernel stack. User stack/TLS frames are allocated at creation and returned on reaping; kernel stacks come from a fixed pool. There are now twelve kernel-owned stacks and twenty-four guard pages in total.

Threads share their component's address space and handle table. Separate stack/TLS regions provide distinct storage, not security isolation from sibling threads.

## Thread state and lifetime

| State | Meaning / transitions |
| --- | --- |
| Empty | Available slot; successful creation publishes Ready only after all resources exist |
| Ready | Can be selected; dispatch changes it to Running |
| Running | Timer/yield changes it to Ready; join can change it to Waiting; exit changes it to Exited |
| Waiting | Has one validated join dependency; target exit publishes its result and changes it to Ready |
| Exited | Holds an exit code until joined or closed; reaping returns user frames and the slot |

Create first validates an executable user entry, then allocates a typed handle, four stack pages and a TLS page. Failure closes the temporary handle, rolls back every allocated page and leaves the slot Empty. Stack/TLS creation never consumes dynamic reservation records. The main thread counts against the four-thread limit.

Join has one consumer. An already completed target is reaped immediately; otherwise the caller parks and the target records its waiter. Waiting-edge installation, result publication, wakeup and reaping run on one CPU with IF clear. No wakeup can interleave between checking completion and parking. Self-join or a cycle in the bounded wait graph returns Deadlock before any wait edge is installed. A second waiter gets Busy.

Successful join consumes the target handle and returns its exit code. Close on a completed, unclaimed thread also reaps it. Close on a live thread returns Busy; detach and cancellation are not implemented. Reused slots receive fresh zeroed pages and a new handle generation.

Thread exit stops only the caller. When no ready/waiting threads remain, the component returns the last exiting thread's code. The existing component Exit call, any user CPU fault, invalid return state or activation budget expiry stops the whole component, closes every handle and allows all remaining pages to be reclaimed. Threads share memory, so a native fault is not treated as an independently recoverable sibling failure.

The ten delivered-tick budget applies to the entire activation, not separately to each thread. It remains a bounded test mechanism rather than calibrated CPU accounting.

## Thread calls introduced in ABI v3

The existing INT 0x80 transport, calls 0–8, startup prefix and register preservation remain. The current startup/query report version 5; v4 retains these thread calls and adds event/clock operations.

| Call | RCX | RDX | R8 | Result |
| --- | --- | --- | --- | --- |
| 9 Thread create | Executable user entry | Opaque initial argument | Flags, must be zero | Typed join handle |
| 10 Thread yield | Ignored | Ignored | Ignored | Zero |
| 11 Thread exit | Exit code | Ignored | Ignored | Does not return to this thread |
| 12 Thread join | Thread handle | Ignored | Ignored | Target exit code; consumes handle |

New statuses are 11 Deadlock and 12 Busy. Existing invalid-handle, wrong-type, denied-rights, invalid-argument, bad-address and no-memory statuses retain their meanings. Failed calls return a zero result.

A new entry receives its argument in RCX, RSP aligned for the existing x64 calling convention, clean GPR/x87/SSE state and no user return trampoline. The entry must exit explicitly; accidental RET reaches the zero return address and faults the component. Entry-point/module discovery remains the caller's responsibility within the controlled fixture.

Every transition back to user mode checks the selected context against that thread's kernel stack, CS/SS, executable RIP and its own mapped user stack range. Returning with a mapped sibling stack is rejected before IRETQ. The dispatcher updates TSS.RSP0 to the selected kernel stack.

## Raw TLS contract

The kernel installs the selected thread's user TLS address in IA32_FS_BASE and uses user data selector 0x2B for FS. It reinstalls this state on every returning syscall/timer transition. CR4.FSGSBASE is explicitly disabled; user code must leave FS under kernel control. The kernel uses no segment-based TLS and resets FS base to zero before returning to its supervising context.

The MSR/segment mechanism and CR4 enable bit follow the [Intel system programming manual](https://cdrdv2-public.intel.com/874249/253668-090-sdm-vol-3a.pdf) and [FSGSBASE guidance](https://www.intel.com/content/www/us/en/developer/articles/technical/software-security-guidance/best-practices/guidance-enabling-fsgsbase.html).

| FS offset | Initial value |
| --- | --- |
| 0 | TLS block's own user address |
| 8 | This thread's handle |
| 16 | Initial entry argument |
| 24–4095 | Zeroed application storage |

The page is RW/NX and component-owned. Its writable header does not authorize anything: the kernel derives authority from its own live handle/thread records. Fixed thread stack/TLS mappings cannot be altered by the dynamic memory calls.

This is a raw TLS block for native fixtures. It is not a Windows TEB, ELF TLS layout, compiler TLS loader or implementation of CoreLib ThreadStatic. The actual NativeAOT ABI/backend still determines those adaptations.

## Validation and evidence

The host separately builds `threads.asm` as `ThreadFixture.pe`, applying the same one-page RX, fixed native entry and no-import/no-relocation checks as the original `UserFixture.pe`. Both images are uploaded with CI diagnostics.

The existing seventeen VM scenarios now require eighty-three M2 groups and twenty-nine contained user faults in each successful boot; the ten thread groups below are unchanged. Ten added groups cover:

| Group | Evidence |
| --- | --- |
| ThreadPreemptionAndTls | Two non-yielding workers require each other's progress; at least two timer switches; distinct FS blocks, stack canaries, GPRs, XMM0/XMM6 and MXCSR survive |
| ThreadJoinAndReuse | Join results, live close rejection, completed close, zeroed stack/TLS reuse, stale handles, invalid creation and fixed TLS protection |
| ThreadJoinCycle | A real two-thread join cycle rejects one edge and both threads terminate |
| ThreadCapacity | Four live slots, recoverable fifth-thread refusal, then joins and complete frame recovery |
| ThreadCreationRollback | Forced failure at each of four stack allocations and the TLS allocation, handle exhaustion, no leaked frames/handles and a successful retry |
| ThreadFault | A child's UD2 terminates its component; a fresh normal component runs |
| ThreadGuardLow / ThreadGuardHigh | Child stack boundary writes produce PF error 6 at the expected addresses |
| ThreadBadReturn | A child's attempt to return on the main thread's mapped stack is rejected |
| ThreadProcessExit | A child can terminate the whole component while its parent is waiting |

The raw thread fixture scans all sixteen KiB of a new/reused stack and checks its initial TLS payload sentinel before use. Kernel assertions verify handle closure, join/reap counts, scheduler progress, restored kernel FS/CR3 and physical free counts. Recovery after faults exercises subsequent component execution.

## Consequences and next work

The kernel can now schedule bounded native sibling threads and perform blocking join. These mechanisms support further runtime work, but they do not implement stop-the-world GC, managed TLS, finalization or managed exceptions.

Version 0.0.7 adds [events/deadlines and idle](M2-Events-and-Deadlines.md). Next investigate the actual runtime target/bootstrap, compiler TLS, coordinated GC suspension and validated fault delivery. Dynamic kernel stacks, larger quotas, cross-process scheduling and SMP remain separate extensions.

Version 0.0.17 adds a separate [static compiler TLS page and GS module vector](NativeAot-Compiler-Tls.md), preserving the raw FS layout. It still does not implement dynamic TLS callbacks or managed thread attachment.
