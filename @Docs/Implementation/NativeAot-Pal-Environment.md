# ADR 0021: Immutable native environment and PAL string conversion

**Status:** Accepted; implemented in WitOS 0.0.24, user ABI v13 unchanged.
**Date:** 2026-09-20.
**Scope:** User-space native configuration transport and UTF-16/UTF-8 conversion. PalInit, GCConfig execution, ThreadStore attachment and managed execution remain pending.

## Context

The pinned Windows PalInit first calls GCConfig::Initialize, then GCToOSInterface::Initialize and CPU-count discovery. GCConfig obtains values through the actual GCToEEInterface and RhConfig. Source analysis establishes the following order:

- GCToEE boolean configuration forces gcConservative true in this NativeAOT profile; otherwise it consults RhConfig private settings before compiler-embedded public knobs.
- RhConfig private settings consult DOTNET_-prefixed environment values first, then the compiler-embedded settings blob. Environment integers use hexadecimal unless the caller explicitly requests decimal.
- Embedded settings use case-sensitive keys; public knobs use case-insensitive keys and decimal integer conversion. Their real string/numeric CRT dependencies remain part of the incomplete link inventory.
- This pinned NativeAOT GCToEE string-config method returns false. RhConfig nevertheless has a separate string environment accessor, using PalCopyTCharAsChar and caller-owned native allocations. Its behavior and allocation failures still need integration testing before claiming complete RhConfig support.

The exact [RhConfig.cpp](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/RhConfig.cpp), RhConfig.h, RhConfigValues.h, [gcconfig.cpp](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/gc/gcconfig.cpp), and [gcenv.ee.cpp](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/gcenv.ee.cpp) are added to the SHA-256 audit. There are now 46 source/license files; the .NET 10.0.8 Git/VMR/package revisions are unchanged.

The first clean CI run exposed six pre-existing PAL header hashes calculated from CRLF Git worktree copies. Fresh downloads from the same pinned commit differed only by CRLF-to-LF conversion. Those pins and the local audit cache were corrected to the canonical downloaded bytes; strict byte hashing remains unchanged. This makes the source audit reproducible on an empty runner cache.

## Decision and alternatives

Supply a bounded immutable environment from initialized readonly data in the component's own image. The private native startup helper accepts an entry table; it publishes the table only after complete validation against the existing checked image descriptor. No new kernel call, kernel environment parser, writable environment API or public resource API is added.

Host environment inheritance would make guest configuration depend on the development machine and require a new boot/launch contract. Returning missing for every variable would only support an empty profile. A mutable process environment would add synchronization and buffer-resizing races that this startup slice does not need. The selected explicit readonly table supports real values and an explicitly initialized empty environment.

## Startup and validation contract

Call wit_pal_environment_initialize once after publishing the checked single-image context and before native TLS constructors or worker creation. Initialization/lifecycle is externally serialized. The TLS startup helper now accepts an already published context only when it is the exact descriptor from its validated startup argument; otherwise it fails fast. Existing startup paths still publish that context themselves.

The limits are sixteen entries, 1-63 UTF-16 units per name and 0-1,023 units per value. Names are printable non-space ASCII excluding '='; lookup folds ASCII letter case. Values are UTF-16 code units and may be empty. Unicode case folding, mutable values, host inheritance, enumeration and child-process inheritance are outside this profile.

The entire aligned table and every complete terminated name/value range must lie within initialized readonly, non-executable image sections. Lengths, internal NULs, terminators and duplicate names are checked before any state publication. Writable data, stacks, unmapped addresses and oversized fields are rejected without dereferencing their contents. The empty environment requires a null table and zero count.

Invalid initialization returns false with ERROR_INVALID_PARAMETER; missing image context reports ERROR_NOT_READY. Failure leaves the environment unpublished and can be retried. Reinitialization after success returns ERROR_ALREADY_INITIALIZED. Successful initialization preserves native last-error and allocates nothing. The table remains valid for the whole component lifetime.

## PAL calls and bindings

PalGetEnvironmentVariable implements the selected Windows Unicode PAL signature. It and the direct/import GetEnvironmentVariableW bindings read the same state. The import cell is readonly and the x64 tail-jump facade lives in Kernel.Arch.X64/native_environment.asm.

