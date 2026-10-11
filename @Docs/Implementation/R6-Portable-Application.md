# R6 — An ordinary application runs unchanged

R6 is the gate of RFC 0015 §1 and §9: an application built the way a developer builds it, framework-dependent and
without a RID, runs on WitOS unchanged. It is the old P6.5. Both ISAs pass, although the gate asks for x64 alone.

```
[R6] A portable application on WitOS 0.1 ABI-1 (witos-x64, .NET 10.0.7-dev): 12 of 12 checks passed
[R6] A portable application on WitOS 0.1 ABI-1 (witos-arm64, .NET 10.0.7-dev): 12 of 12 checks passed
```

## The application

`tests/Runtime.Witos/PortableApp` is the console template: `net10.0`, `ImplicitUsings` and `Nullable`, no RID,
nothing for WitOS. Its `Program.cs` runs twelve checks of what applications use every day:

- LINQ;
- generic collections;
- `async` code on the thread pool;
- exception filters with stack traces;
- reflection with `Activator`;
- compiled expression trees;
- a compiled regular expression;
- `System.Text.Json` with a record;
- `Parallel.For` with a `ConcurrentDictionary`;
- `BigInteger`;
- `Vector<T>`;
- text formatting.

It passes 12 of 12 on Windows too.

The `runtime-portable-app` scenario builds it the way a developer's machine builds it. The tool copies the project
apart from the repository's build properties, with empty `Directory.Build.*`, and publishes it with the stock SDK
that runs the tool: `dotnet publish -c Release`, no RID. Nothing of the output changes. `PortableApp.dll`, its
`deps.json` and its `runtimeconfig.json` go under `/app` beside R5's install, and `dotnet_init.c` runs
`dotnet /app/PortableApp.dll`. The scenario requires `12 of 12 checks passed` and the root task's report of status 0.
`runtime-witos` boots it on each ISA after `runtime-dotnet`.

## What it found

The first run passed five checks and then ended with "Out of memory." Six problems lay behind that, found in turn.
Container-only kernel instruments did the finding:

- a trace of every thread creation, with the process's handle, event and thread counts;
- after a long idle, a dump of every thread of every process with its state, its saved PC and the code addresses on
  its user stack;
- a libc trace of the address where each package file is mapped.

`llvm-nm` on the `.dbg` files turned the stack addresses into frames.

| Finding | Cause | Change |
| --- | --- | --- |
| Threads died, and the thread pool kept adding workers until thread creation failed ("Out of memory.") | musl's threads default to a 128 KiB stack. The checks that ran on a pool worker after the first `await` overflowed it: the expression and regex compilers. The pool's starvation detection then injected worker after worker, and each died the same way. | A patch of CoreCLR's PAL (`pal/src/init/pal.cpp`): `witos` takes the 1.5 MiB default that Apple and Windows threads get. Alpine's `ENSURE_PRIMARY_STACK_SIZE` is not used, because it also grows the primary thread's stack with `alloca`, and a started program's first stack on WitOS is a fixed mapping. |
| Thread creation failed with 64 of 64 handles in use | Each thread takes handles, and every 256 KiB shared memory chunk of the JIT's double mapping holds one (R3.2b) | A system layer process now gets 256 handles, 128 live memory objects, 32 threads and 64 events. The kernel's object table holds 256 objects, and the libc keeps 32 futex slots, each with its event beside the exit word's. |
| "Couldn't find a valid ICU package" | An application's runtimeconfig does not ask for invariant globalization | The root task's initial environment carries `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`, so every process the system layer starts runs .NET in invariant mode (RFC 0015 §8). |
| `FileNotFoundException` for framework assemblies | CoreCLR keeps every assembly it loads open, and the libc's descriptor table held 64 | The table holds 1024, Linux's usual soft limit. `getrlimit(RLIMIT_NOFILE)` reports it. |
| A process whose threads all waited was ended by its tick budget | The idle path's ticks were charged to whichever process ran last, which breaks the rule that a tick is never charged to a component that is not running | A tick that interrupts the idle path charges nobody while something can still wake the processor: a ready thread, a parked wait's deadline in any process, a bound line. When nothing can, every process waits for another that waits too, and the tick still charges the process that went idle, so that its budget bounds the deadlock, as `User.WaitIdleBudget` requires. |
| `[PANIC] Timer did not interrupt the kernel idle path` | When a created process ended from such a tick, `wit_user_finish` resumed another process's frame with the idle flag still set | The flag is cleared before the resume. |

`libc_hello.c` now creates 31 threads beside the main one and expects the 33rd to fail. `process_init.c` checks
`RLIMIT_NOFILE`. Both suites pass on both ISAs.

The patch set grows to 25 files.

## Limits kept explicit

- **A stack overflow on a thread is not reported.** On Linux, CoreCLR reports "Stack overflow" and aborts. On WitOS
  the thread ended, and the runtime went on without it. Why the report does not happen is still to be understood. The
  1.5 MiB stacks keep ordinary programs far from it.
- **Thread stacks are committed eagerly.** The kernel does not page on demand, so each thread's 1.5 MiB is committed
  when the thread starts. The primary thread's stack is the fixed 256 KiB mapping libwitos makes.
- **Quotas are still small** next to Linux's: 32 threads, 256 handles, 64 events and 128 objects per process. A
  program that outgrows them fails with an `OutOfMemoryException` or an error from the runtime.
- **Every method is JIT-compiled.** There is no ReadyToRun code for `witos`, and under emulation that is what makes the
  thread pool's heuristics see a slow machine.
