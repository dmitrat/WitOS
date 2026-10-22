# P3 — a .NET process freezes on ARM64 with four processors

Plan step P3, opened 2026-10-21 while W4.1 closed and closed 2026-10-22. A .NET process under CoreCLR on the ARM64 board with four processors
stopped whole, a few times in every ten runs of the scenarios of applications under load, where x64 and riscv64 had not
shown it. Nothing of the kernel panicked and the guest kept ticking; the runner ended the boot at its wall-clock limit.
The cause (§5) was not in the kernel and not in CoreCLR: the network service's TCP stack answered its own
acknowledgments without end over the loopback, and every socket of every process waited for it.

## 1. The occurrences

| When | Where | What stopped |
| --- | --- | --- |
| 2026-10-21, CI's ARM64 leg of W3.1 (PR #178, first run) | `runtime-compatibility`, 4 processors | `Network.TlsEcho` reported no result within 60 s of the guest's time; `Network.HttpsGet` never reported, not even its own timeout; the monitor's samples showed a thread in a loop of user code |
| 2026-10-21, the container's first ARM64 run of W4.1 | `runtime-compatibility`, 4 processors | the same two lines; the monitor's one sample found the boot processor idle in `wit_arch_idle_once` after its `wfi` |
| 2026-10-21, CI's ARM64 leg of PR #176 (code before W3.1) | `runtime-libraries`, 4 processors, no network | `XUnitApp` stopped after `Starting: XUnitApp`, where xUnit's runner starts its test threads; NUnit's application before it had passed (§8: not explained by the cause below) |
| 2026-10-21, CI's x64 nuget-suites job of PR #176 | `runtime-nuget-suites`, group serialization-numerics, 4 processors | not a freeze: `MemoryPack.Tests.InterfaceFormatterTest.Dictionaries` failed once with `EntryPointNotFoundException` at an interface call, 121 of 122 passed (§8) |
| 2026-10-22, the container, run 11 of the reproduction loop (§3) | `runtime-compatibility`, 4 processors | `Network.TlsEcho` failed with no result within 60 s, nothing after; the core of §4 |
| 2026-10-22, the container, run 6 of the loop with the host loaded (§3) | `runtime-compatibility`, 4 processors | the same; the second core of §4 |

## 2. The tools

A freeze leaves little in the serial log, so the step first gave the runner and the tool a way to look inside the guest.

- **Every processor's registers.** A guest past its deadline has the runner record `info registers -a` once before its
  samples (`QemuControl.QuitAsync`). Each sample is of its own moment, since the guest runs between them: the first
  reading of the core below took the samples beside the memory and saw an idle processor that the dispatcher said ran a
  thread, which the dump's own notes then contradicted. The notes are the view to trust.
