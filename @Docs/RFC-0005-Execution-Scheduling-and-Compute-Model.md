# WitOS
## RFC 0005 — Execution, Scheduling & Compute Model
### Draft v0.1

## 1. Status

Draft.

This document defines the execution, scheduling, CPU topology, compute allocation, resource reservation, placement, and advanced threading model of WitOS.

It builds upon:

```text
RFC 0001 — Architecture Concept
RFC 0002 — Resource & Capability Model
RFC 0003 — Application & Lifecycle Model
RFC 0004 — Security, Identity & Capability Delegation
```

The central principles are:

> **Standard .NET threading semantics remain unchanged.**

and:

> **Advanced execution control is additive and exposed through optional WitOS APIs.**

---

## 2. Motivation

Most mainstream operating-system execution models were designed when:

```text
one CPU
one core
one hardware thread
```

was the normal machine configuration.

Modern systems may instead contain:

```text
multiple sockets
multiple NUMA nodes
multiple chiplets
dozens or hundreds of physical cores
SMT siblings
performance cores
efficiency cores
heterogeneous accelerators
GPU compute
NPU compute
remote compute
```

Traditional scheduling abstractions expose only a small part of this structure to applications.

Typical application-level concepts remain:

```text
Thread
priority
affinity mask
process
```

These are useful but insufficient for workloads that care about:

```text
cache locality
NUMA locality
exclusive cores
SMT interference
energy efficiency
latency
memory bandwidth
thermal behavior
heterogeneous cores
```

WitOS treats compute topology as a first-class resource while preserving standard .NET compatibility.

---

## 3. Compatibility Is Mandatory

WitOS does not redefine:

```csharp
Thread
Task
ThreadPool
Parallel
SynchronizationContext
TaskScheduler
```

Existing .NET applications must continue to use these with standard .NET semantics.

For example:

```csharp
await Task.Run(Calculate);

var thread = new Thread(Worker);
thread.Start();

Parallel.For(0, count, Process);
```

must remain ordinary .NET code.

No application should need WitOS-specific APIs merely to preserve normal .NET behavior.

---

## 4. Additive Execution Model

Advanced execution features are exposed through optional assemblies such as:

```text
OutWit.OS.Execution
OutWit.OS.Execution.Abstractions
```

The exact package decomposition may evolve.

Conceptually:

```text
Application
    │
    ├── Standard .NET
    │      Thread / Task / ThreadPool
    │
    └── OutWit.OS.Execution
           advanced execution API
```

Both paths ultimately use the operating system's execution infrastructure.

---

## 5. Standard .NET Must Not Depend on WitOS Extensions

The dependency direction is strictly:

```text
OutWit.OS.Execution
        ↓
standard .NET
```

not the reverse.

The upstream .NET runtime must not need knowledge of WitOS-specific execution concepts.

---

## 6. Execution as a Resource

Compute capacity is represented as a resource.

Conceptually:

```text
IComputeResource
    ├── ICpuComputeResource
    ├── IGpuComputeResource
    ├── INpuComputeResource
    └── IRemoteComputeResource
```

This RFC focuses primarily on CPU execution.

---

## 7. CPU Is Not a Flat Array

WitOS must not treat a CPU as merely:

```text
CPU 0
CPU 1
CPU 2
...
```

The actual topology may resemble:

```text
Machine
 ├── Socket 0
 │    ├── NUMA Node 0
 │    │    ├── Cache Domain A
 │    │    │    ├── Physical Core 0
 │    │    │    │    ├── Hardware Thread 0
 │    │    │    │    └── Hardware Thread 1
 │    │    │    └── Physical Core 1
 │    │    └── Cache Domain B
 │    └── NUMA Node 1
 └── Socket 1
```

or:

```text
SoC
 ├── Performance Core 0
 ├── Performance Core 1
 ├── Efficiency Core 0
 ├── Efficiency Core 1
 ├── Efficiency Core 2
 └── Efficiency Core 3
```

Scheduling must account for this structure.

---

## 8. Topology Discovery

Advanced applications may query CPU topology.

Conceptually:

```csharp
IComputeTopology topology =
    await Execution.GetTopologyAsync();
```

Possible information includes:

```text
sockets
NUMA domains
physical cores
hardware threads
core classes
cache domains
shared caches
memory domains
frequency classes
performance characteristics
energy characteristics
```

Topology discovery is optional for ordinary applications.

---

## 9. Topology Is Descriptive, Not Authoritative

Knowing that a core exists does not imply permission to reserve it.

Authority remains capability-based.

---

## 10. Three Levels of Execution Control

WitOS defines three conceptual levels.

### Level 1 — Intent

Application describes workload intent.

Example:

```text
Interactive
Background
ComputeIntensive
LatencySensitive
EnergyEfficient
```

