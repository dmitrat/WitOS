# S3 — minimal signals (ABI v66)

Plan step S3 gives the libc its signals ([RFC 0011 v3 §7.5, §9.3](../RFC-0011-Kernel-Architecture-and-ABI.md)): the
synchronous set (`SIGSEGV`, `SIGBUS`, `SIGFPE`, `SIGILL`, `SIGTRAP`) from the kernel's fault delivery with a
`ucontext_t` and a `siginfo_t`, `pthread_kill`, `raise` and `tkill` as activations of the target thread, `sigaltstack`,
masks and pending signals, `sigsuspend` and `pause`, `EINTR` in interrupted waits and sleeps, and the cancellation
point musl's `pthread_cancel` needs. The kernel still knows no signal: it gained one mechanism, a thread's alternate
stack, so that a fault can be delivered when the interrupted stack is exhausted.

## What changed

**The kernel: a thread's alternate stack (S3.1, ABI v66).** The kernel enters the fault callback on the interrupted
thread's own stack and accepts a thread's stack pointer only inside its stack at every return to user mode and in
every context it validates (`validate_return`, `wit_user_context_validate`), so no handler could run on a
`sigaltstack` buffer and no fault could be delivered to a thread whose stack had no room. `THREAD_STACK_ALTERNATE`
(42, `WitThreadAlternateStackRequest`: `Base`, `Bytes`) records for the calling thread one 16-byte aligned range of at
least `WIT_EXCEPTION_STACK_MINIMUM` bytes, committed and writable in the caller's space (a mapping of the image or a
reservation alike) and apart from the thread's stack, validated whole before the record changes; a zero request clears
it, and a caller whose stack pointer is inside the current range gets `BUSY`. The thread's stack pointer is then
accepted in either range (`wit_user_thread_stack_range`), and a delivery whose interrupted stack has no room for the
callback frame enters the callback at the top of the alternate stack when the thread is not on it already; without one
the delivery is refused and the component ends, as before. `WIT_EXCEPTION_STACK_MINIMUM` grew from 4 to 8 KiB: a
libc's signal frame with the `ucontext_t` of either ISA fits in it. On x64 the callback frame now leaves the 128-byte
red zone of the SysV convention below the interrupted stack pointer, where a layer-2 leaf function keeps data without
moving RSP; an activation may interrupt such a function anywhere. The thread's saved frame, its validated contexts and
its returns accept the alternate range alike. The exception fixtures of both ISAs (`tests/User.*/exceptions.asm`)
register an alternate stack after the refusals (a foreign version, a wrong size, an unaligned base, too few bytes, the
thread's own stack, an address outside user space, a reserved but uncommitted range), take a second read fault with
the stack pointer sixteen bytes above the stack's bottom and see the callback run on the alternate stack with the
interrupted stack pointer in the record, find the change refused `BUSY` while on it and clear it. The frozen line's
`worker_lifecycle.cpp` is untouched; no number, status or right of the frozen line changed.

**Signals in the libc (`src/Substrate/libc/signal.c`, S3.2).** Startup (`crt1`) registers the one fault callback,
`__wit_signal_entry` (an assembly entry: the kernel's x64 convention passes the token, vector and address in RCX, RDX
and R8; ARM64 in X0–X2), which reads the record with `EXCEPTION_QUERY` and classifies it. A fault maps by its vector —
the x64 IDT vector or the ARM64 exception class and syndrome — to the signal and `si_code` Linux gives it, for the
faults the kernel delivers to the callback: on x64 `#PF` to `SIGSEGV` with `SEGV_MAPERR` or `SEGV_ACCERR` and the
fault address, `#GP` to `SIGSEGV` with `SI_KERNEL`, `#UD` to `SIGILL`, `#DE` to `SIGFPE` with `FPE_INTDIV`, `#BP` to
`SIGTRAP`; on ARM64 instruction and data aborts to `SIGSEGV` (translation faults `SEGV_MAPERR`, access-flag and
permission faults `SEGV_ACCERR`) or, for an alignment fault, `SIGBUS`, PC and SP alignment to `SIGBUS`, the unknown
and system-register classes to `SIGILL`, `BRK` to `SIGTRAP`. Any other fault still ends the process as the kernel's
fault report. An activation whose callback is the library's marker carries the signal number as its argument
(`SI_TKILL`). The disposition table (`rt_sigaction`: handler, flags, mask), each thread's mask and pending set and its
`sigaltstack` record are the library's, kept in the thread records of `thread.c`.

A blocked activation is recorded pending and the interrupted context continues unchanged; a blocked synchronous
fault, a `SIG_DFL` or `SIG_IGN` fault end the process through `EXCEPTION_REJECT`, which reports the fault as the
kernel always did; a `SIG_DFL` activation ends the process with 128 + the signal (`PROCESS_EXIT`) or is ignored
where POSIX ignores it by default. To run a handler the callback builds a frame — the saved kernel context, a
`ucontext_t` in musl's layout for the ISA (x64 `gregs`, the FXSAVE image as `fpregs` in `__fpregs_mem`; ARM64 `regs`,
`sp`, `pc`, `pstate` and an `fpsimd_context` record), a `siginfo_t` — on the thread's stack below the interrupted
frame or at the top of the alternate stack when the action has `SA_ONSTACK` and the thread is not on it, sets the
thread's mask to the action's with the signal itself unless `SA_NODEFER`, resets a `SA_RESETHAND` action, and
continues the interrupted context changed to enter `__wit_signal_trampoline` with the frame. That continue retires
the delivery before the handler runs, so a handler may fault, raise, be interrupted or `siglongjmp` out; the
trampoline calls the handler with the signal, the `siginfo_t` and the `ucontext_t` and then `__wit_sigreturn`, which
carries the handler's changes to the general registers, the flags and the floating-point image back into the saved
context within what the kernel lets a user context carry, takes the mask the handler left in `uc_sigmask`, re-arms
every pending signal the mask admits as an activation of the own thread and resumes the saved context with
`THREAD_CONTEXT_RESTORE`. The x64 handler starts with a default x87 and SSE environment, as on Linux. musl's kernel
restorers `__restore` and `__restore_rt` exist as symbols and are never entered.

