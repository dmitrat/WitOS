# P1.5 native DWORD thread identity

The kernel assigns each published user thread a nonzero monotonically increasing native DWORD ID. This ID is separate from the generation-bearing ThreadId used for ownership and from duplicated thread-reference capabilities. Native IDs are not recycled across components or slot reuse; exhaustion is detected before allocation and never wraps.

Experimental user ABI v21 provides a scalar native-ID query with zero arguments. Thread snapshot v2 grows to 64 bytes and appends NativeId plus a reserved zero word; the original ownership ThreadId and field offsets remain unchanged. Snapshot copy-out retains full-buffer validation and IF-disabled publication.

PalGetCurrentOSThreadId and direct/readonly-import GetCurrentThreadId use the same kernel native ID. They do not consult writable FS/GS hints and do not truncate the 64-bit ownership token. Mutex, TLS cleanup, exit notification and join/close lifetime checks continue to use the generation-bearing identity/capabilities.

Tests explicitly separate native ID from ownership: PAL snapshots compare NativeId with actual kernel records, slot reuse produces increasing native IDs, and a kernel allocator boundary check accepts UINT32_MAX once then rejects exhaustion without altering the output. Guest direct/import checks survive forged raw TLS hints. Existing detached and last-error tests had used the old PAL ID as a join/close token; they now obtain the actual ownership identity from the kernel while separately checking native ID consistency. Their denial/busy/stale-handle assertions remain in place, and native numeric IDs are rejected as handles.

Validation passed: Release build, source audit/hosted probe, target/source/readiness, all 20 boot scenarios and four runtime-config profiles (227 user groups / 54 expected contained faults each). The minimal/broad strict boundaries are 46/52 unresolved symbols. The native DWORD ID never became an ownership token; detached and last-error lifecycle assertions remain enforced. This change does not implement thread context capture, scheduler priorities, alertable waits or managed runtime attachment.
