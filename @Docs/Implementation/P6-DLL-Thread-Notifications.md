# P6.4 DLL thread notification work

Date: 2026-10-02. ABI47 cooperative thread notifications are implemented and guest-tested. Full regression gates are in progress; P6.4 and DLL TLS remain open.

## Required behavior

The native thread trampoline must execute DLL thread attach on the new thread before entering its workload. Thread detach must execute on the exiting thread after its main-module TLS/runtime cleanup and before THREAD_COMPLETE. Kernel-provided thread identity and kernel-owned notification state must determine completion; writable TLS cannot authorize it.

Notifications use a readonly lifecycle plan and run outside the kernel. They are serialized with load/unload/process lifecycle. The plan pins all callback images and dependencies, but an ordinary live thread is not a permanent DLL reference. Explicit unload still requires the caller to quiesce code users. Thread notification return values do not turn a failed process attach into success; the two paths remain distinct.

Thread creation needs an explicit cooperative lifecycle contract. Raw/native paths must retain their existing behavior when no notification contract applies. A cooperating thread cannot bypass required cleanup with raw THREAD_EXIT or an early THREAD_COMPLETE. Creation rollback must release every observer, identity, stack/TLS page and unpublished start slot. Suspended creation must not execute notifications before resume. Detached cleanup must finish before automatic reaping.

The implementation still rejects DLL TLS directories. It must not claim static/dynamic per-DLL TLS from thread notifications alone. The existing single-main-module compiler TLS and dynamic initializer/destructor machinery remains separate until the multi-module TLS backend is implemented.

## Acceptance to establish

- Real DLL callbacks run with CPL3 and report kernel-confirmed identity of the new/exiting worker.
- Joined, detached and initially suspended native wrappers preserve notification order and exact teardown.
- Main-module TLS constructors/workload observe completed DLL thread attach; runtime notification precedes DLL detach.
- A callback cannot mutate the readonly plan or consume another thread's completion identity.
- Raw/early completion cannot resume peers with missing required cleanup.
- Serialized load/unload/notification work releases user-space gates before yielding and never executes callbacks with kernel interrupts disabled.
- Existing no-entry DLL, NativeAOT thread/GC, last-error/errno and ownership tests remain valid.

`tests/User.X64/library_thread_entry.c` is a genuine native DLL fixture. The Windows normal/suspended-thread oracle passed callback identity/order checks, and is available through --dll-thread-reference. The reusable builder and host test are implemented. The same fixture now runs in the guest through the real native trampoline. Entry-bearing DLLs permit new cooperative workers; raw creation without the lifecycle flag remains unsupported, and loading new entry-bearing graphs while peers already exist is still a separate limitation.


## ABI47 implementation and observed results

A private creation flag opts a worker into kernel-owned notification state. The trampoline requests DLL thread attach before main-module TLS entry; orderly exit runs main TLS cleanup and its runtime notification, then DLL thread detach, then THREAD_COMPLETE. Readonly lifecycle plans serialize callbacks, pin their graph and use the actual worker's generation-bearing identity. Suspended creation does not execute the trampoline before resume.

Notification return values are ignored as required for thread reasons. Kernel completion checks reject raw or early completion when DLL cleanup is required and terminate the component before peers can resume. Existing raw/lazy-TLS native paths with no DLL work retain their old behavior. Unknown creation flags are still rejected; the negative tests now use the next reserved bit after the new opt-in bit was allocated.

Foreign-owner lifecycle contention returns BUSY. Native wrappers release their start-slot gate, yield and retry; same-owner creation during a callback returns DEADLOCK. Temporary readers remain independently releasable when they cannot require nested DLL detach. This does not permit nested load/unload mutations or static DLL TLS.

Both 128/512 MiB profiles passed real callback identity checks, joined/suspended/reference-detached/direct-detached wrappers, actual main-module TLS constructor/destructor witnesses, runtime-notification ordering, teardown, and raw/early completion containment. The Windows reference passed normal/suspended callback identity and order. The full ABI47 matrix is running. Its initial host result is 42/43 because NormalExitReapsPipeIndependentDescendant failed with the former generic diagnostic; result capture was improved without changing timeouts or ownership assertions, and must be rebuilt/retested after the running pipeline.


### Follow-up regression findings

The broad runtime-config run exposed a plain-profile COM fixture with 129 unwind entries against the unchanged 128-entry limit. Function packaging alone did not remove needed code. The shared lifecycle executor was refactored to use one request/dispatch frame for shutdown and thread notifications, retaining the shutdown BUSY result and thread-only yield/retry behavior. The rebuilt COM image has 128 entries and passes admission without increasing quotas. A later mode168 failure was another reserved-flag test using the newly assigned opt-in bit; it now probes the next reserved bit. The corrected config/managed matrix is running; no final success is claimed yet.


The corrected config matrix and all managed boot profiles passed after the transport-frame and reserved-bit fixes. The final host suite passed 44/44 groups (including the new genuine DLL TLS Windows reference) with the original deadlines/ownership checks. This does not establish a root-cause fix for the previously intermittent host process failure. The mandatory final kernel rerun is still running; latest evidence remains partial until it finishes.


ABI47's corrected config/managed, final 44/44 host groups and all 20 kernel cases passed; artifacts/p6-dll-thread-checkpoint.json is finalized. ABI48's TLS work additionally moved required notification-page/handle allocation into atomic worker creation after a quota test reproduced post-creation OOM. New rollback checks cover all twenty required page boundaries. Its broad regression matrix is in progress.