### Level 2 — Topology Constraints

Application describes desired topology.

Example:

```text
8 physical cores
same NUMA node
avoid SMT siblings
prefer performance cores
```

### Level 3 — Exact Placement

Application requests exact hardware entities.

Example:

```text
PhysicalCore 4
PhysicalCore 6
HardwareThread 11
```

Exact placement is an advanced capability.

---

## 11. Intent Should Be Preferred

Most applications should express intent rather than hardware placement.

For example:

```csharp
var allocation =
    await Cpu.AcquireAsync(
        new CpuRequirements
        {
            PreferredParallelism = 8,
            Usage = CpuUsage.ComputeIntensive
        });
```

This allows WitOS to choose the best placement for the current device.

---

## 12. Exact Core IDs Are an Escape Hatch

APIs equivalent to:

```csharp
RunOnCore(7);
```

may exist.

They should not be the normal programming model.

Reasons include:

```text
SMT topology
NUMA topology
P/E cores
thermal constraints
cache domains
dynamic power policy
CPU hot-plug
virtualization
```

A numeric CPU ID carries insufficient semantic information.

---

## 13. CPU Requirements

A CPU allocation request may contain properties such as:

```csharp
new CpuRequirements
{
    MinimumCores = 4,
    PreferredCores = 12,

    PreferPhysicalCores = true,
    PreferSameNumaNode = true,
    PreferPerformanceCores = true,

    SmtPolicy = SmtPolicy.AvoidSiblings,

    Usage = CpuUsage.ComputeIntensive
};
```

The exact API is deferred.

The semantic model is defined here.

---

## 14. Requirements vs Preferences

WitOS must distinguish hard requirements from preferences.

Example:

```text
Requirement:
    at least 4 execution units

Preference:
    12 physical cores
    same NUMA node
```

A failed preference does not prevent allocation.

A failed requirement does.

---

## 15. Allocation Result

Resource acquisition returns not only a usable compute resource but also the guarantees actually granted.

Conceptually:

```csharp
CpuAllocation allocation =
    await Cpu.AcquireAsync(requirements);
```

Possible metadata:

```text
requested
granted
best-effort
unsupported
degraded
```

---

## 16. Example Allocation Negotiation

Request:

```text
16 physical cores
exclusive
same NUMA node
avoid SMT
```

Result:

```text
Granted:
    12 physical cores
    same NUMA node
    SMT avoided

Not granted:
    exclusive ownership
```

The application may accept or reject the result.

---

## 17. Guarantee Levels

A property may be classified as:

```text
Guaranteed
BestEffort
Unavailable
```

Example:

```text
Parallelism = 8          Guaranteed
SameNumaNode             Guaranteed
ExclusivePhysicalCores   BestEffort
DedicatedCache           Unavailable
```

Applications must not infer stronger guarantees than reported.

---

## 18. Portable Fallback

`OutWit.OS.Execution` should support non-WitOS platforms where practical.

Conceptually:

```text
OutWit.OS.Execution
      │
      ├── WitOS backend
      ├── Windows backend
      ├── Linux backend
      ├── macOS backend
      └── Generic .NET fallback
```

The application API remains the same.

---

## 19. Generic .NET Fallback

When the host platform provides no advanced topology control:

```text
CpuRequirements
      ↓
Generic backend
      ↓
Thread
Task
ThreadPool
Semaphore
custom worker pool
```

The program still executes.

Only advanced guarantees are absent.

---

## 20. Portability Principle

Referencing `OutWit.OS.Execution` should not by itself make an application WitOS-only.

A portable application should be able to use:

```csharp
await Cpu.AcquireAsync(...);
```

on:

```text
WitOS
Windows
Linux
macOS
```

subject to backend capability.

---

## 21. Capability Detection

Portable code tests the actual allocation.

Good:

```csharp
if (allocation.Guarantees.ExclusivePhysicalCores)
{
    RunLowLatencyMode();
}
```

Bad:

```csharp
if (OperatingSystem.IsWitOS())
{
    RunLowLatencyMode();
}
```

Applications depend on guarantees, not OS names.

---

## 22. CPU Partition

A `CpuPartition` represents compute capacity assigned under an execution contract.

Conceptually:

```text
CpuPartition
    ├── CPU capacity
    ├── topology constraints
    ├── scheduling policy
    ├── lifetime
    └── guarantees
```

It is not necessarily equivalent to fixed physical cores.

---

## 23. Partition Implementations

A partition may represent:

```text
exact physical cores
logical processors
scheduler quota
CPU time reservation
virtual CPUs
shared execution group
```

depending on backend capability.

Applications interact with the contract, not implementation detail.

---

## 24. Reservation Lifetime

CPU allocations should generally have explicit lifetime.

Example:

