# Runtime CPU capability policy

**Status:** WitOS 0.0.41; experimental ABI v17 unchanged.

The WitOS aotminipal archive now implements minipal_getcpufeatures and minipal_detect_rosetta in Kernel.Arch.X64. The Windows source profile retains upstream cpufeatures.c. Feature constants and grouping are taken from the pinned upstream cpufeatures.h/c; these two canonical-byte pins bring the audit to 66 files.

## State preservation is part of capability

The scheduler saves baseline x87/SSE state with FXSAVE/FXRSTOR. Kernel paging activation now explicitly clears inherited CR4.OSXSAVE as well as FSGSBASE, and verifies both stay clear. It does not enable XSAVE, AVX, extended register state or new user segment-base instructions.

Hardware AVX support alone is insufficient: enabling AVX also depends on OS-managed extended state. The distinction follows the [Intel instruction-set/system programming reference](https://www.intel.com/content/dam/develop/external/us/en/documents/319433-024-697869.pdf). User-space discovery observes OSXSAVE through CPUID, never reads mutable TLS as authority and never executes XGETBV. Unexpected OSXSAVE enablement or a missing FXSR/SSE/SSE2 baseline fails the component instead of silently publishing a misleading feature mask.

The initial optional-feature policy is deliberately conservative:

- The upstream SSE4.2 group requires SSE3, SSSE3, SSE4.1, SSE4.2 and POPCNT together.
- The upstream AES group requires both AESNI and PCLMULQDQ.
- AVX/AVX2/AVX-512/AVX10/APX and all other optional groups are withheld until separately admitted and tested. A zero mask means baseline x64/SSE2, not an absence of floating-point/SSE support.

Discovery uses bounded CPUID leaves in user-space architecture code. It creates no resources, requires no compiler TLS/PAL/environment initialization and preserves errno/native last-error. No new syscall or public hardware API is added. The adapter's /Od and /GS- bootstrap compile settings keep its bounded native code compatible with the current guest object profile; no security-cookie or XState API stubs are supplied.

Rosetta detection retains upstream's informational VirtualApple brand-string heuristic. It checks the maximum extended leaf, reads exactly three brand leaves and performs a bounded, case-sensitive search limited to 48 bytes and the first zero. It does not claim secure platform identity or hardcode a successful/negative response.

## Guest evidence

A separate, import-free CPU fixture avoids growing the main configuration image beyond its existing metadata budget. It links the verified source-built aotminipal archive. The arch-specific test assembly and production CPUID instructions both remain in Kernel.Arch.X64.

- MinipalCpuFeatures tests synthetic CPUID snapshots, missing dependency bits, unsupported baseline/state, bounded brand matching, actual discovery and three native worker contexts. Hardware CRC32 is compared with a software CRC32C result. Where advertised, real PCLMUL and AES instructions execute; distinct AES state survives a yield in a nonvolatile XMM register. errno and native last-error stay thread-local and unchanged.
- MinipalCpuWithoutTls repeats discovery/instruction checks in an image without compiler TLS, before native image/PAL initialization.
- AvxDisabled executes a VEX/YMM instruction and requires a contained #UD at its exact linker-map address. This runs even on the AVX-capable max CPU, so rejection is not inferred merely from a missing hardware feature bit.

runtime-config now runs four full boots and explicitly checks each CPU's reported mask:

| QEMU CPU / RAM | Approved optional mask | Hardware AVX |
| --- | --- | --- |
| qemu64 / 128 MiB | 0 | 0 |
| qemu64 / 512 MiB | 0 | 0 |
| Nehalem / 256 MiB | 1 (SSE4.2 group) | 0 |
| max / 256 MiB | 513 (SSE4.2 and AES groups) | 1 |

The mask checks supplement instruction execution, CPUID dependency tests and the explicit AVX fault. Each component validates thread counts, owned memory, handles/events and physical-page recovery. The additional #UD brings runtime-config to 54 expected contained faults; ordinary boots still require 51. The new total is 205 configuration user groups versus 178 ordinary groups.

## Validation and remaining boundary

All four runtime-config profiles passed locally. The CPU fixture is 8,704 bytes with 29 plain unwind entries; the existing configuration image remains separate. The archive contains sixteen configuration source objects and eleven minipal members. The source audit (66 files), hosted probes, full Windows/WitOS source builds and minimal startup diagnostic passed. All 19 ordinary QEMU scenarios passed; Release solution builds completed without warnings or errors.

The removed __imp_GetEnabledXStateFeatures dependency reduces minimal startup from 72 to 71 unresolved symbols (broad source boundary 78 to 77). This does not implement the Windows API or provide XSAVE/context restoration, managed GC coordination or guest .NET execution. CoreLib and the pinned managed compiler remain unchanged.
