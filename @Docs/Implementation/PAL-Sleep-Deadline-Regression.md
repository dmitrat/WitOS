# Debug report: expired sleep deadline in PAL thread validation

**Status:** Fixed in WitOS 0.0.34. ABI v17 and runtime behavior are unchanged.

## Reproduction

[Kernel CI for 0.0.33](https://github.com/dmitrat/WitOS/actions/runs/36316221260) reached the final timeout scenario, then failed at the combined PAL thread preemption/reuse/accounting assertion. The NativeAOT workflow passed. The failed log did not print individual counters.

The PAL fixture read delivered PIT ticks, computed an absolute deadline one tick later, and called ThreadSleep. The supervisor unconditionally required IdleHalts > 0. A timer interrupt can occur between the clock read and the syscall, so the requested deadline may already have expired.

A new mode deliberately waits until the deadline expires before making that same sleep call. With the old assertion intact, this reproduced the same panic. The diagnostic counters were: four thread creations, three joins, three reaps, three timer switches, zero idle halts, and 21 owned pages against 21 expected. The native application returned its successful exit code. Thus the test had a reproducible false assumption about idle; the original combined CI log alone cannot identify which counter failed there.

## Root cause

The sleep contract returns success immediately for an already-expired absolute deadline. It does not require the kernel to park or enter idle in that case. The test incorrectly treated every successful absolute sleep as evidence of an idle transition. Increased runtime instrumentation or different scheduling can expose that assumption without violating the sleep contract.

## Fix

The fixture now records the requested deadline, the last clock observation before the call and the first observation after the call. The supervisor checks that completion is not earlier than the deadline and retains separate strict assertions for creation/join/reap counts, timer preemption, page ownership and handle cleanup.

The forced-expiration mode additionally requires that the deadline was already reached before the call and that no idle halt occurred. It runs with plain compiler-TLS-free and relocated static-TLS images. Failures now print the relevant counters and use distinct diagnostic messages.

The general mode no longer assumes a nonzero idle count. Real idle coverage remains mandatory in the separate WaitClockAndIdle, WaitIdleBudget, deadline and join-chain scenarios; those assertions were not relaxed. No kernel sleep/scheduler implementation, timeout budget, source pin or runtime placeholder was changed.

## Validation and prevention

The old assertion failed under the forced-expiration reproducer; the corrected normal boot passed and emitted the new required User.PalExpiredSleep marker. The full regression and CI validate this case as part of every normal boot. Results are reported with delivery rather than inferred from a successful compilation.

Ordinary successful boots now require 175 user groups; runtime-config requires 190 including its fifteen dedicated groups. The suite still has nineteen VM scenarios and 51 contained hardware user faults. The native port remains at 93 unresolved symbols: this repair improves verification rather than adding a runtime feature.

When testing an absolute deadline, distinguish an actual parked wait from a deadline that elapsed before kernel entry. Assert successful completion semantics separately from tests that deliberately exercise idle and IRQ return state.
