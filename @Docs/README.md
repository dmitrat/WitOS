# WitOS RFC Draft Set

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

- [RFC 0011 — Kernel Architecture & ABI](RFC-0011-Kernel-Architecture-and-ABI.md) — Draft v0.5, boot v2, kernel execution and experimental user ABI.
- [M0 implementation and validation](Implementation/M0-Boot.md).
- [M1 memory and exception foundation](Implementation/M1-Memory-and-Exceptions.md) — first slice, eleven VM scenarios.
- [M1 kernel core](Implementation/M1-Kernel-Core.md) — paging, protection and preemptive execution; seventeen VM scenarios.
- [RFC 0015 — .NET Runtime Port & Compatibility Contract](RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md) — source-backed requirements and a pinned hosted NativeAOT probe.
- [NativeAOT host experiment](Implementation/NativeAot-Host-Probe.md).
- [M2 isolated execution plan](Implementation/M2-Isolated-Execution-Plan.md).
- [M2 implementation and experimental ABI](Implementation/M2-Isolated-Execution.md) — ring 3, private mappings/handles and contained user faults.
- [M2 sparse user memory](Implementation/M2-User-Memory.md) — reserve/commit/decommit/protect/release, recoverable exhaustion and ABI v2.
- [Immediate next steps](Implementation/Next-Steps.md).

The earlier RFCs describe the long-term vision. These implementation notes distinguish working behavior from future runtime and operating-system features.
