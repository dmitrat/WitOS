# ADR 0023: Initialize the NativeAOT PAL over real WitOS services

**Status:** Implemented in WitOS 0.0.26.
**Date:** 2026-09-20.
**Scope:** PalInit in the native Workstation source port and dedicated runtime-config guest probe. ABI v13 is unchanged. RuntimeInstance, collector startup, ThreadStore attachment and managed execution remain pending.

## Decision and ordering

The pinned [Windows PalInit](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/windows/PalMinWin.cpp) initializes GCConfig, initializes the GC OS environment and determines the process CPU count. Implement the same required services in pal_init.witos.cpp using the real configuration path established in ADR 0022 and the existing GCToOSInterface::Initialize.

Before initialization, require the validated single-image context, a published immutable native environment and non-null g_pRhConfig. Missing prerequisites return false with ERROR_NOT_READY. Obtain the current thread snapshot from the kernel and require compiler TLS before reading errno. An image with raw FS storage alone is rejected before any compiler-GS access. Malformed snapshot data reports ERROR_GEN_FAILURE; a failed kernel query retains its mapped native status.

The kernel snapshot supplies CPU count and current-thread state. Writable FS/GS hints never provide those values. PalInit does not create compiler TLS, run native TLS constructors or initialize managed thread state.

## CPU profile

This port remains the controlled single-CPU q35 profile. A kernel-reported count other than one is unsupported.

Use actual RhConfig::ReadConfigValue for PROCESSOR_COUNT in decimal mode, preserving the upstream environment/private-setting lookup. Match the upstream validity window: values 1 through 65,535 are overrides; zero, unparseable and larger values fall back to kernel discovery. The only supported override is one. A valid override from two through 65,535 returns ERROR_NOT_SUPPORTED before GCConfig or the GC OS environment is initialized.

PalGetProcessCpuCount continues to report the kernel's actual count. This is an explicit prototype restriction, not emulation of additional CPUs, processor groups, affinity or job quotas.

## Publication and lifetime

A private atomic gate serializes initialization and ready-state inspection. No event wait or yield occurs while the gate is held. A concurrent/reentrant caller that cannot acquire it receives ERROR_BUSY and may retry; the caller is never parked by PalInit.

Once prerequisites and CPU policy pass, call the real GCConfig::Initialize and GCToOSInterface::Initialize. Publish Ready only after both complete successfully. GC OS failure returns ERROR_GEN_FAILURE without Ready; its preceding configuration writes are not advertised as transactional rollback. No page reservation, allocation, handle or worker is created by PalInit.

Success preserves incoming native last-error and errno. Failures report a native error; code paths that access errno restore it before returning. The compiler-TLS prerequisite is checked before that access, so raw-only failure is safe.

A successful later call returns cached readiness without overwriting refreshed GC configuration or rerunning initialization. Direct GC configuration/OS lifecycle changes remain externally serialized. GCToOSInterface::Shutdown ends this component's PAL lifecycle: subsequent PalInit returns ERROR_INVALID_STATE rather than resurrecting the environment. There is no restart or PAL shutdown API in this slice.

## Executable evidence

Release build, the full eighteen-scenario regression, both runtime-config boots, the 48-file audit and hosted NativeAOT probe passed locally. runtime-config also completed runtime-target and both native source-build profiles.

runtime-config retains the original configuration/CRT checks and adds:

| Group | Evidence |
| --- | --- |
| PalInitPrerequisites | Calls before image publication, before environment publication and with null g_pRhConfig fail without changing GC state; an actual image without a TLS directory is rejected before compiler TLS access |
| PalInitPolicy | Absent/one CPU settings succeed; zero, out-of-range and invalid text fall back to discovery; valid two/65,535 overrides fail without partial GC publication |
| PalInitLifecycle | Real GC config/OS initialization, kernel-derived CPU information despite overwritten raw TLS hints, preserved errors and memory accounting, cached calls from workers, and refusal after GC OS shutdown |

The cache test refreshes the heap limit and then disables its override input. Reinitializing GCConfig would change the observed value, so unchanged output proves the ready path does not rerun it. Worker tests retry the explicit BUSY result and verify per-thread error preservation.

The raw fixture retains the same native code/sections but has its PE32+ TLS directory cleared. Kernel assertions require CompilerTls to be zero and no worker creation. All images remain kernel-validated before allocation; no GS operation or fake runtime method is substituted for this test.

Successful scenarios require three joined/reaped workers; rejection scenarios require only the main thread. Every scenario checks its completion bitmask and exact resource reclamation. The dedicated command boots 128 and 512 MiB RAM, checks both relocated bases, and requires 165 user groups per boot, including nine dedicated groups and the existing 50 contained hardware faults. Ordinary test still requires eighteen VM scenarios and 156 groups.

The final local configuration fixture is 29,184 bytes with 48 plain unwind entries. The raw variant has a separate recorded hash. The Workstation archive contains 82 members, including twenty verified local adapter/helper objects. The probe archive contains five exact objects; minipal retains eleven members.

Strict linking now reports 99 unresolved symbols: seven GC environment, seventeen PAL, five deliberately excluded native transport/startup and seventy other requirements. PalInit must resolve; PalAttachThread and PalInitComAndFlsSlot must remain unresolved. These counts describe the tested link, not completion percentages.

## Next startup boundary

The pinned [RhInitialize](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/startup.cpp) now has its first PAL prerequisite. Its following Windows module lookup and atexit path, then InitDLL, still need explicit WitOS behavior. InitDLL includes interface dispatch, GC-event locking, optional diagnostics, RestrictedCallouts, RuntimeInstance, hardware exception delivery and InitializeGC.

Continue through that actual startup sequence with deliberate feature/profile choices and executable ownership/failure tests. Native process-exit callbacks must not stand in for real ThreadStore/GC teardown. Keep unsupported dependencies unresolved and raise image/stack/heap quotas only with measured runtime requirements. PalInit success is not RhInitialize success, a running collector or managed execution.
