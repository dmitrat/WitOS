# Minipal monotonic time and runtime random TLS

**Status:** WitOS 0.0.38; ABI v17 unchanged.

Four real minipal entrypoints now use WitOS kernel services: minipal_hires_ticks, minipal_hires_tick_frequency, minipal_lowres_ticks and minipal_microdelay. Their declarations come from the pinned upstream time.h. The Windows source reference retains time.c; only the WitOS aotminipal archive selects minipal_time.witos.cpp.

## Contract

High-resolution ticks and their actual frequency come from the existing HPET monotonic-domain syscalls. Reads validate success, the signed tick range and the supported 1 kHz to 1 GHz frequency range. Milliseconds are calculated by dividing before multiplication, avoiding overflow. This is not UTC and does not use delivered PIT ticks.

These calls need no compiler TLS, environment table, PalInit or GC initialization. They allocate no memory and acquire no shared gate. Native last-error and errno remain unchanged. Unexpected kernel clock/sleep failure terminates the component instead of returning invented time or successful delay.

Microdelays use upward-rounded absolute deadlines, saturating at the kernel monotonic maximum. UINT32_MAX is a finite microsecond interval, not an infinite-wait sentinel. Zero is a no-op and preserves the caller's busy-wait counter.

As in upstream's Windows strategy, requests up to 1,000 microseconds busy-wait against the real counter; larger requests use kernel sleep-until. The busy counter adds the requested short interval with saturation rather than wrapping. The sleeping branch clears the counter after successful return. It may complete immediately if the absolute deadline expired before kernel entry; clearing this accounting does not prove that another thread ran or the CPU idled. Tests assert elapsed lower bounds separately from the kernel's existing idle-path tests. Scheduling and PIT wake granularity may extend delays; no upper latency guarantee is claimed.

The optional counter is ordinary caller-owned memory whose lifetime must cover the call. No identity or capability is inferred from it.

## Source and executable profile

The complete source-built aotminipal archive is copied into the configuration probe's artifact directory and hash-checked before linking. The exact time adapter and unchanged upstream xoshiro object are verified against archive bytes. The guest links these members from that archive; no separate test implementation supplies the minipal functions.

The new optimized time object originally emitted five chained unwind records (flag byte 0x21), and the kernel correctly rejected the fixture. The WitOS time source now explicitly compiles with /Od so it fits the current plain-unwind bootstrap profile. This option is checked in the real compile command. Windows-reference compilation remains unchanged. Kernel parsing still rejects handler/chained forms; this is not stack-unwinder support or a performance-tuned delay implementation.

Native transport declarations must retain C linkage. Both the broad source-boundary check and minimal-startup check now reject C++-decorated wit_native declarations instead of misclassifying them as missing platform services.

The audit now pins 64 canonical-byte source/license files. New pins cover time.h, xoshiro128pp.h and xoshiro128pp.c; time.c and the actual runtime thread.cpp were already pinned. Runtime/VMR versions and existing pin bytes remain unchanged.

## Actual runtime TLS constructor

The configuration source slice now includes the unchanged ee_alloc_context::PerThreadRandom constructor and t_random definition from thread.cpp. The real compiler emits its TLS initializer, the existing WitOS TLS lifecycle executes it, and it calls minipal_hires_ticks and the source-built minipal_xoshiro128pp_init implementation.

The guest checks nonzero initialized state, simultaneous worker-local addresses, isolation while yielding, retention of the main state and fresh initialization after worker reuse. Workers zero their private state before exit so a reused slot cannot pass with stale state. This is the runtime's native non-cryptographic allocation-sampling state; it does not initialize a collector, attach ThreadStore or provide security randomness.

## Guest and source validation

Three required guest groups extend runtime-config:

- MinipalTime: frequency/counter consistency, millisecond bracketing, zero/short/long delays, null counters, rounding, saturation and six joined workers with independent errno/error state.
- MinipalTimeWithoutTls: the identical time path executes in an image with no compiler-TLS directory, before image/environment/PAL initialization.
- RuntimeRandomTls: the real upstream constructor initializes the main thread and two batches of three workers, including concurrent storage isolation and reuse, at normal and relocated image bases.

Each case checks exact thread creates/joins/reaps, unchanged owned-page accounting, no leaked handles/events and complete physical-page recovery after component teardown. The extreme deadline arithmetic is tested directly instead of waiting thousands of seconds. The configuration archive now has thirteen source objects and links the verified minipal archive.

Local validation passed: Release tool builds, all 19 ordinary QEMU scenarios, 64-file source audit, eight hosted probe groups, four native-target groups, complete reference/WitOS source builds and the minimal startup diagnostic. Both 128/512 MiB runtime-config boots passed 197 required groups and 51 contained hardware faults. Ordinary successful boots retain 178 groups; runtime-config adds nineteen configuration/startup groups. The final import-free fixture is 41,984 bytes with 105 plain unwind records on the local compiler.

The strict minimal startup boundary falls from 83 to 79 unresolved symbols (broad inventory: 89 to 85). The removed native imports are __imp_QueryPerformanceCounter, __imp_QueryPerformanceFrequency, __imp_GetTickCount64 and __imp_SleepEx. Direct Windows time references from other consumers remain unresolved; this does not implement a general Windows API personality. Managed guest execution remains pending.