```csharp
await using CpuAllocation allocation =
    await Cpu.AcquireAsync(requirements);
```

Disposal releases the associated reservation.

This fits naturally with .NET resource management.

---

## 25. Reservation Is Not Permanent Ownership

A reservation grants capacity according to a contract.

It does not automatically mean no other code ever executes on these cores unless exclusivity was explicitly requested and granted.

---

## 26. Shared CPU Allocation

Typical applications receive shared compute.

The global scheduler remains free to move work within the granted contract.

---

## 27. Exclusive CPU Allocation

Specialized workloads may request exclusive physical cores.

Potential use cases:

```text
real-time audio
data acquisition
VR
game simulation
low-latency systems
industrial control
```

---

## 28. Exclusive Means Explicitly Granted

An application requesting exclusivity must not assume it received it.

It may fall back, continue best-effort, abort, or ask the user.

---

## 29. SMT Policy

SMT siblings may share execution resources.

Applications may request policies such as:

```text
Allow
PreferSeparatePhysicalCores
RequireSeparatePhysicalCores
UseSiblingThreads
```

A compute-heavy solver may prefer one hardware thread per physical core.

---

## 30. Core Classes

WitOS should support heterogeneous CPU cores.

Possible classifications:

```text
Performance
Balanced
Efficiency
RealTimeOptimized
Unknown
```

These are semantic descriptions.

They must not assume one vendor architecture.

---

## 31. P/E Core Example

An application may request performance cores for latency-sensitive work and efficiency cores for background work.

The same application works on CPUs without heterogeneous cores.

---

## 32. NUMA

NUMA topology must be first-class for systems where it matters.

A CPU allocation may optionally include memory locality.

Example:

```text
CPU Partition:
    NUMA node 1
    16 cores

Memory Allocation:
    NUMA-local
    32 GB
```

---

## 33. Compute and Memory Should Be Negotiated Together

For NUMA-sensitive workloads, CPU and memory cannot be treated independently.

A future API may allow a compute request to include both CPU cores and local-memory requirements.

---

## 34. Local Memory

An allocation may provide a memory resource.

This memory may be:

```text
NUMA-local
pinned
large-page backed
shared
device-visible
```

depending on requirements and guarantees.

---

## 35. Standard GC Memory Remains Standard

Ordinary managed allocations:

```csharp
new byte[1024];
new MyObject();
```

must retain standard .NET semantics.

WitOS must not redefine normal GC allocation behavior.

Advanced locality-aware memory uses additional APIs.

---

## 36. Specialized Memory APIs Are Additive

A future `OutWit.OS.Memory` package may provide:

```text
NUMA-local buffers
pinned memory
shared memory
DMA-safe memory
large-page allocations
```

without changing normal .NET allocation.

---

## 37. Cache Locality

Advanced workloads may express cache constraints.

Examples:

```text
same last-level cache
different cache domains
avoid cache contention
co-locate communicating workers
```

Such requests are advisory unless explicitly guaranteed.

---

## 38. Affinity

Affinity remains supported as a low-level concept.

Possible forms:

```text
preferred affinity
allowed CPU set
hard affinity
exclusive affinity
```

WitOS distinguishes them explicitly.

---

## 39. Placement vs Affinity

Affinity answers:

> Where may this work execute?

Placement additionally answers:

> Where should related work and memory be located for performance?

WitOS APIs should prefer placement semantics for high-level usage.

---

## 40. Execution Group

An `ExecutionGroup` represents related work that should share an execution policy.

A group may contain:

```text
threads
workers
tasks submitted through WitOS APIs
application components
```

---

## 41. Execution Groups Are Not .NET Task Groups

An execution group does not redefine `Task`.

It is an operating-system compute construct.

Submitted work may still return standard `Task`, `Task<T>`, `ValueTask`, or `ValueTask<T>`.

---

## 42. Standard Task Integration

Example:

```csharp
Task<Result> task =
    group.RunAsync(() => Solve(input));

Result result = await task;
```

To consumers, `task` remains a normal .NET Task.

---

## 43. No Task Semantic Changes

WitOS must not attach hidden non-standard semantics to ordinary tasks.

`Task.Run(...)` must remain standard .NET behavior.

---

## 44. Explicit Submission

Advanced placement should preferably use explicit APIs such as:

```csharp
await group.RunAsync(...);
```

rather than ambient magic.

This makes execution intent visible.

---

## 45. Ambient Execution Context Should Be Used Carefully

An ambient execution context can interact unpredictably with `Task.Run`, `ThreadPool`, `ConfigureAwait(false)`, and library internals.

Therefore strong placement guarantees should normally require explicit submission.

---

## 46. Dedicated Worker

A partition may expose dedicated worker abstractions.

This may be preferable to pretending that all advanced behavior fits into `System.Threading.Thread`.

