# WitOS RFC Draft Set

Current runtime bring-up: [PLAN.md](../PLAN.md), [M3 NativeAOT profile](Implementation/M3-NativeAOT-Profile.md), [P5 integration evidence](Implementation/P5-Managed-Integration.md) and [P5 completion audit](Implementation/P5-Completion-Audit.md). Original architecture drafts and historical milestones remain below.

This archive contains the first ten architecture RFCs drafted for WitOS.

The documents form the initial high-level architecture set: from the overall platform concept and capability model down through execution, communication, hardware, storage, presentation, packaging, software identity, signing, and trust.

## Documents

1. `RFC-0001-Architecture-Concept.md` — Draft v0.1
   - overall WitOS vision
   - upstream .NET compatibility
   - minimal kernel philosophy
   - universal hardware boundary
   - hosted-first development strategy

2. `RFC-0002-Resource-and-Capability-Model.md` — Draft v0.1
   - resources and typed capabilities
   - discovery vs authority
   - delegation, attenuation, revocation
   - locality and local/remote resources
   - resource composition

3. `RFC-0003-Application-and-Lifecycle-Model.md` — Draft v0.1
   - application vs process
   - persistent logical application identity
   - lifecycle and suspension
   - sessions and handoff
   - optional GUI and replaceable shells

4. `RFC-0004-Security-Identity-and-Capability-Delegation.md` — Draft v0.1
   - identity vs authority
   - least privilege
   - capability delegation and revocation
   - trusted consent UI
   - driver, DMA and IOMMU security

5. `RFC-0005-Execution-Scheduling-and-Compute-Model.md` — Draft v0.1
   - standard .NET threading compatibility
   - optional `OutWit.OS.Execution`
   - CPU topology, NUMA and heterogeneous cores
   - reservations, placement and guarantees
   - portable backend/fallback model

6. `RFC-0006-IPC-and-Local-Remote-Communication-Model.md` — Draft v0.1
   - kernel channels and messaging
   - local and remote communication semantics
   - capability transfer
   - shared memory and zero-copy
   - service identity, RPC layering and WitRPC integration

7. `RFC-0007-Universal-Hardware-Interface.md` — Draft v0.1
   - native firmware / compatibility HAL / hypervisor backends
   - CPU, memory, interrupts, timers, devices
   - DMA/IOMMU and power
   - managed driver model
   - hardware certification levels

8. `RFC-0008-Storage-and-Persistent-State-Model.md` — Draft v0.1
   - standard `System.IO` compatibility
   - logical storage identity vs path
   - application state, transactions and durability
   - versioning, replication and data locality
   - filesystems as replaceable providers

9. `RFC-0009-Presentation-Shell-and-Input-Architecture.md` — Draft v0.1
   - presentation resources rather than mandatory GUI
   - shell/compositor separation
   - adaptive UI and unified input
   - trusted presentation and secure input
   - remote presentation and performance profiles

10. `RFC-0010-Application-Packaging-and-Distribution.md` — Draft v0.3
    - applications as ordinary files
    - copy-directory-and-run deployment
    - arbitrary installation locations
    - optional packages, installers and stores
    - inexpensive developer signing
    - publisher identity, TOFU, key rotation and transparency
    - mandatory shell indication for unsigned/invalid software

## Storage implementation plan

- [Block storage and filesystem architecture](WitOS-Block-Storage-and-Filesystem-Architecture.md) - Draft v0.1; future M5 storage work: asynchronous block capabilities, managed filesystem providers, reuse of OutWit.Common.Fat, and later ext4/volume composition. Revisit when filesystem implementation begins; the immediate implementation focus remains M3 NativeAOT.

## Project Naming

Product name:

```text
WitOS
```

Primary package root:

```text
OutWit.OS.*
```

Examples:

```text
OutWit.OS.Resources
OutWit.OS.Security
OutWit.OS.Execution
OutWit.OS.Communication
OutWit.OS.Storage
OutWit.OS.Presentation
```

## Current RFC Sequence

```text
0001 Architecture Concept
0002 Resource & Capability Model
0003 Application & Lifecycle Model
0004 Security, Identity & Capability Delegation
0005 Execution, Scheduling & Compute Model
0006 IPC & Local/Remote Communication Model
0007 Universal Hardware Interface
0008 Storage & Persistent State Model
0009 Presentation, Shell & Input Architecture
0010 Application Packaging & Distribution
```

Likely follow-up RFCs discussed so far:

