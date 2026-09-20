# ADR 0022: Execute upstream runtime configuration inside WitOS

**Status:** Accepted; implemented in WitOS 0.0.25.
**Date:** 2026-09-20.
**Scope:** Dedicated native runtime-config guest probe; ABI v13 unchanged. PalInit, collector startup, ThreadStore attachment and managed execution remain pending.

## Decision and source boundary

Execute real RhConfig and GCConfig logic in a separate guest image built from the pinned NativeAOT tree. Ordinary boot builds remain independent of the full source-build prerequisite.

A complete gcenv.ee.cpp object requires collector, thread-store, finalization, diagnostics and suspension services. The probe selects its actual configuration bodies and the configuration prefix of gcconfig.cpp. The Workstation library still compiles complete gcconfig.cpp/gcenv.ee.cpp with their real missing dependencies.

The generator consumes hash-verified bytes and records exact input/generated hashes. It copies the whole RhConfig.cpp with the allocation checks below, the gcconfig.cpp prefix containing accessors/defaults/initialize/refresh/enumeration, and the unchanged GCToEE GetBooleanConfigValue/GetIntConfigValue/GetStringConfigValue/FreeStringConfigValue bodies. The unrelated affinity parser stays outside this probe.

The fixture supplies typed configuration input globals, including actual GCHeapHardLimitInfo and settings/knob blobs with layout assertions against RhConfig::Config. These model the compiler's format; they are not an executed ILC-produced managed module. No collector or ThreadStore implementation is supplied.

The 48-file audit adds gcconfig.h and nativeaot/Runtime/gcenv.h. Source/package revisions remain .NET 10.0.8 at b82454cad0aaaae3db2cf18fbf2cccc36e201ccc. The upstream working tree stays clean.

## Build and profile

    dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-config

The command runs runtime-source, including runtime-target, refreshes both native profiles and verifies all four probe objects byte-for-byte in WitOS.ConfigProbe.lib. It then builds an import-free PE and boots QEMU at 128 and 512 MiB RAM.

An isolated CMake target uses the actual NativeAOT headers/feature definitions and rejects CoreCLR definitions. Its guest flags select static CRT declarations, size optimization and disabled Windows GS/CFG/EH instrumentation, matching existing native fixtures. It links no Windows/CRT library. The Workstation library retains its normal native compilation profile.

CMake creates the empty target before deferred inspection and supplies the NativeAOT sources/properties afterward. Compile-command export is explicit. Current source-input and archive hashes are checked again before guest linking, rejecting changed or stale inputs.

Only runtime-config embeds this image and requires its additional markers. Ordinary build/run/test never silently consume cached configuration artifacts. CI runs the new command after verified QEMU setup.

## Actual behavior exercised

The probe executes [RhConfig](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/RhConfig.cpp) and [GCConfig](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/gc/gcconfig.cpp), including:

- Environment values overriding private settings, followed by GCToEE fallback to public knobs.
- Case-sensitive private keys; case-insensitive knob keys; exact lowercase true for boolean knob values.
- The pinned strict environment integer parser with its sixteen-character limit; strtoull for embedded numbers. Invalid embedded numeric text can still produce zero with a successful lookup; this upstream behavior is retained.
- Actual cached accessors, observed by avoiding a second environment lookup and preserving last-error.
- Supplied/default distinctions, the NativeAOT conservative-GC setting, heap-limit refresh from GCHeapHardLimitInfo and enumeration of updated values.

These are configuration values only. The tests do not allocate a managed heap, enforce limits on a collector, initialize RuntimeInstance or register managed modules. Existing native quotas remain unchanged.

## Native C functions and errno

crt_config.witos.cpp implements strlen, memcpy, strcmp, _stricmp and strtoull for a fixed C locale. Comparison uses unsigned bytes; case folding is ASCII. Callers retain normal native buffer/pointer preconditions, including non-overlap for memcpy. Mutable locale and the remainder of the CRT remain unsupported.

Integer conversion accepts bases 0 and 2-36, ASCII whitespace, signs and octal/hex prefixes; it reports the stopping position, consumes digits after overflow, saturates to ULLONG_MAX and sets ERANGE. Invalid base/null input returns zero with EINVAL. Success and no-conversion preserve prior errno. See the [Microsoft strtoull contract](https://learn.microsoft.com/en-us/cpp/c-runtime-library/reference/strtoull-strtoull-l-wcstoull-wcstoull-l?view=msvc-170).

_errno returns per-thread static compiler TLS storage, with a readonly import binding. It is available before dynamic constructors, is zero on thread creation/reuse, and is independent of raw-FS last-error. Raw-only images without compiler TLS do not use this slice.

## Explicit RhConfig OOM correction

Pinned TryGetStringValue can return true with a null UTF-8 result and fails to check its wide-buffer allocation. The derived source checks both allocations, rejects zero/oversized second lookup results, and publishes output only after successful conversion. Failure preserves caller output and releases the temporary wide buffer through the actual upstream holder.

The original checkout and Windows reference are unchanged. Both the WitOS Workstation library and probe compile the same recorded RhConfig overlay. Reports distinguish the clean working tree from this compiled-source correction.

## Validation and limits

Release build, the complete eighteen-scenario regression, both runtime-config boots, the 48-file audit and hosted probe passed locally. runtime-config also completed runtime-target and both native source-build profiles.

| Additional group | Evidence |
| --- | --- |
| RuntimeConfigCrt | Numeric boundaries/signs/prefixes/end pointers/overflow, byte/string functions and independent last-error |
| RhConfigPrecedence | Environment/settings/knob priority, matching, missing/invalid values, cache and stack-size setting |
| RhConfigStrings | Unicode, resize, three allocation-failure paths, preserved output, retry and no leaks |
| GcConfigValues | Actual initialization, provided/default values and unchanged GCToEE string-config behavior |
| GcConfigRefresh | Actual heap-limit override refresh and enumeration |
| RuntimeConfigThreads | Yielding workers, independent errors and zero errno in a reused slot |

Each dedicated boot retains 156 ordinary groups plus these six, and 50 contained hardware user faults. Configured/empty environments and two relocated bases run with a required completion bitmask, three joins/reaps and full resource reclamation.

The local probe is 25,600 bytes with 41 plain unwind entries. Workstation has 81 members, including nineteen verified local adapter/helper objects; the separate probe has four objects, and minipal eleven. Strict linking reports 100 unresolved symbols: seven GC environment, eighteen PAL, five deliberately excluded transport/startup and seventy other requirements. PalInit and PalAttachThread remain unresolved.

## Next boundary

Implement PalInit using this verified configuration path and existing GC OS initialization, with explicit environment/startup ordering and processor-count/profile policy. Continue to actual RuntimeInstance/collector startup and ThreadStore lifecycle, preserving the real FixAllocContext dependency on detach. Managed-module registration, GC rendezvous, exceptions/unwinding and measured image/stack/quota expansion remain M3 work.