---

## 47. Standard Thread Remains Available

Applications remain free to use:

```csharp
new Thread(...)
```

with normal semantics.

Advanced execution is optional.

---

## 48. Standard Thread Affinity Compatibility

Where practical, WitOS may support standard .NET or native affinity APIs expected by existing applications.

These map onto the normal scheduler.

They do not replace the richer WitOS execution model.

---

## 49. Custom TaskScheduler

`OutWit.OS.Execution` may optionally expose integration with standard .NET `TaskScheduler`.

Example:

```csharp
TaskScheduler scheduler =
    allocation.CreateTaskScheduler();
```

This enables standard task-based code to target an execution partition.

---

## 50. TaskScheduler Is an Adapter

The WitOS kernel does not understand `TaskScheduler`.

It understands execution resources and native execution contexts.

`TaskScheduler` is a .NET-level adapter.

---

## 51. Hierarchical Scheduling

WitOS should support hierarchical scheduling.

Conceptually:

```text
System Scheduler
      │
      ├── Application A allocation
      │       ↓
      │   CoreCLR ThreadPool
      │
      ├── Application B allocation
      │       ↓
      │   custom worker pool
      │
      └── System Services
```

The OS allocates capacity among applications.

Runtimes schedule finer-grained work internally.

---

## 52. Kernel Does Not Schedule Managed Tasks

The kernel schedules native execution contexts.

It does not need to know about:

```text
Task
async/await
continuations
managed delegates
```

This maintains runtime independence.

---

## 53. Runtime Cooperation

Although WitOS does not modify .NET semantics, runtime cooperation may improve efficiency.

CoreCLR may observe the effective processor set available to a process or execution context using standard platform mechanisms.

Such integration should use upstream-supported runtime concepts where possible.

---

## 54. No Mandatory CoreCLR Fork

Advanced execution features must not depend on maintaining a custom CoreCLR scheduler fork.

This is a major compatibility invariant.

---

## 55. Default Scheduler

The default WitOS scheduler handles ordinary workloads automatically.

It considers factors such as:

```text
load
priority
latency requirements
core topology
power state
thermal state
NUMA
foreground/background status
```

Applications need not provide hints.

---

## 56. Scheduler Policy Is Replaceable Internally

The scheduling algorithm may evolve.

Application APIs should describe contracts and intent rather than depend on a specific algorithm.

---

## 57. Interactive Work

Interactive applications may declare or receive `Interactive` execution intent.

The scheduler may prioritize latency over throughput.

---

## 58. Background Work

Background workloads may receive lower CPU priority, efficiency cores, reduced frequency preference, or throttling under battery pressure without requiring platform-specific application logic.

---

## 59. Compute-Intensive Work

A compute-heavy operation may request high sustained throughput, many cores, NUMA-local memory, and reduced migration.

---

## 60. Latency-Sensitive Work

Examples include:

```text
audio processing
VR
interactive simulation
input processing
industrial acquisition
```

Possible requirements:

```text
dedicated core
reduced migration
predictable scheduling
bounded interference
```

Hard guarantees require explicit support.

---

## 61. Real-Time Candidate

WitOS may expose a real-time candidate mode.

This does not automatically imply hard real-time guarantees.

The allocation result must explicitly state what guarantees exist.

---

## 62. Hard Real-Time

True hard real-time behavior may require:

```text
dedicated cores
bounded kernel paths
controlled interrupts
fixed memory
no paging
special GC constraints
```

Standard managed execution may not satisfy such requirements.

WitOS must not falsely label best-effort execution as hard real-time.

---

## 63. CPU Reservations

Applications may request reservations expressed as:

```text
cores
CPU percentage
time budget
deadline budget
exclusive intervals
```

The exact models may vary.

---

## 64. Time-Based Reservation

A future request may reserve a CPU-time budget within a periodic interval.

Such guarantees depend on scheduler capability.

---

## 65. Quotas

The system may impose CPU quotas.

Quotas are security/resource-management mechanisms as well as scheduling tools.

---

## 66. Compute Capability Security

Ordinary applications receive shared CPU authority.

Advanced capabilities such as exclusive cores, real-time scheduling, topology control, or system-wide CPU isolation require explicit permission.

---

## 67. OutWit.OS.Execution Does Not Grant Authority

Referencing:

```text
OutWit.OS.Execution
```

does not grant additional execution rights.

The application must successfully acquire an appropriate compute capability.

---

## 68. Exact Core Reservation

Advanced APIs may allow exact topology reservation.

This is a privileged expert-level operation.

---

## 69. Exact Placement Is Not Portable

Applications using exact topology may naturally become tuned to particular machines.

This is acceptable.

The architecture distinguishes portable semantic usage from hardware-specific optimization.

---

