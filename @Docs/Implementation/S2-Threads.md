# S2 — threads (ABI v65)

Plan step S2 gives the libc its threads ([RFC 0011 v3 §7.3, §7.4, §9.3](../RFC-0011-Kernel-Architecture-and-ABI.md)):
musl's pthreads run over the kernel's threads of the one form, its futex waits over kernel events, and a thread's
exit is served by the kernel after the thread stopped. The first libc program creates, joins and detaches threads,
takes a mutex from two, waits on a condition variable, runs a key destructor, keeps thread-local storage and errno per
thread and posts a semaphore; libc-test's `pthread_cond`, `pthread_tsd`, `tls_local_exec`, `pthread_once-deadlock`,
`pthread_rwlock-ebusy` and `pthread_condattr_setclock` pass on both ISAs.

## What changed

**The kernel: an exit request (S2.1, ABI v65).** musl's exiting thread holds the thread list lock until the kernel
clears the lock word and wakes its waiters — Linux's `CLONE_CHILD_CLEARTID` — because the thread's own stack may be
freed by a joiner the moment it is known to be gone; nothing a thread does for itself can replace that.
`THREAD_EXIT`'s third argument, zero until now, is `WitThreadExitRequest` (`thread_reference.h`, 24 bytes: `Version`
1, `Size`, `ClearAddress`, `Event`): once the thread no longer runs — on its exit path, before the reap releases the
stack reservation the word may lie in, in the same uninterrupted kernel path — the kernel writes zero to the 4-byte
word and sets the event. The request is validated whole before the exit (a foreign version `UNSUPPORTED`, a wrong size
or an unaligned word `INVALID_ARGUMENT`, an unwritable word `BAD_ADDRESS`, an event the caller does not hold with
`SIGNAL` as the handle check says), and a refusal returns with nothing served. The thread form fixtures of both ISAs
(`tests/User.*/threads2.asm`) exercise the refusals and the served request. No call number, status or right changed;
RFC 0011 §5's rule applies: no composition of existing calls provides a store and a signal after the caller's own
death, and musl's thread list is the layer-2 consumer with the test.

**Threads (`src/Substrate/libc/thread.c`).** musl's `__clone(start, stack, flags, args, &tid, thread pointer,
&thread_list_lock)` is WitOS's: a `THREAD_CREATE` of the one form, started suspended, with the entry
`__wit_thread_entry`, the launch block (start function, argument, thread pointer) placed below musl's start arguments
on the new stack and named as the stack pointer and the argument, and the thread pointer as the raw TLS base (the
FS base on x64; on ARM64 the trampoline writes TPIDR_EL0 itself, the thread's own register). The library numbers its
threads (the main thread is 1), maps the kernel's identity (`THREAD_QUERY`) to that number, writes the parent's copy
of the id, records the clear-on-exit word, resumes the thread and closes the kernel handle: musl tracks the thread's
life itself. `gettid` and `set_tid_address` serve the main thread and the created ones; `__set_thread_area` on x64
is `THREAD_SET_TLS`. A thread's `SYS_exit` is `THREAD_EXIT` with the exit request naming its clear word and the futex
event of that word; `__unmapself` (a detached thread's end) names the stack's reservation as well, which the kernel
releases once the thread stopped — musl's `clone.c`, `__unmapself.c` and the x64 `__set_thread_area` leave the build.

**The futex equivalent (`futex.c`).** The kernel waits on events, not on words, so the library keeps the word. A
waiter takes one of eight slots, each with its own auto-reset kernel event created on first use, under one lock: it
checks the word there and parks on the slot's event with the deadline (relative for `FUTEX_WAIT`; `FUTEX_WAIT_BITSET`
absolute on the monotonic clock, or on the real-time clock through the kernel's UTC). `FUTEX_WAKE` finds the slots of
that word under the same lock, marks up to the count woken and sets their events; `FUTEX_REQUEUE` and
`FUTEX_CMP_REQUEUE` rename the remaining slots' word, as Linux moves the waiters. A wake between the check and the
park leaves the event's token in place, so the park returns at once: no wake is lost and no waiter of another word is
woken; a token a wake left after a deadline is reset before the slot serves again. The word the exit request clears
(musl's thread list lock, which every thread names at its creation and `set_tid_address` names for the main thread) is
the exception: the kernel sets one event after the thread is gone, so that word's waiters share one event and wake one
another on, as musl's `__tl_sync` and `__tl_unlock` do on Linux, where the kernel also wakes one. Nine events in all,
within the root task's quota of sixteen; the private flag changes nothing and priority inheritance is `ENOSYS`.

## Tests

- `tests/User/libc_hello.c` grows to 74 checks: two threads incrementing a mutex-protected counter a thousand times
  each with yields, their `pthread_exit` values joined, the key destructor run twice, thread-local storage and errno
  kept per thread, a condition variable signalled to a waiter, a detached thread posting a semaphore, `pthread_self`.
- libc-test gains `pthread_cond`, `pthread_tsd`, `tls_local_exec`, `pthread_once-deadlock`, `pthread_rwlock-ebusy`,
  `pthread_condattr_setclock` (82 tests selected).
- The kernel self-test's thread form fixtures cover the exit request on both ISAs; the frozen line's
  `worker_lifecycle.cpp` now expects `BAD_ADDRESS` for an unreadable third argument of `THREAD_EXIT`, where it
  expected `INVALID_ARGUMENT` for any nonzero one.

## Limits kept explicit

- The kernel runs at most four threads of a process (`WIT_USER_THREAD_CAPACITY`, a prototype quota bound to the
  frozen line's fixed TLS windows): a fourth `pthread_create` beside the main thread fails with `EAGAIN`, so `sem_init`
  (three workers at once, then a fourth), `tls_init` (five at once) and `pthread_cond-smasher` wait for the quota's
  rework. K5.3 gives the system layer's processes sixteen threads and brings them back
  ([K5.3-Process-Capacities.md](K5.3-Process-Capacities.md)). libc-test's own runner forks a process per test, this one does not yet (S6): a program that mistakes a
  failed creation for a thread and joins it faults or corrupts the process, and `pthread_mutex` leaves a thread
  deadlocked on a mutex of a returned stack frame by design, which the next test's frames then overwrite — both
  wait for the process per test.
- No cancellation or `pthread_kill` (signals, S3), no robust or priority-inheriting mutexes (`ENOSYS`), no
  `set_robust_list`; `clone` serves threads of this process alone, never a new address space. `__cxa_thread_atexit`
  arrived with the C++ runtime (S4, libc++abi's fallback over pthread keys).
- Eight futex slots: a ninth concurrent waiter (impossible with the kernel's four threads) gets `ENOMEM`, which
  musl's loops treat as a spurious wake-up. K5.3 makes them sixteen, as many as its threads of a process. The exit word's waiters share one event, so a wake of that word with
  several parked wakes one of them, who wakes the next (musl's protocol). The slot lock spins with `THREAD_YIELD`.
- Thread ids are the library's small integers, not kernel identities; the library keeps at most 64 live threads'
  records.
