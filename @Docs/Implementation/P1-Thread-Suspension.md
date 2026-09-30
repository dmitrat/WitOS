# P1.8 counted native thread suspension

User ABI v28 adds Suspend/Resume over real thread-reference capabilities. Native SuspendThread/ResumeThread use these operations, including previous-count return values, a maximum of 127 and ERROR_SIGNAL_REFUSED on overflow. The native Windows reference confirms those values. This is the execution-control prerequisite for later context mutation and GC rendezvous; PalHijack and PAL context get/set/restore remain unresolved.

## Scheduler and ownership

SUSPEND_RESUME is a separate reference right. DuplicateHandle maps the corresponding Windows access bit; QUERY/GET_CONTEXT/SET_CONTEXT do not imply it. The current-thread pseudo handle addresses only the caller. Wrong-type, stale, closed and insufficient-right references fail before mutation. Counter increment/decrement is serialized with interrupts disabled, and overflow leaves the count unchanged. Resume at zero succeeds with previous count zero.

Suspension is separate from the thread's ready/waiting state. A positive count prevents dispatch of user code. Events, close notifications, joins and deadlines can still complete their kernel-side work. A completed waiter becomes ready but remains ineligible until its count reaches zero. Resume does not turn a still-waiting thread into ready. Closing a reference does not decrement the counter or implicitly resume a thread. Counter state is cleared on preparation, reaping and component teardown.

Self-suspend saves its successful return result, becomes ready-but-suspended and dispatches another eligible thread. If none exists, the kernel idles with zero FS/GS as before and retains the existing PIT budget. All-suspended execution is not mistaken for successful process termination, and the active kernel stack is never freed.

## Discovery and context meaning

Thread-reference snapshot v3 is 56 bytes and reports suspend count plus a suspended state. Register snapshot v2 retains its 720-byte size, adds suspend count and separate SUSPENDED/SERVICE_ACTIVE flags. It retains the underlying ready/waiting state. Pending kernel waits may still update their return result while suspended; this is explicitly reported as service-active until completion. No PAL-safe-redirection or full stable-context promise is inferred from suspension alone.

The upcoming set-context path must validate target lifetime, actual suspended state, owning stack, selectors, flags and FP masks before mutation. It must preserve upstream's refusal to redirect service-active/exception-active contexts. This slice does not enable arbitrary context mutation, debug-register programming, exception dispatch or runtime attachment.

## Evidence

A native Windows helper exercises counted suspension, overflow without increment and decrement to zero. It is host-only and links the system libraries there. The exact WitOS native suspend objects are included in the guest fixture without Windows implementation libraries.

Guest modes 91-93 cover direct/import bindings, denied rights, reserved arguments, nested suspend/resume, the real count limit, event signal and event close while suspended, no user progress across explicit yields, completed-wait state, handle close without implicit resume, event generation reuse, exited/stale references, worker-slot reuse, self-suspend with a separate resumer, no-compiler-TLS calls and the all-suspended idle/budget path. Kernel model tests exercise the actual monotonic sleep/event expiration functions with a suspended waiter; this avoids assuming that every finite guest deadline must park before it expires. Existing ordinary deadline/APC regressions remain intact.

Validation passed: Release build, all 20 ordinary boot scenarios, runtime-audit/probe/target/source/readiness, native Windows reference and four runtime profiles with 253 user groups and 55 expected contained faults each. Full native archive: 125 members; config probe: 41 exact objects. The COM/context fixture is 36352 bytes with 117 plain unwind records. The minimal/broad link remains 14/20 unresolved; P1.8 is open. The production GS context-storage object remains excluded from the intermediate fixture until real handler/unwind execution exists, as documented in P1-Context-Storage. Suspension does not close that acceptance gap.

Logs: artifacts/p1-suspend-config.log, artifacts/p1-suspend-test.log, artifacts/p1-suspend-audit.log, artifacts/p1-suspend-probe.log and artifacts/runtime-suspend/reference.log.

References: [SuspendThread](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-suspendthread), [ResumeThread](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-resumethread).