## 70. Graceful Degradation

A well-designed advanced application should often support:

```text
exact optimized mode
        ↓
semantic allocation mode
        ↓
generic .NET fallback
```

Example:

```text
WitOS workstation:
    exact NUMA-aware allocation

Windows:
    affinity-based approximation

generic host:
    worker pool
```

---

## 71. Workstation Example

Engineering solver requests:

```text
16 physical cores
32 GB memory
same NUMA node preferred
avoid SMT siblings
```

WitOS may grant all of these explicitly.

---

## 72. Windows Fallback Example

The same application on Windows may receive:

```text
parallelism = 16
processor affinity applied
NUMA preference best-effort
exclusive cores unavailable
```

The same code runs with weaker guarantees.

---

## 73. Generic Fallback Example

On a generic .NET host:

```text
parallelism = 16
implemented through worker pool
topology control unavailable
```

The solver still functions.

---

## 74. Mobile Example

A photo-processing application may request preferred parallelism and performance cores.

A phone may grant a mixed performance/efficiency allocation or choose a more energy-efficient configuration.

The application need not know the phone model.

---

## 75. Energy-Aware Scheduling

Compute allocations may include power intent.

The scheduler may prefer low-power cores or lower frequencies for background work.

---

## 76. Thermal Awareness

On thermally constrained devices, the scheduler may reduce sustained compute capacity.

Applications may observe allocation changes when relevant.

---

## 77. Dynamic Allocation

A CPU allocation may be elastic.

Example:

```text
Minimum = 4 cores
Preferred = 16 cores
```

The system may grant different amounts over time if the contract allows dynamic resizing.

---

## 78. Fixed Allocation

Some workloads may require stable capacity.

This requires stronger reservation and may be denied.

---

## 79. Allocation Change Notification

Elastic allocations should expose changes asynchronously.

The exact API remains open.

---

## 80. Preemption

Reserved compute resources may or may not be preemptible.

Possible contracts:

```text
BestEffort
Preemptible
NonPreemptible
EmergencyPreemptible
```

Strong non-preemptibility is privileged and platform-dependent.

---

## 81. System Safety Overrides

Even exclusive applications must not be able to prevent critical system behavior such as:

```text
thermal protection
hardware failure response
secure attention
emergency scheduling
```

No application has absolute physical ownership of the CPU.

---

## 82. Shell and CPU Resources

The graphical shell receives ordinary compute resources like any other application/service.

It does not have special permanent ownership of CPU capacity.

---

## 83. Workstation Profile

A workstation execution profile may coordinate:

```text
shell throttling
background-service throttling
CPU reservations
GPU reservations
memory reservation
storage bandwidth
```

This allows engineering and media workloads to obtain predictable capacity.

---

## 84. Gaming Profile

A gaming profile may request:

```text
latency-sensitive CPU allocation
prefer performance cores
reduce background CPU load
reserve GPU capacity
low-latency input
exclusive presentation
```

This is coordinated resource policy rather than a special hard-coded game mode.

---

## 85. Profiles Are Not Hidden Magic

Applications should be able to observe what was actually granted.

---

## 86. Compute Resources Beyond CPU

The same general resource model should extend to:

```text
GPU
NPU
DSP
FPGA
remote CPU
remote accelerator
```

CPU-specific details should not contaminate the higher-level generic compute model.

---

## 87. Generic Compute Request

A future generic compute request may describe parallelism, memory, accelerator preference, locality, and trust requirements.

Possible providers include CPU, GPU, NPU, or remote node depending on workload compatibility.

---

## 88. Specialized Compute Capabilities

Generic compute interfaces cannot express every accelerator.

Resources may expose specific capabilities:

```text
ICpuCompute
IGpuCompute
ITensorCompute
IVideoEncoder
```

Applications request the most specific interface they understand.

---

## 89. Remote Compute

Compute resources may be remote.

The application may use the same resource acquisition model.

---

## 90. Remote Compute Is Not Transparent Local Execution

Remote execution has:

```text
latency
serialization cost
network failure
security boundaries
data movement
```

The API must expose these characteristics where relevant.

---

## 91. Compute Locality

Requirements may include:

```text
RequireLocal
PreferLocal
SameDeviceAs(resource)
SameNumaNodeAs(resource)
SameTrustDomain
```

This allows applications to express meaningful physical constraints.

---

## 92. Co-Location

Resource resolution may place computation where data already exists rather than moving large datasets.

---

## 93. Anti-Affinity

Applications may request separation.

Examples:

```text
Audio processing
    anti-affinity with
background encoding

Redundant workers
    different physical cores
    different NUMA nodes
```

This is more expressive than a flat affinity mask.

---

## 94. Group Affinity

Related execution groups may request same NUMA node, same cache domain, or different physical cores.