```text
0011 Kernel Architecture & ABI
0012 Driver Runtime & Device Manager
0013 Boot, Recovery & System Updates
0014 Distributed Resource Discovery & Placement
0015 .NET Runtime Port & Compatibility Contract
0016 Networking Architecture
0017 Software Identity, Signing & Trust
```

These are architecture drafts intended for continued review and revision rather than frozen specifications.

## Implemented Foundation

The initial native milestone now has a concrete boot contract and runnable implementation:

- [RFC 0011 — Kernel Architecture & ABI](RFC-0011-Kernel-Architecture-and-ABI.md) — Draft v0.13, boot v2, kernel execution and experimental user ABI.
- [M0 implementation and validation](Implementation/M0-Boot.md).
- [M1 memory and exception foundation](Implementation/M1-Memory-and-Exceptions.md) — first slice, eleven VM scenarios.
- [M1 kernel core](Implementation/M1-Kernel-Core.md) — paging, protection and preemptive execution; seventeen VM scenarios.
- [RFC 0015 — .NET Runtime Port & Compatibility Contract](RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md) — source-backed requirements and a pinned hosted NativeAOT probe.
- [NativeAOT host experiment](Implementation/NativeAot-Host-Probe.md).
- [M2 isolated execution plan](Implementation/M2-Isolated-Execution-Plan.md).
- [M2 implementation and experimental ABI](Implementation/M2-Isolated-Execution.md) — ring 3, private mappings/handles and contained user faults.
- [M2 sparse user memory](Implementation/M2-User-Memory.md) — reserve/commit/decommit/protect/release, recoverable exhaustion and ABI v2.
- [M2 user threads and TLS](Implementation/M2-User-Threads-and-Tls.md) — bounded preemption, raw TLS, join/exit, cleanup and ABI v3.
- [M2 events and deadlines](Implementation/M2-Events-and-Deadlines.md) — persistent signals, close/timeout ordering, kernel idle and ABI v4.
- [NativeAOT target/bootstrap evidence](Implementation/NativeAot-Target-Bootstrap.md) — static object, native C-host initialization and strict link boundaries; hosted only.
- [M2 guest PE loading](Implementation/M2-Pe-Image-Loading.md) — validated sections, zero-fill, relocation, isolation and rollback.
- [M2 native image/bootstrap handoff](Implementation/M2-Native-Module-Bootstrap.md) — readonly descriptors, C startup, cleanup and plain unwind metadata; ABI v5.
- [NativeAOT source-port decision and GC memory adapter](Implementation/NativeAot-Gc-Memory-Port.md) - pinned upstream interface, guest syscalls and strict incomplete-runtime link boundary.
- [NativeAOT native source build](Implementation/NativeAot-Source-Build.md) - complete native libraries, source-built Windows execution and strict WitOS port boundary.
- [GC environment discovery](Implementation/NativeAot-Gc-Discovery.md) - atomic allocator snapshots, quota/physical-pressure accounting and ABI v6.
- [GC events and yielding](Implementation/NativeAot-Gc-Events.md) - native signals/waits, safe slot reuse, failure containment and explicit timing limits.
- [Monotonic time and GC deadlines](Implementation/NativeAot-Gc-Time.md) - q35 HPET, IRQ-independent counts, finite waits and ABI v7.
- [Recursive native mutexes and Crst](Implementation/NativeAot-Mutexes.md) - kernel-owned thread identity, blocking/recursive locks, checked initialization and ABI v8.
- [Committed memory reset](Implementation/NativeAot-Gc-Reset.md) - whole-range validation, retained commitment/protection, no allocation and ABI v9.
- [Native runtime allocation](Implementation/NativeAot-Native-Heap.md) - bounded nothrow C++ new/delete, lazy commitment, cross-thread use and rollback.
- [Static compiler TLS](Implementation/NativeAot-Compiler-Tls.md) - validated single-module PE TLS, private per-thread templates, GS switching and rollback.
- [Dynamic C++ TLS lifecycle](Implementation/NativeAot-Dynamic-Tls.md) - real compiler constructors/destructors, user-space thread entry/exit, bounded cleanup and failure containment.
- [WitOS PAL and thread discovery](Implementation/NativeAot-Pal-Thread-Discovery.md) - kernel-owned thread/stack snapshots, actual PAL declarations, explicit missing services and ABI v10.
- [PAL memory, events and waits](Implementation/NativeAot-Pal-Memory-and-Waits.md) - committed allocation/protection, typed events, monotonic waits and truthful yield result; ABI v11.
- [Detached PAL workers](Implementation/NativeAot-Pal-Background-Threads.md) - actual native callbacks, C++ TLS cleanup, automatic stack/TLS/handle reclamation and ABI v12.
- [Native last-error](Implementation/NativeAot-Pal-Last-Error.md) - per-thread 32-bit storage, real direct/import bindings, PAL failure diagnostics and ABI v13.
- [Native PAL module discovery](Implementation/NativeAot-Pal-Module-Discovery.md) - shared checked image metadata, section-aware lookup, inclusive bounds and TLS/worker lifecycle coverage.
- [M3 runtime integration plan](Implementation/M3-Runtime-Integration-Plan.md) - remaining work packages, acceptance and estimate.
- [Immutable native environment and PAL strings](Implementation/NativeAot-Pal-Environment.md) - validated readonly startup values, native UTF conversion and the remaining real GCConfig boundary.
- [Upstream runtime configuration](Implementation/NativeAot-Runtime-Configuration.md) - real RhConfig/GCConfig execution in dedicated guest boots, checked OOM behavior and native C/errno support.
- [Native PAL initialization](Implementation/NativeAot-Pal-Initialization.md) - actual GC config/OS initialization, startup prerequisites, CPU policy and cached lifecycle.
- [BootTo.NET source review](Implementation/BootToNET-Review.md) - verified applicability, harness differences and independent hosted GC/unwind regression additions.
- [Native process exit](Implementation/NativeAot-Process-Exit.md) - real atexit callbacks, TLS ordering, bounded cleanup and checked upstream registration failure.
- [Upstream interface-dispatch startup](Implementation/NativeAot-Interface-Dispatch-Startup.md) - real InitDLL entry subsystem, AllocHeap lifecycle and OOM/concurrency checks.
- [RuntimeInstance and ThreadStore creation](Implementation/NativeAot-Runtime-Instance.md) - real upstream object startup, allocation rollback and kernel-confirmed compiler TLS metadata.
- [Process memory barriers](Implementation/NativeAot-Process-Barrier.md) - ABI v14 data-memory fence, real GC/PAL bindings and single-processor limitations.
- [CPU cache discovery](Implementation/NativeAot-Cpu-Cache.md) - bounded architectural cache metadata, real GC binding and Intel/AMD guest validation.
- [Atomic event WaitAny](Implementation/NativeAot-Wait-Any.md) - real PAL multi-event waiting, deadlines and generation-safe completion.
- [Memory-pressure notifications](Implementation/NativeAot-Memory-Pressure.md) - kernel-driven low-memory events, hysteresis and real physical/quota tests.
- [PAL sleep deadline regression](Implementation/PAL-Sleep-Deadline-Regression.md) - deterministic reproduction and correction of the CI idle assumption.
- [Immediate next steps](Implementation/Next-Steps.md).