The library's locks are held with the thread's signals held (`__wit_lock`, `__wit_signal_hold_enter`): an activation
that interrupts the holder — a tick may preempt it anywhere — waits pending and is re-armed at the release, since a
handler's `sem_post`, which POSIX allows in a handler, takes the futex slots' lock and would spin on its own thread.
The same pass gave the tables S1 and S2 left unguarded a lock: every Linux call that reads or changes the mappings
(`memory.c`), the descriptors or the package (`files.c`) runs under one lock in `__wit_syscall`, while the calls that
wait, sleep, exit or signal never take it. musl maps a large static TLS block through `mmap` before it installs the
main thread's pointer, so the hold counts only once a constructor of `signal.c` set `__wit_tls_ready` (with a volatile
store: an optimizer that evaluates constructors at compile time folded a plain one into the flag's initial value), and
as a precaution every thread-local access of the library sits behind a call into another file, so that no load of
thread-local state can be moved above that check.

`rt_sigprocmask` changes the calling thread's mask and re-arms what became deliverable (an activation of the own
thread is delivered at that call's return); `rt_sigpending` reports the blocked pending set; `sigaltstack` keeps the
library's record and gives the kernel the same range through `THREAD_STACK_ALTERNATE` (`SS_ONSTACK` is reported
while the stack pointer is inside it, a change from on it is `EPERM`, a range below `WIT_EXCEPTION_STACK_MINIMUM` is
`ENOMEM`); `rt_sigsuspend` and `pause` sleep (`SLEEP_UNTIL` without a deadline) until an activation ran a handler,
which ends the sleep `INTERRUPTED`, and return `EINTR`; `nanosleep` and the futex waits return `EINTR` with the time
left when a handler ran. `tkill` and `tgkill` reach a thread of the own process by its id; `kill` reaches the calling
thread of the own process and no other (`ESRCH`); stop, continue and kill signals are refused with `EINVAL`.