The scheduler attempts to satisfy the topology relationship.

---

## 95. Compute Graph

Complex applications may describe execution topology.

Example:

```text
UI
 │
 └── interactive CPU

Solver
 │
 └── 16-core compute partition

Renderer
 │
 └── GPU

Indexer
 │
 └── efficiency CPU

Exporter
 │
 └── hardware encoder
```

All belong to one application.

---

## 96. Component-Level Scheduling

Application components may hold independent compute capabilities.

This supports UI, solver, plugin, renderer, and background worker components with separate resource policies.

---

## 97. Plugin Scheduling

Plugins must not automatically inherit the host's advanced CPU reservations.

A host may delegate a narrower compute allocation to a plugin.

---

## 98. Service Scheduling

System services also execute under resource contracts.

Example:

```text
Indexer:
    background
    energy-efficient

Audio:
    latency-sensitive

Update Service:
    maintenance
```

---

## 99. CPU Time as Authority

Compute access itself is a capability.

A sandbox may be restricted by maximum parallelism, CPU quota, or maximum reservation duration.

This helps prevent denial-of-service.

---

## 100. Reservation Duration

Some advanced reservations may be time-limited.

The application can renew if policy allows.

---

## 101. Fairness

The global scheduler must balance:

```text
application requests
user expectations
system responsiveness
security
battery
thermal constraints
fairness
```

Applications do not directly control global policy.

---

## 102. Priority Inversion

WitOS scheduler design must account for priority inversion involving locks, IPC, resource providers, and driver dependencies.

Possible strategies include priority inheritance or priority donation.

---

## 103. IPC and Scheduling

When a high-priority task synchronously depends on a service, the service may require temporary priority adjustment.

This will be specified alongside IPC semantics.

---

## 104. Blocking

Advanced execution groups should avoid unnecessary blocking.

However, standard .NET blocking APIs remain supported.

The scheduler may distinguish running, blocked, waiting, sleeping, and I/O wait for resource accounting.

---

## 105. Async Work

Async operations are naturally compatible with hierarchical scheduling.

A thread may release CPU while awaiting storage, network, timer, device, or remote resource operations.

---

## 106. Continuations

Standard .NET continuation scheduling remains controlled by .NET semantics.

WitOS-specific execution APIs may provide explicit execution-group submission for continuations that require placement guarantees.

---

## 107. SynchronizationContext

WitOS should support standard `SynchronizationContext` behavior.

GUI frameworks may use their own synchronization context.

Advanced CPU allocation must not break this.

---

## 108. UI Thread

A graphical framework may choose a dedicated UI thread.

WitOS does not require a universal UI-thread model.

Presentation frameworks define their own threading rules.

---

## 109. Low-Latency Input Thread

A game or VR application may request a specialized low-latency execution allocation for input processing.

This remains separate from standard UI-thread semantics.

---

## 110. GC Interaction

GC behavior can affect compute-sensitive applications.

WitOS must preserve standard CoreCLR GC behavior.

Advanced applications may select existing .NET GC modes using supported runtime mechanisms.

---

## 111. No Custom WitOS GC Semantics

WitOS-specific compute allocation must not require modifying object lifetime or garbage-collection semantics.

Any future integration with CoreCLR GC must remain upstream-compatible.

---

## 112. GC CPU Usage

The system may treat runtime GC threads as part of an application's compute consumption.

Exact scheduling cooperation depends on upstream .NET behavior.

---

## 113. NativeAOT

NativeAOT applications use the same WitOS execution APIs.

The execution resource model is independent of JIT availability.

---

## 114. Other Managed Runtimes

Although .NET is the primary WitOS platform, the kernel execution model should not require .NET-specific concepts.

Other runtimes may map their own schedulers onto WitOS compute resources.

---

## 115. Language-Neutral Kernel ABI

The kernel may expose concepts equivalent to:

```text
CreateExecutionContext
CreateExecutionGroup
SetAllowedCpuSet
SetSchedulingClass
ReserveCpuCapacity
QueryTopology
BindMemoryDomain
```

Exact ABI is deferred.

---

## 116. Managed Execution API

`OutWit.OS.Execution` provides a .NET-friendly representation over these primitives.

The package should feel like normal .NET:

```text
async
ValueTask
IAsyncDisposable
CancellationToken
strongly typed requirements
```

---

## 117. Backend Interface

A cross-platform implementation may internally define platform providers such as:

```text
WitOSExecutionBackend
WindowsExecutionBackend
LinuxExecutionBackend
MacExecutionBackend
GenericExecutionBackend
```

The public API remains backend-neutral.

---

## 118. Backend Feature Reporting

Each backend must report actual capabilities.

Example:

```text
CPU topology          Supported
Affinity              Supported
Exclusive cores       Unsupported
NUMA locality         BestEffort
Core class selection  Supported
```

