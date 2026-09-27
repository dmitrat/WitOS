# Native thread exit notification

**Status:** Implemented in WitOS 0.0.35; ABI v17 unchanged.

This adds the private user-space notification needed before connecting real NativeAOT thread attachment. It does not implement PalAttachThread, PalInitComAndFlsSlot, ThreadStore attachment, RuntimeThreadShutdown or collector startup.

## Why a separate notification

The pinned .NET 10.0.8 Windows PalMinWin.cpp associates the runtime Thread pointer with an FLS callback. FiberDetachCallback calls the real RuntimeThreadShutdown outside the loader lock. The WitOS profile has no Windows FLS, fibers or COM implementation.

In upstream startup.cpp, OnProcessExit records the thread performing process shutdown. RuntimeThreadShutdown checks this state before attempting ThreadStore detach. Ordinary Thread::Detach eventually calls the real collector's FixAllocContext. Running a substitute callback and calling that managed detachment would conceal a missing GC dependency.

WitOS therefore implements only the underlying native notification here. The fixture callback checks native ordering and identity; it is not named or linked as RuntimeThreadShutdown. The complete upstream methods and unresolved PAL requirements remain intact in the source-built archive.

## Contract

- One callback and opaque context per native thread, stored in its existing kernel-managed compiler TLS page. There is no dynamic allocation, process-wide callback pool, new syscall or public FLS API.
- Registration requires published/initialized native TLS and a whole successful kernel thread query with a nonzero identity and compiler TLS. Those checks precede access to GS-backed notification storage. Unavailable TLS returns DENIED; null/data/readonly non-code pointers return BAD_ADDRESS; duplicate registration returns BUSY. Registration during/after cleanup returns CLOSED.
- The callback must belong to initialized RX, nonwritable image bytes. Its context is opaque caller-owned data, whose lifetime must extend through notification. The adapter captures a kernel-provided generation-bearing identity and checks it again on notification; writable FS/GS fields do not supply thread identity.
- Orderly worker exit is C++ TLS destruction, notification, then the actual kernel THREAD_EXIT. This applies to callback return and explicit wit_native_thread_exit, including detached workers. The compiler TLS page and owning thread resources remain live through callback execution.
- Orderly process shutdown is current-thread TLS destruction, the complete LIFO atexit queue, current-thread notification, then publication of shutdown completion. No more atexit registrations are accepted during notification. Workers must already be quiesced by the caller.
- Notification removes its callback/context before invocation and marks execution in progress. Reentry fails fast; registration cannot schedule more cleanup. A completed notification is idempotent. Process shutdown cannot report completion or recursively restart while its notification is running.
- No shared gate is held across notification. Callbacks may yield or park. The adapter itself preserves native last-error; callback changes to caller-owned state are not undone.
- New threads receive fresh zeroed compiler TLS. Notification storage therefore follows the same lifetime as the actual thread rather than requiring a separately recycled registry slot.
- Raw thread/process exits and faults remain abrupt. Plain TLS destruction alone does not deliver notification; lifecycle entry points control its ordering. Existing native_start return-to-exit transport remains raw.

These are component-private lifecycle rules, not a security boundary between native code sharing one address space. Direct invocation of the internal notify helper is reserved for lifecycle drivers and adversarial tests.

## Guest evidence

The existing native process-exit fixture now registers a main-thread notification and checks TLS/atexit/notification order, invalid callback rejection, duplicate registration, completed-call idempotence and native error preservation. Existing process callback faults/raw exit still bypass the later notification.

Three additional required groups cover:

1. NativeThreadExitNotify: four rounds of three concurrent joined workers, including explicit exit and callback return, yielding notifications, unique kernel identities across reuse, original and relocated image bases, and a raw-thread-exit bypass case.
2. NativeThreadExitDetached: the same twelve-worker lifecycle at both image bases. All three notifications rendezvous concurrently; the parent observes live private identities, denied joins and busy closes before releasing callbacks, then waits for automatic reaping.
3. NativeThreadExitFailFast: recursive notification, recursive process shutdown from notification, notification before TLS cleanup, and successful subsequent component restart.

Every case checks component page reclamation. Successful worker cases also require exact creates/joins/reaps, unchanged owned-page accounting and empty handle/event tables. The fixture is native and import-free; there is no managed runtime hidden in the test.

Local validation:

- Release solution build: no warnings or errors.
- test: all 19 QEMU scenarios passed; successful ordinary boots require 178 user groups and 51 contained hardware faults.
- A final 256 MiB boot passed all 178 groups after strengthening the cross-generation comparison to retain the entire previous batch until validation completes.
- runtime-audit: all 58 canonical-byte source pins verified.
- runtime-probe: eight hosted Windows groups passed.
- runtime-target, also run by runtime-config: four native-host groups passed; the deliberately incomplete package links still fail strictly.
- runtime-source, also run by runtime-config: 83-member upstream/WitOS native archive and 11-member minipal archive verified, with the Windows reference passing all four groups. The WitOS boundary retains 93 unresolved requirements, including five GC environment methods.
- runtime-config: both 128/512 MiB QEMU boots passed 193 required user groups and 51 expected contained hardware faults each. The fifteen additional groups still exclude collector startup and ThreadStore attachment.

The process/notification fixture is 10,752 bytes with 36 structural unwind entries on the local compiler. These entries do not provide exception unwinding.

## Remaining integration

Connect PalAttachThread to this notification only with the actual RuntimeThreadShutdown dependency preserved. Explicitly select the WitOS startup/diagnostic/COM policy instead of claiming Windows initialization succeeded. Thread construction, ThreadStore list publication and collector allocation-context cleanup still need their real implementations and guest evidence. Standard upstream CoreLib and the eventual unchanged portable IL application contract remain unchanged.
