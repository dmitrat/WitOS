# P6.4 DLL process lifecycle

Date: 2026-10-02. Initial process lifecycle: ABI 45; normal shutdown: ABI 46, boot ABI 4. Implementation slice; P6.4 is not complete and CoreCLR has not started in the guest.

## Contract

Native LOAD, UNLOAD and reader release can opt into a user-space lifecycle plan. The kernel maps and binds a new graph before producing a component-owned readonly page containing the exact entrypoints, module bases and dependency order. The page and its generation-bearing lifecycle handle remain owned until completion. Generic memory operations and CLOSE cannot mutate or release the plan. Only the initiating kernel thread identity may acknowledge completion; writable TLS is not identity authority.

The native adapter validates the complete readable/nonwritable plan and every executable entrypoint before calling any user code. Entry calls receive the actual module base, process attach (1) or detach (0), and a null reserved pointer for explicit load/free. Dependencies attach first; completed instances detach in reverse attach order. Repeated loads only increment references. A false attach result detaches the failed module and previously entered new modules in reverse order, then aborts the new graph. Existing module references are preserved; arbitrary side effects of callbacks are the DLL's responsibility. INITIALIZATION_FAILED maps to ERROR_DLL_INIT_FAILED in the hosting PAL.

A pending lifecycle is serialized and pins its affected graph. Load/find/unload reentry returns BUSY instead of parking under a loader lock. Temporary unwind readers owned by the callback can be acquired/released; a release requiring unrelated nested detach is rejected. Ordinary raw release cannot silently skip required detach. Final reader release also drives a protected detach plan when it owns the last graph root. No entrypoint executes in the kernel.

Completion is a native lifecycle assertion, not proof against malicious code in the same component. A callback fault/raw exit is abrupt component termination. Normal CRT/process shutdown is connected by the ABI 46 extension below.

## Deliberate current boundary

Entry-bearing graphs are admitted only when one live user thread exists. Thread creation remains explicitly unsupported while such a graph is loaded or a lifecycle is pending. This avoids pretending that DLL thread notifications or per-module static/dynamic TLS are implemented. Non-entry DLLs retain their existing multithreaded behavior. TLS directories, forwarded exports, delay/bound imports and nested loader mutations remain unsupported. These limitations must be lifted with real lifecycle/OS support before full CoreCLR startup can be accepted.

## Evidence

The fixtures are real linker-generated DLLs with nonzero entrypoints, actual imported data/functions and internal relocations. An x64 helper reads CS so each callback checks CPL3 as well as its actual image base. Guest tests cover attach-once, dependency ordering, reverse detach, child attach failure, reader-delayed detach, protected plan memory/handle rules, invalid completion, nested mutation refusal and allocation failure through image/plan boundaries. The first full guest 128/512 MiB run passed; the expanded failed-parent and live-peer admission cases are in the final matrix.

The Windows LoadLibraryEx/FreeLibrary reference passed attach/refcount/order and failed child/parent cleanup checks. This behavior follows [Microsoft's DllMain contract](https://learn.microsoft.com/en-us/windows/win32/dlls/dllmain), including detach after a failed process attach. ASan also passed the 8206 full-PE import cases and 2582 descriptor units. All ABI45 host/kernel/source/runtime/managed gates passed; artifacts/p6-dll-lifecycle-checkpoint.json binds their source hashes and authoritative acceptances.


## Normal process shutdown (ABI 46, verified slice)

`wit_native_process_shutdown` now invokes the real DLL shutdown service after main TLS destructors, atexit and runtime/thread notification. Module code remains available throughout those earlier callbacks. The common C lifecycle executor has no path, filesystem or heap dependency; the source-build gate now requires its compilation into the actual runtime archive and exact-byte archive-member verification. The full source build compiled the helper and verified its exact archive member.

Kernel SHUTDOWN rejects active lifecycle work or module readers without changing references. It prepares a readonly reverse-order plan for every initialized DLL, retaining the entire graph while user-space callbacks execute. The shared executor passes process detach with a nonnull reserved value. Completion closes the DLL namespace, drops external references and releases all images. Empty and repeated shutdown are successful without repeated callbacks. LOAD/FIND cannot resurrect the namespace. Callers quiesce workers/readers and execute shutdown from the main image; raw kernel exit/fault remains abrupt.

The guest CRT fixture passed at 128/512 MiB. A witness in the main image survives DLL unmapping and proves TLS -> atexit -> runtime notification -> reverse DLL detach. It verifies reader BUSY, unchanged load references on refusal, reserved-dependent callback results, stale handles, closed namespace and idempotence. The existing real CRT byte routines are linked into the enlarged fixture; source-path-based object names prevent production/test filename collisions.

A separate Windows child loads the same DLL graph, writes to a parent-owned shared-memory witness and calls the real ExitProcess. The observed detach order and nonnull reserved semantics match the guest. The full PE/descriptor ASan tests also passed. All kernel/reference/source/config/managed gates passed. A standalone RuntimeCpuFixture link initially missed the common executor object; its explicit object list and source manifest were corrected, then config and managed gates passed. The failed log is retained. artifacts/p6-dll-shutdown-checkpoint.json binds source/gate hashes and authoritative acceptances. No complete P6.4/CoreCLR startup claim is made. DLL TLS and thread notifications remain open, and the entry-bearing process-only admission restriction remains in force.


## Cooperative threads (ABI47)

The temporary creation restriction is now lifted for native workers that opt into the genuine DLL notification contract. [Thread notification implementation and evidence](P6-DLL-Thread-Notifications.md) covers attach-before-workload and detach-after-TLS/runtime cleanup, actual worker identity, suspended/detached lifecycle and containment of cleanup bypass. Loading a new entry-bearing graph while peers already exist, DLL TLS and complete hosting/CoreCLR startup remain open. Full ABI47 regressions are in progress.