A successful copy returns the UTF-16 unit count excluding NUL. A zero-capacity query or insufficient destination returns the required count including NUL, leaving the destination and prior error unchanged. An empty present value returns one for a size query, or writes NUL and returns zero for a sufficient buffer. Missing keys return zero with ERROR_ENVVAR_NOT_FOUND. Invalid names and nonzero-size null destinations report ERROR_INVALID_PARAMETER; lookup before publication reports ERROR_NOT_READY. Error paths do not modify the destination.

These size/missing-value conventions follow [GetEnvironmentVariableW](https://learn.microsoft.com/en-us/windows/win32/api/processenv/nf-processenv-getenvironmentvariablew); unsupported name forms and explicit startup readiness are WitOS profile restrictions. Callers must supply valid readable query names and writable copy buffers; these are unprivileged native calls, not kernel copy-out operations.

PalCopyTCharAsChar returns a separately allocated, NUL-terminated UTF-8 string that the caller releases with delete[]. It uses the actual bounded native nothrow heap. BMP characters, surrogate pairs and supplementary-plane endpoints are encoded; isolated surrogate units become U+FFFD, matching the upstream use of CP_UTF8 with zero flags in [WideCharToMultiByte](https://learn.microsoft.com/en-us/windows/win32/api/stringapiset/nf-stringapiset-widechartomultibyte).

The source limit is 32,767 UTF-16 units. Null input reports ERROR_INVALID_PARAMETER, longer input ERROR_BUFFER_OVERFLOW, and allocation failure ERROR_NOT_ENOUGH_MEMORY. Success preserves prior last-error. Allocation reserves the bounded worst case of three bytes per UTF-16 unit plus NUL; this prevents a write overrun even if a caller changes input between length measurement and copying. A caller must still keep its source valid and stable for a meaningful conversion. No general WideCharToMultiByte or CRT substitute is supplied.

## Validation and consequences

Release build, runtime-port, all eighteen VM scenarios, runtime-audit, runtime-probe and runtime-source passed locally; runtime-source also ran runtime-target.

The guest fixture uses unchanged pinned Pal.h declarations with the actual Unicode profile. It has no OS/CRT imports. Four required groups cover:

| Group | Evidence |
| --- | --- |
| PalEnvironment | Positive/missing/empty values, case folding, direct/import bindings, zero/short/exact buffers, 300-unit values, full sixteen-entry capacity and maximum name/value lengths |
| PalEnvironmentValidation | Unavailable context, writable/unmapped/truncated/oversized manifests, embedded NUL, duplicate names, no partial publication, valid retry and refused replacement |
| PalUtf8Copy | ASCII, Cyrillic, Hebrew, supplementary characters, encoding boundaries, malformed surrogates, maximum/overlong input, real heap exhaustion, recovery and unchanged memory accounting |
| PalEnvironmentThreads | Reads from actual TLS constructors/destructors, two yielding workers, per-thread errors, a reused worker slot and post-cleanup lookup |

The full suite requires 156 user groups and 50 contained hardware user faults across eighteen VM scenarios. The environment fixture runs at both relocated image bases and with an explicitly empty environment. Each run checks three joins/reaps and complete physical-page/handle/event/reservation reclamation. The local fixture is 16,384 bytes with 32 plain unwind entries.

The source-built Workstation archive contains eighty members, including seventeen verified local adapter/helper objects; minipal retains eleven members. Strict linking reports 105 unresolved symbols: seven GC environment, eighteen PAL, five deliberately excluded native transport/startup and 75 other platform/runtime requirements. The two new PAL methods and both GetEnvironmentVariableW bindings must resolve. PalInit and PalAttachThread must remain unresolved. Counts are diagnostics, not a compatibility percentage.

## Next action

Execute the real RhConfig and GCConfig paths with environment, embedded settings/knobs and native defaults, completing their actual string/numeric dependencies and checking ownership/failure semantics. Only then implement and test PalInit with real GC environment initialization. Native image discovery, configuration transport and C++ TLS do not replace RuntimeInstance/GC startup, ThreadStore lifecycle or managed module registration.
