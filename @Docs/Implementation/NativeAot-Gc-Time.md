# ADR 0010: HPET monotonic time and GC deadlines

**Status:** Implemented in WitOS 0.0.13; verified locally on 2026-09-17.
**Scope:** Controlled QEMU q35/x64, user ABI v7, GC time/sleep hooks and finite GCEvent waits. No managed runtime or collector executes in WitOS yet.

## Decision and platform boundary

Use q35's HPET main counter as the monotonic clock, while retaining PIT IRQ0 for scheduling. Delivered interrupts cannot measure elapsed time while IRQs are disabled. A TSC path would require a separate frequency/stability contract; generic ACPI timer discovery would expand the boot contract beyond this controlled board milestone.

The hardware-specific driver and mapping stay in `Kernel.Arch.X64`. The runner explicitly selects `q35,hpet=on`. The fixed `0xFED00000` aperture is a board-profile choice, not generic hardware discovery. Other machines need an explicit platform/discovery implementation; the kernel fails closed if the expected HPET is unavailable. UEFI details and boot handoff v2 are unchanged.

The [Intel HPET specification](https://www.intel.com/content/dam/www/public/us/en/documents/technical-specifications/software-developers-hpet-spec-1-0a.pdf) defines the counter width and femtosecond period fields. The pinned [QEMU implementation](https://github.com/qemu/qemu/blob/v11.1.0/hw/timer/hpet.c) and [register definitions](https://github.com/qemu/qemu/blob/v11.1.0/include/hw/timer/hpet.h) supply this board's 10 ns counter period: 100,000,000 counts/second, advancing on QEMU virtual time.

## Hardware ownership and reads

Only the kernel maps the device page: supervisor RW/NX, PCD/PWT selecting PAT entry 3. The code requires PAT support and checks that this entry is UC; it does not change inherited PAT/MTRR settings. Usable-RAM/kernel-image/guard overlap or an existing mapping rejects setup. The scratch allocator cannot map an alias to this non-allocator device frame.

Initialization validates a 64-bit main counter, revision/vendor, supported timer count and a period between 1 ns and 100 ns that yields an integer frequency. HPET comparator interrupts, FSB delivery and legacy PIT replacement are disabled. Only the free-running main counter is used.

Reads use bounded high/low/high 32-bit sampling, so crossing the low-word boundary cannot return a torn value. During bootstrap, before publishing the clock, a real counter seeded near that boundary must cross it. The counter is then reset to the final boot epoch and must advance at least 2 ms with IF clear and no increase in delivered PIT ticks. It is never reset after publication.

The single-CPU kernel reads with IF clear; it checks nondecreasing values and saturates at signed-64 maximum. Unexpected counter reversal is fatal. This serial-read assumption must be redesigned for SMP. The frequency is derived from the device's advertised period, not independently calibrated to UTC. VM suspension/migration semantics and generic physical hardware are outside this target's validation.

## User ABI v7

The startup prefix remains 24 bytes. Legacy calls 13-19 keep their delivered-PIT semantics. Four new calls use a separate monotonic domain:

| Call | Input | Result |
| --- | --- | --- |
| 21 Monotonic read | None | Nonnegative signed-64-compatible count since the boot epoch |
| 22 Monotonic frequency | None | Counts per second |
| 23 Sleep until | Absolute monotonic deadline; other arguments zero | Zero on completion |
| 24 Event wait until | Event handle, absolute monotonic deadline, zero flags | Zero on signal; TIMED_OUT or CLOSED on those outcomes |

Deadlines from zero through `0x7FFFFFFFFFFFFFFF` are valid; all-ones means infinite. Other values return INVALID_ARGUMENT. Infinite sleep is supported by the new family; legacy tick sleep retains its previous rejection of infinity. A stored event signal wins for an immediate/past-deadline wait. Invalid deadline/flags do not consume a signal.

Parked threads carry an explicit clock-domain tag. Both domains are expired under the existing serialized syscall, dispatch and timer paths, before subsequent event signal/close operations. The sampled counter in that critical section defines ordering. Tick values never expire monotonic waits or vice versa. FIFO selection and handle-generation rules span both families.

PIT still wakes the CPU from idle. A deadline will not expire before the sampled counter reaches it, but notification can be late until a scheduler/syscall opportunity. There is no sub-tick wake-latency or hard real-time guarantee. If the deadline passes during call setup, completion need not park. The existing ten-delivered-tick activation budget remains a test/prototype limit.

## NativeAOT adapter

`gc_time.witos.cpp` implements QueryPerformanceCounter, QueryPerformanceFrequency, GetLowPrecisionTimeStamp and Sleep through the new ABI. Timestamp conversion uses quotient/remainder arithmetic to avoid multiplication overflow. Millisecond durations round upward to counter ticks; deadline addition saturates at the signed-64 horizon without becoming the infinite sentinel. Sleep(0) yields; Sleep(INFINITE) parks indefinitely subject to component lifetime/budget.

GCEvent computes its deadline before acquiring the private pool gate, so setup/contention counts toward the timeout. It captures the generation-bearing handle, releases the gate, and uses EventWaitUntil. Poll, finite and infinite waits now share the same kernel event state and cancellation rules. The alertable argument retains the upstream non-alertable behavior.

These are GC interface hooks, not a complete port of all CoreLib/Windows PAL clock APIs. The strict negative link now requires the still-unimplemented VirtualReset symbol. At this milestone native locks remained incomplete; the subsequent [mutex extension](NativeAot-Mutexes.md) implements minipal/Crst. TLS/attachment, fault/unwind integration and GC rendezvous remain incomplete.

## Validation

Release build, runtime-port, all 18 VM scenarios, runtime-audit, runtime-probe and runtime-source passed locally; runtime-source also refreshes runtime-target. The new negative scenario boots the same image with `hpet=off` and requires the specific unsupported-HPET panic plus expected exit status.

At version 0.0.13, successful boots required 106 user check groups and 34 contained user faults, plus kernel counter-rollover/IRQ-independence markers and the pinned 100 MHz frequency. New checks cover:

- Coherent 64-bit hardware counting and progress with interrupts disabled.
- Deterministic mixed-clock FIFO, exact deadline, late signal/close, past/invalid deadline and infinite-sleep state transitions.
- Guest performance counter/frequency, millisecond timestamps and sleep lower bounds.
- Manual/auto finite event timeouts, finite signal wakeup and stale/closed-event failure.
- Rounding and saturation, including the largest finite uint32 millisecond duration.
- A real user-mode read of HPET registers failing with page fault 14/error 5, followed by successful recovery.

Timing tests assert lower bounds and semantic results, not tight host-dependent latency ceilings. The local guest fixture is 14,848 bytes with 39 plain unwind records and no Windows/CRT imports. The full workstation archive contains 69 members; each of the three adapter objects is verified byte-for-byte. Strict linking reports 146 unresolved symbols, including eight GC environment requirements. Counts remain diagnostics, not compatibility percentages.

## Next work

Native minipal/Crst locks are now implemented. Connect runtime TLS/attachment, complete memory/reset and fault/GC coordination semantics, and expand measured runtime quotas. General device discovery and other clock backends are later platform work. A successful native time probe does not meet the managed-runtime M3 gate.