No backend may silently claim guarantees it cannot enforce.

---

## 119. Conformance Tests

`OutWit.OS.Execution` should define backend conformance tests.

Example categories:

```text
basic execution
parallelism
cancellation
lifetime
affinity
topology reporting
reservation guarantees
fallback behavior
```

This follows the existing OutWit provider model.

---

## 120. Stable Contract, Replaceable Provider

The execution API follows a core OutWit principle:

> **The contract is stable; the provider is replaceable.**

A program depends on `OutWit.OS.Execution`, not directly on Win32, Linux scheduling APIs, or the WitOS kernel ABI.

---

## 121. Example: Portable Solver

```csharp
var request = new CpuRequirements
{
    MinimumCores = 4,
    PreferredCores = 16,
    PreferPhysicalCores = true,
    PreferSameNumaNode = true
};

await using var cpu =
    await Cpu.AcquireAsync(request);

var result =
    await cpu.RunAsync(() => Solve(model));
```

This application remains portable.

---

## 122. Example: Check Guarantees

```csharp
if (cpu.Guarantees.Has(
        CpuGuarantee.SameNumaNode))
{
    UseNumaOptimizedSolver();
}
else
{
    UseGenericSolver();
}
```

The application checks functionality, not OS identity.

---

## 123. Example: Exact Hardware Tuning

An expert application may query physical topology and request exact cores.

This intentionally reduces portability.

---

## 124. Example: Background Work

Background work may request a small efficient allocation.

On a heterogeneous mobile CPU, efficiency cores may be preferred.

---

## 125. Example: Low-Latency Audio

Requirements may include:

```text
1 physical core
exclusive preferred
no SMT sibling preferred
low-latency scheduling
```

If exclusivity is unavailable, the application receives weaker guarantees and may increase buffering.

---

## 126. Example: Video Export

Editing mode may use interactive CPU and shared GPU.

Export may temporarily request more CPU, hardware encoder, and GPU compute.

The system allocates additional resources for the duration of export.

---

## 127. Example: Engineering Workstation

```text
UI
    normal interactive compute

Solver
    24 cores
    NUMA-local memory

Renderer
    GPU

Indexer
    efficiency cores

Shell
    reduced workload
```

The machine behaves as a coordinated resource system rather than independent processes competing blindly.

---

## 128. Example: Same Application on Windows

The engineering application uses the same code.

Windows backend approximates advanced guarantees using available affinity, priority, NUMA, and worker-pool mechanisms.

The application remains functional.

---

## 129. Example: Same Application on Generic .NET

Generic backend provides worker-pool and parallelism limits using standard threading.

All topology-specific guarantees report unsupported.

The application still computes correctly.

---

## 130. Scheduling and Application Lifecycle

Suspension may release advanced compute reservations.

Applications must not assume physical CPU allocation persists across suspension.

---

## 131. Persistent Intent vs Transient Allocation

Application state may persist a preference such as "solver prefers 16 cores", but the actual reservation is transient.

Upon restore, a new allocation is negotiated.

---

## 132. Handoff

During device handoff, a large workstation allocation may become a smaller local allocation on the destination device.

The application may continue with different compute capacity.

---

## 133. Remote Continuation

Alternatively, presentation may move while compute remains on the original device.

The compute resource becomes remote.

This is naturally supported by the broader resource model.

---

## 134. Scheduling Policy and Shell Profiles

Presentation profiles may influence scheduling policy.

Example:

```text
Workstation Profile:
    reserve compute for foreground engineering app

Gaming Profile:
    reduce background CPU interference

LowPower Profile:
    prefer efficiency cores
```

These are policy layers over the same execution model.

---

## 135. User Control

Users may configure policies such as:

```text
Allow applications to reserve exclusive cores
Limit background CPU
Prefer battery life
Prefer maximum workstation performance
```

Applications state requirements; users/system define policy.

---

## 136. Administrative Control

Organizations may restrict real-time scheduling, exclusive core allocation, remote compute, or high-power execution through capability policy.

---

## 137. Observability

Applications should be able to observe relevant execution information.

Examples:

```text
allocated parallelism
granted topology guarantees
CPU time consumed
throttling
allocation changes
```

---

## 138. Profiling

Performance tools may receive additional capabilities to inspect scheduler activity, CPU migration, cache locality, NUMA traffic, and allocation utilization.

---

## 139. Diagnostics

WitOS-aware diagnostics may report requested vs granted resources and topology guarantees.

This makes resource contracts debuggable.

---

## 140. Scheduling Failure

Resource acquisition may fail because of insufficient cores, policy denial, thermal constraints, unavailable exclusive resources, or impossible NUMA requirements.

Errors should communicate the reason where safe.