**Thread handles and the cancellation point (`thread.c`, S3.3).** The library now keeps every thread's kernel
handle while the thread lives (the main thread's from `HANDLE_DUPLICATE` of `WIT_THREAD_SELF` at startup) and closes
it in the thread's own exit, so that `pthread_kill` is `THREAD_ACTIVATE` of the target's handle (`WIT_THREAD_SELF`
for the own thread) with the marker callback and the signal; the kernel's four pending activations per thread bound
what can be queued (`EAGAIN` beyond). musl's cancellation point `__syscall_cp_asm` is C here: the cancel word is
checked first, as musl's assembly does, and the call runs with the word published (`__wit_cancel_requested`), so that
a blocking path about to park — a futex wait, a sleep — checks it again and returns `EINTR`, on which musl's
`__syscall_cp_c` cancels; Linux closes that window with its atomic system call instruction, WitOS with the check
before the kernel wait. The window symbols `__cp_begin` and `__cp_end` are empty and `__cp_cancel` is musl's
`__cancel`: a cancellation that arrives in a parked wait ends it `INTERRUPTED`.

## Tests

- `tests/User/libc_hello.c` adds the signal section: `sigaltstack` and a handler that runs on it with `SS_ONSTACK`
  reported, a blocked signal kept pending, shown by `sigpending` and delivered on unblocking, a handler raising
  another signal whose handler nests, `SIG_IGN`, a store into a `PROT_NONE` page repaired by a `SIGSEGV` handler
  (`si_addr`, `si_code`) and completed on return, `siglongjmp` out of a `SIGILL` handler (and a `SIGFPE` handler on
  x64, whose integer division traps), `pthread_kill` of a worker parked in `sem_wait` seen as `EINTR`, `sigsuspend`
  woken by another thread's `pthread_kill`, `kill` of the own process only.
- libc-test gains `functional/setjmp` (the mask `sigsetjmp` saves), `regression/sigaltstack`,
  `regression/sigprocmask-internal` and `regression/sigreturn`.
- The kernel self-test's exception fixtures cover the alternate stack on both ISAs (`User.ExceptionCallbackAndContext`
  now counts two delivered read faults and three continuations).

## Limits kept explicit

- A thread's stack overflow is caught only when the thread registered a `sigaltstack` of at least
  `WIT_EXCEPTION_STACK_MINIMUM` (8 KiB); otherwise the process ends with the fault, as before. `MINSIGSTKSZ` is
  smaller than that minimum: `sigaltstack` refuses it with `ENOMEM`.
- Signals are per thread: `kill` of the own process reaches the calling thread, never another process; no job
  control (`SIGSTOP`, `SIGTSTP`, `SIGTTIN`, `SIGTTOU` and `SIGKILL` are refused with `EINVAL`), no `sigqueue` values,
  no `sigtimedwait` or `sigwaitinfo` (`ENOSYS`), no `SA_RESTART` restart (an interrupted wait returns `EINTR` to
  musl's loops, which retry where they must), no `SA_NOCLDSTOP` or `SA_NOCLDWAIT` (there are no children).
- At most four activations wait for one thread in the kernel: a fifth `pthread_kill` before the thread runs is
  `EAGAIN`; `raise-race` (a thousand signals and `fork`) stays out of the selection.
- A blocked synchronous fault, an ignored fault and a default-disposition fault end the process (the kernel reports
  the fault); an ignored fault on Linux would re-execute forever. Faults the kernel does not deliver to the callback
  (x64 `#DB`, `#NM`, `#SS`, `#MF`, `#AC`, `#XM`; the ARM64 floating-point trap) end the process too, so unmasked
  floating-point exceptions are not `SIGFPE` yet, and ARM64 integer division never traps.
- The ucontext a handler may change is applied within the kernel's user profile: the x64 flags keep the status bits
  and the ARM64 `pstate` the condition flags; the segment selectors, TLS bases and the ARM64 `x18` stay the kernel's.
