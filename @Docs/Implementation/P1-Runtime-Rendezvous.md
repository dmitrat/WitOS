# P1.8.g: actual runtime rendezvous and foreign GC roots

The full guest runtime workload now performs compacting collections initiated by a worker, preserves roots on the main thread and another managed worker, and executes the real upstream context-redirection path. It also collects while a detached thread is already running its native TLS destructor but has not yet entered RuntimeThreadShutdown.

## Root-walk correction

The first worker-initiated collection failed although collection on the main thread passed. Thread::GcScanRoots previously had no enclosing native unwind scope. The checked unwinder requires kernel-owned authority for a foreign stack; an implicit current-thread scope cannot authorize it.

The WitOS source overlay retains the pinned full GcScanRoots body and wraps it with a stack lease. A foreign target acquires one counted kernel suspension after the upstream runtime rendezvous has stabilized managed state. The lease covers CrossThreadUnhijack, StackFrameIterator construction, enumeration and all uses of original saved-register/root locations. The lease is released before the matching resume. Failure terminates the component; it never continues with an unprotected stack. Current-thread walks also use an explicit scope.

This does not replace the runtime suspension protocol or infer identity from writable TLS. It adds the kernel lifetime guarantee needed by the existing checked unwinder. The first worker-driven collection then passed all four RAM/CPU profiles at both image bases (artifacts/p1-foreign-gc-fixed.log).

## Workload and acceptance

Each image execution retains the nine-worker lifecycle/rollback workload from [P1-Worker-Lifecycle](P1-Worker-Lifecycle.md), then adds two worker pairs. There are thirteen completed worker lifecycles per execution, or 104 across the eight-run matrix.

1. A worker forces a blocking compacting collection while Main waits in native code with live local/static roots and an array. It verifies its own live object/array, refills its allocation context and completes real detach.
2. A managed worker spins on a volatile field with a live Box and byte array. A second managed worker forces a compacting collection, then releases the spin loop. Both verify their roots and exit through the real runtime lifecycle. Native observers require at least one actual PalHijack attempt and a real redirection or return-address hijack; a cooperative poll alone cannot satisfy the test.
3. A detached worker pauses inside its actual compiler-generated C++ TLS destructor. A second worker collects while the exiting thread remains listed in ThreadStore. The destructor confirms that the collector reset its allocation context to null/null before proceeding. RuntimeThreadShutdown then completes actual detach. This case checks the post-GC context state separately from the nonempty-context cases; their earlier assertions are retained.
4. Main performs another collection and checks its original roots, array sentinels and the final worker-created object before returning 42. The kernel verifies complete handle/event and page/table teardown at both image bases.

The spinning code was inspected in the linked PE: the Box is held in RBX and the array in RAX across the loop. Both are read after release, so this workload exercises nonvolatile and volatile register roots. Disassembly is saved in artifacts/p1-spin-disassembly.log.

## Active service-frame refusal

Before the collection scenarios, a real attached worker parks in a native event wait. The fixture confirms WAITING, takes an outer counted suspension and obtains a complete context with CONTEXT_SERVICE_ACTIVE. Under the real ThreadStore lock it calls PalHijack. The adapter must reject this frame, retain every context byte, preserve caller last-error/errno and balance only its own nested suspend/resume. The outer suspension remains at one and is explicitly released by the fixture.

Private diagnostic counters record attempts, kernel-observed instruction-pointer changes, return-address hijacks and unsafe snapshots. Reads occur before/after quiescent test phases, not as general concurrent telemetry. They do not control runtime behavior, grant capabilities or supply a substitute collector. The tested profiles reported 2 attempts, 1 real context redirection, 0 return-address hijacks and 1 service-frame refusal per execution. The chosen upstream redirection route is proven; no claim is made that this workload forced the alternative return-address fallback.

## Evidence and profile

The expanded workload passed qemu64 at 128/512 MiB, Nehalem at 256 MiB and max at 256 MiB, each at two image bases. See artifacts/p1-exit-gc-boot.log and the hash-linked artifacts/x64/runtime-boot/acceptance.json. The tested image has 1,003,520 mapped bytes, 2,819 unwind entries and 648 compiler-TLS bytes; its SHA-256 is 0add9e777e7d1099d12624a7ed1b73bccec8c57c031f9b2c0310db22c6dc78dd. OS imports and PE TLS callbacks remain absent. Admission quotas are unchanged.

Final whole-P1 regressions and the [completion audit](P1-Completion-Audit.md) passed; P1.8.g/h and all P1 are complete in the documented profile. See root PLAN.md, artifacts/p1-final-*.log and artifacts/p1-completion-evidence.json. The native scope/GC changes are recorded in startup-provenance.json and the source-build overlay inventory.

The profile remains one x64 CPU, x87/SSE state, workstation non-concurrent GC, standard CoreLib and a static NativeAOT image. This is not SMP, AVX state support, comprehensive managed exception/finalizer/Thread API acceptance or CoreCLR/JIT compatibility; those retain their existing P3/P5/P6 criteria.
