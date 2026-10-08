# K6 — UTC (ABI v62)

Plan step K6 brings the UTC clock of [RFC 0011 v3 §7.10](../RFC-0011-Kernel-Architecture-and-ABI.md) into the kernel:
`CLOCK_READ` and `CLOCK_FREQUENCY` serve `WIT_CLOCK_UTC` beside the monotonic domain, `CLOCK_SET` (58) sets UTC behind
the clock capability (handle kind `CLOCK`, 16) that the root task receives as its fourth initial handle, and the
`UTC` bit joins the feature mask `QUERY` and the root startup report. The monotonic domain is unchanged. The
call-by-call reference is [ABI-Reference.md](ABI-Reference.md).

## What changed

**UTC is the board's real-time clock at boot plus the monotonic time since.** The platform reads its real-time clock
once (`wit_platform_realtime_seconds`), after its monotonic clock is ready: on q35 the MC146818 CMOS clock at ports
0x70/0x71 (update-in-progress waited out, two consecutive readings equal, BCD or binary and 12- or 24-hour form as
status register B says, the century from CMOS 0x32, the date converted with the proleptic Gregorian day count; an
implausible date reports no clock), on virt the PL031 at the fixed base of the QEMU virt profile (`WIT_VIRT_PL031_BASE`,
like the console's PL011), whose data register holds the seconds since 1970. The kernel (`clock.c`) keeps the UTC of
the moment it took its monotonic origin and adds the elapsed counter time, converted in whole seconds and remainder so
that no product overflows for any counter frequency up to 10^9. `CLOCK_READ(UTC)` is nanoseconds since 1970-01-01,
`CLOCK_FREQUENCY(UTC)` is 10^9; on a board without a real-time clock both are `UNSUPPORTED`.

**Setting UTC is a capability.** `CLOCK_SET(clock handle with WRITE, WIT_CLOCK_UTC, nanoseconds)` rebases UTC so that
later readings continue from the value; the monotonic clock cannot be set and UTC cannot move before the boot
(`INVALID_ARGUMENT`), a value beyond 2^63 is refused. The clock handle is the authority itself, with no record behind
it: `WRITE`, `DUPLICATE` and `TRANSFER`, attenuable through `HANDLE_DUPLICATE`, movable through a channel into another
process. The root task gets it as `Handles[WIT_ROOT_HANDLE_CLOCK]` (3) with `HandleCount` 4; the kernel grants it to
nobody else.

**Not in K6.** The delivered-tick clock the RFC removes here is the frozen Windows-form line's (`MONOTONIC_QUERY` 207
and the PIT deadlines); it leaves with that line at K8. There is no slewing, no leap-second table, no TAI and no
monotonic-raw clock; the real-time clock is read once at boot, and the root task's `CLOCK_SET` is the only correction.

## Kernel

- `user_abi.h`: v62, `WIT_CALL_CLOCK_SET` 58, the `UTC` feature; `handles.h`: `CLOCK` 16; `root.h`:
  `WIT_ROOT_HANDLE_CLOCK` 3; `platform.h`: `wit_platform_realtime_seconds`; `clock.h`/`clock.c`: the UTC domain.
- `kernel.c`: `wit_clock_initialize` after the platform clock. `user_calls.c`: the UTC branches and `clock_set`.
  `user_reference.c`: `duplicate_clock`; `user_channel.c`: the clock capability moves; `root_task.c`: the fourth
  handle.
- `Kernel.Platform.Q35/rtc.c`, `Kernel.Platform.QemuVirt/clock.c` (PL031), `virt.h`.

## Tests

`tests/Kernel/clock_tests.c` (`Clock.Utc`, both ISAs' foundation): the board reported a clock, two readings never go
backwards, the boot time lies in [2026, 2100), a value beyond the range and a value before the boot are refused, a
set to 2030-01-01 is continued from, and the clock is restored. The root fixtures (`tests/User.X64/root.asm`,
`tests/User.A64/root.asm`, run by `Root.Started` in the self-test and as the root task of every release boot) check
`HandleCount` 4, `CLOCK_FREQUENCY(UTC)` = 10^9, a plausible `CLOCK_READ(UTC)`, `CLOCK_SET` through the clock handle
followed by a reading from the value set, `WRONG_TYPE` through the log handle and `INVALID_ARGUMENT` for the
monotonic clock.

## Limits kept explicit

- The real-time clock is read once; drift against the host and the host's own clock steps are not followed.
- The virt PL031 sits at the QEMU virt profile's fixed address, as the console does; the device tree is not parsed for
  it. Another board adds its own `wit_platform_realtime_seconds`.
- A board without a real-time clock has no UTC (`UNSUPPORTED`), and `CLOCK_SET` then establishes one from the value
  given.
