# P3 — a .NET process freezes on ARM64 with four processors (in progress)

Plan step P3, opened 2026-10-21 while W4.1 closed. A .NET process under CoreCLR on the ARM64 board with four processors
stops whole, a few times in every ten runs of the scenarios of applications under load, where x64 and riscv64 have not
shown it. Nothing of the kernel panics and the guest keeps ticking; the runner ends the boot at its wall-clock limit.

## 1. The occurrences

| When | Where | What stopped |
| --- | --- | --- |
| 2026-10-21, CI's ARM64 leg of W3.1 (PR #178, first run) | `runtime-compatibility`, 4 processors | `Network.TlsEcho` reported no result within 60 s of the guest's time; `Network.HttpsGet`, which makes its certificate through OpenSSL before its first `await` on the main thread, never reported, not even its own timeout; the monitor's samples showed a thread in a loop of user code |
| 2026-10-21, the container's first ARM64 run of W4.1 | `runtime-compatibility`, 4 processors | the same two lines; the monitor's one sample found the boot processor idle in `wit_arch_idle_once` after its `wfi` |
| 2026-10-21, CI's ARM64 leg of PR #176 (code before W3.1) | `runtime-libraries`, 4 processors, no network | `XUnitApp` stopped after `Starting: XUnitApp`, where xUnit's runner starts its test threads; NUnit's application before it had passed |
| 2026-10-21, CI's x64 nuget-suites job of PR #176 | `runtime-nuget-suites`, group serialization-numerics, 4 processors | not a freeze: `MemoryPack.Tests.InterfaceFormatterTest.Dictionaries` failed once with `EntryPointNotFoundException` at an interface call (`IEquivalencyAssertionOptions.get_AllowInfiniteRecursion`), 121 of 122 passed, where every earlier run passed; counted here as a possible second symptom of the same cause |

Common to the freezes: ARM64, four processors, CoreCLR with many threads (the thread pool, the socket engine, xUnit's
workers), the process stopping as a whole — a main thread that awaits with a 60 s timeout never resumed, which means the
thread pool itself made no progress, and the `WaitAsync` continuation had nowhere to run. Reruns of the same images
passed; the guest runs in QEMU's virtual time, so the difference between a run that freezes and one that does not lies
in what the host feeds the guest — the terminal's typing, slirp's answers, the runner's servers, virtio completions —
and in when those land in the guest's instruction count.

## 2. The tools

A freeze leaves little in the serial log, so the step first gave the runner and the tool a way to look inside the guest.

- **Every processor's registers.** A guest past its deadline has the runner record `info registers -a` once before its
  samples (`QemuControl.QuitAsync`): the current processor alone showed an idle boot processor while the others' state
  was unknown.
- **The guest's memory.** With `WITOS_GUEST_DUMP=1` the runner writes the guest's physical memory as an ELF core beside
  the logs through QMP's `dump-guest-memory` (`<boot>.core`, without paging), and the stop callback's budget grows to
  two minutes for it (`BootScenarioRunner.GUEST_DUMP_VARIABLE`, `Processes.RunWithFilesAsync`'s `controlBudget`).
- **`guest-threads`** (`tools/WitOS.Dev/Diagnostics`): `guest-threads --core <file> --map <WitOS.map> --serial
  <serial.log> [--arch arm64] [--libc <libc.so>] [--symbols <directory>]...` reads the core (`GuestCore`: the ELF's
  loadable segments, reads by physical address, ARM64's stage-1 translation for the kernel's stack window and a process's
  own table), the kernel's link map (`KernelMap`: every symbol, static ones by their object) and the boot's serial log
  (the image's base from the printed kernel stack, TTBR1, the processors online), and reports the kernel lock's owner
  and waiters, each processor's dispatcher, and for every live process each thread's state, wait (its handles and
  deadline), activations, processor, stack and the user context its kernel frame holds (pc, sp, lr, spsr, x0–x8); for
  a dynamic program it lists the modules musl's dynamic linker keeps for debuggers (`_dl_debug_addr` through the
  interpreter at libwitos's place for it) and walks each thread's frame records (x29) as module, offset and function
  (`ElfSymbols`, from an ELF of the module's name below a `--symbols` directory). The layout of the kernel's records
  comes from the kernel's own headers through the pinned clang (`kernel_layout.c` compiled to LLVM IR for the kernel's
  triple), so no offset is written by hand. ARM64 alone so far.

## 3. Reproduction

- 2026-10-21/22, the container, `runtime-libraries` on ARM64 with `WITOS_GUEST_DUMP=1`, the wall-clock limit cut to
  15 minutes (a healthy boot takes 4–5): 12 runs, 12 passed.
- 2026-10-22, `runtime-compatibility` on ARM64, the limit cut to 12 minutes: 12 runs in progress, the first 6 passed.
- Next: the same with the host loaded beside QEMU (`LOAD=8` idle loops), since both CI occurrences were on shared
  runners and the container's one came right after a build, when I/O completions land at other instruction counts than
  on an idle host.

## 4. Open

The cause. Candidates by the symptom (every thread of the process waiting, the thread pool starved): a lost wakeup in
the libc's futex (`wait_slot`/`wake` under the one lock), in the kernel's wait and event publication across processors,
or in the socket session's readiness events; a CoreCLR suspension for a GC that never completes on four processors.
The core of a frozen run decides between them.