The earlier RFCs describe the long-term vision. These implementation notes distinguish working behavior from future runtime and operating-system features.

- [Pre-P6 code quality and coverage audit](Implementation/P5-Code-Quality-and-Coverage-Audit.md) - reproduced tooling defects, coverage gaps and Q1 follow-up.

- [Q1 quality hardening](Implementation/Q1-Quality-Hardening.md) - completed audit fixes, real return-address hijack, measured native branch coverage and sanitizer/fuzz lanes.

- [P6 CoreCLR/JIT profile and boundary](Implementation/P6-CoreClr-Profile.md) - pinned source-built reference, ordinary IL hosted acceptance, full direct/delay/ordinal import inventory and WitOS port requirements.
- [P6 executable memory](Implementation/P6-Executable-Memory.md) - sparse RW/RX views and VMToOS adapter.
- [P6 dynamic unwind](Implementation/P6-Dynamic-Unwind.md) - function tables, target/collided dispatch and Windows/guest acceptance.
- [P6 assembly storage](Implementation/P6-Assembly-Storage.md) - unchanged payload delivery, readonly guest IO and boot ownership.
- [P6 host and binding](Implementation/P6-Host-Binding.md) - active upstream hosting work and remaining platform boundaries.
- [P6 native libraries](Implementation/P6-Native-Libraries.md) - immutable DLL loading, protected image ownership, actual library PAL leaves and remaining loader lifecycle.

- [P6 DLL process lifecycle](Implementation/P6-DLL-Lifecycle.md) - readonly user-space callback plans, attach/detach rollback and explicit process-only bring-up boundary.

- [P6 DLL thread notifications](Implementation/P6-DLL-Thread-Notifications.md) - cooperative kernel-owned lifecycle, actual worker callbacks and remaining DLL TLS boundary.