---

## 141. Cancellation

Acquisition and execution APIs should support `CancellationToken`.

This follows standard .NET conventions.

---

## 142. Async Disposal

Reservations may require asynchronous release.

Therefore `IAsyncDisposable` is appropriate for advanced resource allocations.

---

## 143. No Hidden Permanent Reservations

Advanced allocations must not survive accidental object loss indefinitely.

The system should use leases, liveness, process cleanup, and timeouts where appropriate.

---

## 144. Crash Cleanup

If an application crashes, CPU reservations, exclusive core assignments, and scheduler quotas must be reclaimed automatically.

---

## 145. Kernel Mechanisms

The native kernel should support mechanisms such as:

```text
execution contexts
processor sets
execution groups
scheduling classes
CPU reservations
topology discovery
memory affinity
timers
preemption
```

It should not understand high-level application semantics.

---

## 146. Policy Above Kernel

Terms such as Gaming, Workstation, and Background are high-level policy.

The kernel may receive translated constraints such as priority class, allowed cores, reservation, deadline, or quota.

---

## 147. Scheduling Classes

Possible low-level scheduling classes may include:

```text
Normal
Background
LatencySensitive
Reserved
RealTime
```

Exact semantics remain open.

---

## 148. Application APIs Should Avoid Kernel Vocabulary

Ordinary developers should not need to understand kernel scheduler internals.

Prefer semantic usage descriptions over numeric scheduler-class parameters unless using explicit low-level APIs.

---

## 149. High-Level and Low-Level APIs

The managed API may expose:

```text
OutWit.OS.Execution
```

for semantic scheduling.

A lower-level package may eventually expose:

```text
OutWit.OS.Execution.LowLevel
```

for topology-sensitive infrastructure.

---

## 150. Compatibility Invariants

1. Standard .NET threading semantics remain unchanged.
2. `Task`, `Thread`, `ThreadPool`, and `Parallel` do not depend on WitOS-specific assemblies.
3. Advanced scheduling is additive.
4. `OutWit.OS.Execution` should remain cross-platform where practical.
5. Unsupported guarantees degrade explicitly, never silently.
6. Applications should prefer execution intent over exact core IDs.
7. Exact topology control remains available for expert use.
8. The kernel schedules execution contexts, not managed Tasks.
9. CoreCLR must remain upstream-compatible.
10. Compute allocations are capabilities and do not imply unlimited authority.
11. CPU topology is a resource property, not part of application identity.
12. NUMA and heterogeneous cores are first-class concepts.
13. Local and remote compute belong to the same broader resource model.
14. A program using WitOS execution APIs should remain functional through fallback unless it explicitly requires unavailable guarantees.

---

## 151. Deferred Questions

The following require future design work:

### Kernel Scheduler

```text
algorithm
preemption
fairness
deadline support
priority inheritance
```

### CPU Reservation Protocol

```text
hard vs soft guarantees
time budgets
quota enforcement
preemption rules
```

### NUMA Memory API

```text
managed interaction
local buffers
large pages
migration
```

### CoreCLR Integration

```text
effective processor count
ThreadPool behavior
GC worker behavior
upstream integration
```

### Remote Compute

```text
code deployment
serialization
security
data locality
failure recovery
```

### Real-Time Execution

```text
hard guarantees
interrupt isolation
managed-runtime constraints
```

---

## 152. Relationship to Future RFCs

This RFC interacts strongly with:

```text
RFC 0006 — IPC & Local/Remote Communication
    execution dependency and priority propagation

RFC 0007 — Universal Hardware Interface
    topology discovery and CPU control

RFC 0008 — Storage & Persistent State
    data locality

RFC 0009 — Presentation, Shell & Input
    interactive scheduling and exclusive presentation

RFC 0010 — Application Packaging
    optional OutWit.OS.Execution dependencies
```

A dedicated Compute & Distribution RFC may later extend this model to GPU, NPU, remote nodes, clusters, and OmnibusCloud.

---

## 153. Summary

WitOS treats CPU capacity as an explicit, structured compute resource rather than an invisible flat pool of numbered processors.

Applications may choose between three levels of control:

```text
standard .NET
    ↓
semantic WitOS execution API
    ↓
explicit topology control
```

Ordinary .NET code continues to use:

```text
Thread
Task
ThreadPool
Parallel
```

without modification.

Advanced applications may optionally request:

```text
core count
physical cores
NUMA locality
SMT policy
core class
exclusive capacity
memory locality
scheduling intent
```

through `OutWit.OS.Execution`.

The same API should degrade gracefully on other operating systems through replaceable providers and standard .NET fallbacks.

The defining principle is:

> **Standard .NET defines how code executes by default. WitOS adds a portable way to describe where, with what resources, and under what guarantees execution should occur.**
