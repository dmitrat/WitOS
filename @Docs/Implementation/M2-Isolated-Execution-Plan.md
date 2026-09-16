# M2 — First isolated execution slice

Status: original plan, now implemented for the controlled fixture in [WitOS 0.0.4](M2-Isolated-Execution.md). The [NativeAOT inventory](../RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md) informed this work; the hosted probe remains separate evidence.

## Observable result

A separately built native component runs unprivileged in its own address space, writes through an explicitly granted console handle and exits. An invalid component is terminated while the kernel starts another component successfully.

This proves the hardware/ABI boundary before introducing a managed runtime.

## Initial scope

1. Introduce an execution-context object with separate user/kernel stacks, address-space ownership, saved CPU state, lifecycle and a private handle table.
2. Create a fresh user address-space root. Shared kernel mappings remain supervisor-only; private user branches must not modify the kernel's mapping hierarchy.
3. Map a controlled native test image read/execute, data read/write/non-executable and a guarded user stack. Newly exposed physical pages must be zeroed.
4. Reserve at least the low 64 KiB from user mapping until the runtime null-area contract is selected.
5. Add ring-3 selectors, TSS kernel-stack handling and a checked transition to unprivileged execution.
6. Add a versioned native call boundary with only the operations below.
7. Distinguish user faults from kernel faults and reclaim the failed component's resources.
8. Retain timer control so an uncooperative component cannot keep the kernel from enforcing a test execution budget.

The current two-worker demonstration and fatal-only exception path cannot simply be reused unchanged: incoming user frames have a user RSP, execute under another CR3 and require per-context kernel stacks.

## First call semantics

| Operation | Required behavior |
| --- | --- |
| Query ABI | Return version and supported features without granting authority |
| Write terminal | Validate a process-local handle, write rights and the complete user buffer range |
| Exit | End the current component, release its transient handles and owned resources |

Exact numbers, register assignments and structures will be committed with the native stub and kernel tests. This keeps the immediate ABI small while the runtime inventory guides its extension.

For output, reject invalid/closed/wrong-type handles, inaccessible or overflowing pointer ranges and oversized requests with defined status values. Copy user data through a checked boundary; never treat a user pointer or numeric ResourceId as authority.

These are native mechanisms. The kernel should not know about Task, managed objects, BCL classes or a particular application framework.

## Required VM evidence

| Case | Acceptance |
| --- | --- |
| Normal component | Executes in ring 3, outputs through its granted handle, exits with a known status |
| Kernel read/write | User fault is contained; kernel memory canary remains intact |
| Privileged instruction | Component is stopped; kernel remains responsive |
| NX and user-stack guards | Correct fault is attributed to the component |
| Invalid syscall/handle | Explicit error, no kernel panic or authority escalation |
| Invalid output buffer | Full-range validation, including boundary crossing and integer overflow |
| Two components | Private data cannot be accessed through the other component's handles or mappings |
| Resource teardown | Repeated runs restore owned-page/handle counts |
| Zero-fill on reuse | A new component cannot read the previous component's data |
| Infinite loop | Kernel timer budget stops it and a subsequent component still runs |

Keep all existing 17 M1 scenarios as regression coverage.

## Runtime-driven extensions after this slice

- Per-address-space virtual reservation, commit, decommit, protection and release.
- Dynamic threads, TLS, exit/join and cleanup.
- Atomic wait/signal/timeout contracts with no lost wakeups.
- Process-local GC rendezvous/context operations and correct memory ordering.
- Validated runtime fault delivery and context restoration for managed exceptions.
- Explicit module/TLS/unwind initialization for the selected NativeAOT target.

These are pending M2/M3 work. A passing Windows-hosted probe is not their implementation.

## Limits

Keep the first target to one x64 QEMU CPU and known native images. General untrusted PE loading, demand paging, SMP, broad device access and a public stable SDK are outside this slice.
