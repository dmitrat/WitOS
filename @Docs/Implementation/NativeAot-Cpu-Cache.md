# CPU cache discovery for the GC

**Status:** Implemented in WitOS 0.0.31, experimental ABI v15.
**Scope:** Largest architecturally reported cache accessible to the single online x64 processor. This is a GC sizing hint, not a measurement of host hardware or an allocation limit.

## Architecture and ABI

CPUID instructions and decoding stay in Kernel.Arch.X64. WIT_CALL_CPU_CACHE_SIZE (29) accepts three zero arguments and returns the reported byte count. Invalid arguments return INVALID_ARGUMENT with zero result. Unknown/unsupported discovery or a backend with more than one online processor returns UNSUPPORTED and zero. No caller buffer, allocation or handle is involved.

The first result is cached, including an unknown result. Current callers execute with interrupts disabled on the bootstrap processor; hotplug, migration, heterogeneous CPUs and SMP aggregation are outside the profile.

Supported inputs:

- GenuineIntel: deterministic leaf 4 when advertised by leaf 0.
- AuthenticAMD: deterministic leaf 0x8000001D when the extended maximum leaf and topology-extension bit advertise it.
- Older AuthenticAMD: cache descriptors from extended leaves 0x80000005 and 0x80000006. The L3 value has the architecture's 512 KiB reporting granularity; it represents the encoded nominal/lower-bound quantity, not independently measured exact capacity.

Deterministic enumeration is limited to sixteen entries and must terminate. Cache types 1/2/3 are accepted, including instruction caches, matching the upstream largest-cache policy. Missing levels, reserved cache types and arithmetic overflow reject the discovery instead of publishing a partial value. The product uses widened arithmetic for ways, partitions, line size and sets. AMD legacy descriptors require valid line/associativity information when a nonzero capacity is reported. Unknown vendors and unsupported older Intel descriptor formats return unknown; no arbitrary fallback size is supplied.

The decoding follows the [Intel deterministic-cache formula](https://www.intel.com/content/dam/doc/manual/64-ia-32-architectures-optimization-manual.pdf) and [AMD CPUID cache fields](https://www.amd.com/content/dam/amd/en/documents/archived-tech-docs/design-guides/25481.pdf). A malformed advertised deterministic AMD table is not hidden by falling back to older leaves.

## Upstream interface

GCToOSInterface::GetCacheSizePerLogicalCpu uses the kernel query and returns zero when discovery is unavailable. Both trueSize values return the same unscaled quantity, as in the pinned [Windows x64 implementation](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/gc/windows/gcenv.windows.cpp#L849). Despite the method name, that implementation chooses the greatest reported cache size; it does not divide shared cache capacity by the hardware thread count.

The method is compiled and byte-verified in the actual WitOS runtime archive. The Windows reference, upstream source pins and standard CoreLib/compiler packages are unchanged. The strict missing-function probe now targets ResetWriteWatch; unsupported write-watch behavior is not replaced with a successful stub.

## Validation

Seventeen fixed-input decoder scenarios cover Intel and AMD deterministic data, AMD legacy data, missing topology-extension support, unknown vendors/leaves, empty tables, absent terminators, overflow, reserved types/associativity, missing levels, instruction-only cache data and largest-size selection independent of cache level. Reader call counts are bounded; a null reader is also rejected.

The ordinary GC discovery fixture compares the raw syscall result and both upstream trueSize forms against the kernel result, rejects each nonzero argument and retains exact resource accounting. Existing dedicated native worker tests also query both forms while verifying per-thread last-error and errno preservation.

The full kernel regression adds an Intel Nehalem QEMU boot alongside qemu64. Both emulated profiles reported 16,777,216 bytes with the pinned QEMU package; this value is an observation, not a hardcoded production constant or a universal property of those processors. The decoder fixtures independently exercise the differing vendor/leaf paths.

Ordinary successful boots now require 163 user groups and 51 contained hardware faults; the suite has nineteen VM scenarios. runtime-config retains fifteen dedicated groups, giving 178 groups per boot. Its local image is 36,352 bytes with 83 plain unwind entries. Release build, source audit, hosted NativeAOT, runtime-target/source and the 128/512 MiB dedicated boots passed; all nineteen kernel regression scenarios also passed locally.

The full source-port boundary drops to 95 unresolved symbols: five GC environment methods, sixteen PAL methods, five deliberately omitted transport symbols and 69 other platform/runtime requirements. Runtime/GC startup, managed thread attachment, exception integration and root enumeration remain unfinished. Cache discovery alone does not constitute a running collector.
