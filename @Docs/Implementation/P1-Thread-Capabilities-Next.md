# P1.5 next boundary: native thread capabilities and waits

Status: implementation requirements identified; no thread-reference or alertable-wait completion is claimed by this document.

The actual CoreLib Thread.NativeAot.Windows.cs GetOSHandleForCurrentThread path calls DuplicateHandle(GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(), ..., DUPLICATE_SAME_ACCESS). The kernel's current thread identity is a generation-bearing 64-bit handle. It must not be cast into an OS context capability or truncated into a supposedly global 32-bit Windows thread ID.

Required next implementation:

1. Add a kernel-owned native 32-bit thread ID with checked exhaustion, distinct from the generation-bearing ownership identity. Keep the latter for mutex/TLS/exit ownership. Align direct GetCurrentThreadId and PAL logging ID with the real native-ID field, through a versioned whole-buffer snapshot or equivalent checked discovery.
2. Represent duplicated native thread references separately from join capabilities. Reference lifetime must survive thread execution exit/reaping without retaining or later dereferencing freed stack/TLS pages. A terminal reference must not bind to a reused thread slot.
3. Resolve current-process/current-thread pseudo handles to the current kernel record only when an operation requires a real capability. Never return the writable TLS ID as a capability. Validate destination buffers before granting/copying an output handle; roll back failed grants and preserve ownership/generation.
4. Keep close of a duplicated reference independent from join/reap and execution lifetime. Preserve existing busy/ownership rules for legacy join handles. Bound references by prototype handle quotas and publish terminal state under IF-disabled serialization.
5. Add meaningful query rights and live/terminal behavior before exposing GetThreadPriority or context operations. The sole supported scheduler priority is normal; do not invent dynamic priority behavior. Context capture/set/suspension remain P1.8 requirements, not capability-name placeholders.
6. Implement actual alertable-wait behavior before binding WaitForMultipleObjectsEx's reached CoreLib path. CoreLib WaitHandle.Windows.cs passes alertable=true even for ordinary waits. Existing PalCompatibleWaitAny rejects alertable/reentrant operations; merely forwarding that call or silently ignoring alertability does not satisfy P1.5. Preserve whole handle-array validation, event-generation snapshots and deadline/close ordering.

The current source adaptation of Thread::Construct keeps an absent OS/context handle INVALID_HANDLE_VALUE. Change that policy only after real capabilities exist, preserving full upstream GC detach dependencies. Do not replace them with successful native probes.