- **The guest's memory.** With `WITOS_GUEST_DUMP=1` the runner writes the guest's physical memory as an ELF core beside
  the logs through QMP's `dump-guest-memory` (`<boot>.core`, without paging), and the stop callback's budget grows to
  two minutes for it (`BootScenarioRunner.GUEST_DUMP_VARIABLE`, `Processes.RunWithFilesAsync`'s `controlBudget`). QEMU
  stops the guest for the dump and writes one `NT_PRSTATUS` note per processor, so the core is one consistent instant:
  the processors' registers and the memory they were working on.
- **`guest-threads`** (`tools/WitOS.Dev/Diagnostics`): `guest-threads --core <file> --map <WitOS.map> --serial
  <serial.log> [--arch arm64] [--libc <libc.so>] [--symbols <directory>]... [--peek <slot>:<address>:<words>]...
  [--scan <words>]` reads the core (`GuestCore`: the ELF's loadable segments, reads by physical address, the notes,
  ARM64's stage-1 translation for the kernel's stack window and a process's own table), the kernel's link map
  (`KernelMap`: every symbol, static ones by their object, and the symbol an address lies in) and the boot's serial log
  (the image's base from the printed kernel stack, TTBR1, the processors online), and reports every processor's
  registers at the dump's instant (a kernel address named by the map), the kernel lock's owner and waiters, each
  processor's dispatcher, and for every live process each thread's state, wait (its handles and deadline), activations,
  processor, stack and the user context its kernel frame holds (pc, sp, lr, spsr, x0–x8); for a dynamic program it lists
  the modules musl's dynamic linker keeps for debuggers (`_dl_debug_addr` through the interpreter at libwitos's place for
  it) and walks each thread's frame records (x29) as module, offset and function (`ElfSymbols`, from an ELF of the
  module's name below a `--symbols` directory: a directory of links named as the modules are — `libcoreclr.so` to the
  runtime build's `libcoreclr.so.dbg`, `ld-musl-aarch64.so.1` to the sysroot's `libc.so`, `Network` to the service's
  `Network.dbg`, OpenSSL's libraries from its build — serves them all). `--peek` prints words of a process's memory
  through its table (a NativeAOT static's address is its symbol's in the `.dbg` file plus the module's base), and
  `--scan` lists the code addresses among the words of each thread's stack above its stack pointer, for a thread whose
  code keeps no frame records (musl's and the libc's own threads). The layout of the kernel's records comes from the
  kernel's own headers through the pinned clang (`kernel_layout.c` compiled to LLVM IR for the kernel's triple), so no
  offset is written by hand. ARM64 alone so far.
- **The kernel's code.** `llvm-objdump -d --start-address=0x14001a12c BOOTAA64.EFI` disassembles the ARM64 image at a
  map address (the map's addresses are at the preferred base 0x140000000), which placed a processor's return address
  inside `wit_cpus_lock`'s loop.

## 3. Reproduction

Loops in the container of the scenario under `WITOS_GUEST_DUMP=1`, each run's wall-clock limit cut to 12 or 15 minutes
where a healthy ARM64 boot takes 4–5, every failed run keeping its logs, the kernel's map and the guest's core:

- 2026-10-21/22, `runtime-libraries` on ARM64: 12 runs, 12 passed.
- 2026-10-22, `runtime-compatibility` on ARM64: 12 runs, 1 froze (run 11).
- 2026-10-22, `runtime-compatibility` on ARM64 with 8 processors of the host kept busy beside QEMU (`LOAD=8`), since
  both CI occurrences were on shared runners: 12 runs, 1 froze (run 6).

So the freeze comes in about one run of twelve, on an idle host as on a loaded one, and `runtime-libraries` did not
reproduce its one occurrence.

## 4. What the cores showed

Both cores told the same story, in different frames of the same loop.

**The kernel was healthy.** Run 11: processor 0 in `wit_cpus_lock`'s loop on the kernel stack of the network service's
thread 3 (a `CLOCK_READ` of its); processor 1 in user mode in the dotnet process's thread 2, which had just returned
from an `OBJECT_WAIT` with `TIMED_OUT`; processors 2 and 3 halted in `wfi` on their idle stacks with `Idle` set. The
lock's words (`owner 0, waiters 0x1, yields_to 0x1`) were a transient of the handoff: processor 1 had released the lock
to the waiting processor 0, which was about to take it. Run 6: processor 0 at the exception vector of the same thread's
`CLOCK_READ`, the three others idle. No pending request on any processor, no thread suspended, every wait with a plausible deadline.

**The dotnet process waited for a socket.** Its main thread was in `Monitor.Wait` under `RunMain` (the suite awaiting
a check); its thread 9 was inside the libc's socket `call`, waiting on the channel of the process's session with the
network service for a reply (`wait objects [0xb000d0074]`); the finalizer, the thread pool's threads and the libc's
reaper, terminal and network event threads waited as they should; two threads polled with `OBJECT_WAIT` timeouts
(`--scan` placed them in `poll_descriptors` over a pipe, the libc's 10 ms look while a shared pipe or a local socket
lives). Nothing of CoreCLR was wrong.

**The network service was the one busy thing.** Its session thread (thread 4, `SocketConnection.SendStream` in run 11,
`ReceiveStream` in run 6) waited in `Monitor.Enter` for the stack's lock in `StackHost.Call`. Its stack thread (thread 3,
`StackHost.Run`) held that lock inside `NetworkStack.Advance → ReceiveIpv4 → ReceiveTcp`: in run 11 one frame deeper in
`SendAck → Transmit → SendIpv4 → BuildIpv4`, allocating the packet, inside the GC the allocation triggered (`gc1` at its
end, `update_end_ngc_time`); in run 6 in `Segment → StackHost.Now → witos_clock_read`. The monitor's samples of run 11
had caught the thread in `CLOCK_READ` four times in a row — the GC's timestamps and `Now` of every segment. `--peek` of
the service's GC statics (`WKS::gc_heap::settings.gc_index` through `Network.dbg`'s symbols) counted 12151 and 10911
collections of a heap of 6.5 MB: the thread allocated a packet at a time, without end. The loopback feeds a transmitted
packet back into the stack's own queue, and `Advance` drained that queue until it was empty — which it never was again.

## 5. The cause: an acknowledgment storm over the loopback

The suite's `Network.TlsEcho` writes a megabyte through `SslStream` over a loopback connection while the server reads
16 KiB at a time and writes each piece back. Both directions carry data, and on four processors the two sides fill each
other's receive buffers (256 KiB) at some moment: both windows close. Each side then probed the other's closed window as
the stack probed (RFC 9293 section 3.8.6.1's "at least one octet"): one byte of data at SND.NXT, which the peer with the
closed window dropped and acknowledged, and which advanced the prober's SND.NXT past the peer's RCV.NXT. From then on
every pure acknowledgment either side sent carried a sequence number one past what the other would take: with a zero
window a segment is acceptable only at RCV.NXT exactly (section 3.10.7.4), and an unacceptable segment "should" be
answered with an acknowledgment — which was unacceptable to the other side in turn. Two connections answering each
other's acknowledgments without end, at the loopback's speed of none, inside one `Advance` that took every answer out of
the queue it had just put it into. The session threads could not take the stack's lock, so no socket call of any process
was answered: the TLS echo's 60 s timeout was the first visible symptom, and `HttpsGet` blocked in a socket call before
its first `await`.

Linux has two defences, and the documentation of the second names this very case: `tcp_invalid_ratelimit` "limits the
maximal rate for sending duplicate acknowledgments in response to incoming TCP packets that are for an existing
connection but that are invalid due to any of these reasons: (a) out-of-window sequence number, (b) out-of-window
acknowledgment number, or (c) PAWS check failure. This can help mitigate simple 'ack loop' DoS attacks, wherein a buggy
or malicious middlebox or man-in-the-middle can rewrite TCP header fields in manner that causes each endpoint to think
that the other is sending invalid TCP segments, thus causing each side to send an unterminating stream of duplicate
acknowledgments for invalid segments. Default: 500 (milliseconds)." The first is the form of its window probe
(`tcp_xmit_probe_skb`): an acknowledgment with a sequence number from before the window, which takes no sequence space,
so that a probing peer's acknowledgments stay acceptable to a closed window and its acknowledgment of the other
direction's data is not lost while the window is closed.

Why ARM64 and four processors: the storm needs both windows closed with data in flight both ways at once, which the
echo's interleaving on four processors reaches in about one run of twelve; the other boards and the one-processor
runs never did in the step's loops, and nothing suggests they cannot.

## 6. The fix

`OutWit.OS.Networking` (commit `P3: a TCP acknowledgment storm on the loopback froze the network service`):

- `SendInvalidAck` answers an unacceptable segment, RFC 5961's challenge of a reset or a SYN, and an acknowledgment of
  what was never sent at most once in 500 ms of a connection (`TcpSocket.InvalidAckAt`, `INVALID_ACK_INTERVAL_MILLISECONDS`);
  `NetworkStatistics.AcknowledgmentsSkipped` counts the ones held back.
- `SendWindowProbe` sends Linux's probe — an acknowledgment at SND.UNA − 1 — when the persist timer expires, backing off
  by `Rto << ProbeBackoff` until the window opens (`ProbeBackoff` cleared by a window update) and failing the
  connection with `ETIMEDOUT` after the data retries (`Retries`, which the peer's answer clears, as before).
  `NetworkStatistics.WindowProbes` counts them.
- `Advance` takes the loopback's queue as it was at entry: what a received packet's answer queues waits for the next
  `Advance`, so that two connections cannot keep one going. `NextDeadline` was already now while the queue holds
  datagrams, so the service's stack thread looks again at once.

The host test `TcpTests.ClosedWindowsBothWaysOverTheLoopbackDoNotStormTest` connects two sockets of one stack over the
loopback, fills both directions until both windows close, and lets ten seconds pass with nobody reading: 22 segments go
(12 probes, 2 answers held back), both sides' SND.NXT equal their SND.UNA, and every byte arrives once both read. Before
the fix the test's first `Advance` after the windows closed never returned. `SimulatedNetwork.Pump` now keeps advancing
while a stack's loopback holds datagrams (`NextDeadline <= Now`), since the handshake over the loopback takes three.

## 7. Verification

| Where | Result |
| --- | --- |
| `OutWit.OS.Tests`, Networking | 26 passed, the new test among them |
| `runtime-compatibility` on ARM64, 12 runs with `LOAD=8` on the rebuilt services | 11 passed; run 6 failed otherwise — not a freeze: the Ipc area's child `dotnet` could not create CoreCLR (§8) — where the loops before the fix froze in one run of twelve |
| `runtime-devices` (through `runtime-services`) and `runtime-compatibility` on ARM64 | passed on the rebuilt services |
| `runtime-devices` and `runtime-compatibility` on x64 | passed ( through , then ) |
| `runtime-devices` and `runtime-compatibility` on riscv64 | passed (the same) |

## 8. Open

- The `XUnitApp` occurrence in `runtime-libraries` (CI of PR #176) is not explained by the storm: that scenario has no
  adapter and the application uses no socket. Its loop of twelve runs passed. It stays on the list of flakes to watch
  (`ci-flakes`), with its own core the next time it comes.
- `MemoryPack`'s one `EntryPointNotFoundException` on x64 is unrelated to the storm and unexplained.
- Run 6 of the verification loop: the Ipc area's child (`dotnet … --sleep` with `DOTNET_DiagnosticPorts`) printed
  `Failed to create CoreCLR, HRESULT: 0x8007000C` — `HRESULT_FROM_WIN32(ERROR_INVALID_ACCESS)`, the PAL's code of a
  refused `VirtualProtect`, as S8.2's `The access code is invalid` was — and the parent's connection broke; 273 MiB were
  free afterwards, as in the passing runs, so not memory. Once in some thirty runs. The libc now logs every `mprotect` it
  refuses (`[LIBC] mprotect refused …`), so the next occurrence names the call; until then it is a flake to watch.
- Linux also limits challenge acknowledgments globally (`tcp_challenge_ack_limit`); the stack limits them per connection
  alone.
