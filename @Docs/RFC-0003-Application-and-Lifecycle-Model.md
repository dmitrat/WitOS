# WitOS
## RFC 0003 — Application & Lifecycle Model
### Draft v0.1

## 1. Status

Draft.

This document defines the application, execution, session, presentation, and lifecycle model of WitOS.

It builds upon the Resource & Capability Model defined in RFC 0002.

The key principle is:

> **An application is a logical entity. A process, window, device, or presentation surface is only one possible temporary manifestation of that application.**

---

## 2. Motivation

Traditional operating systems commonly bind several concepts too tightly:

```text
application
    ≈
process
    ≈
executable
    ≈
window
    ≈
one machine
```

This model works reasonably well for traditional desktop software but becomes increasingly awkward for:

- mobile devices;
- suspend/resume;
- multi-device workflows;
- distributed execution;
- background services;
- headless applications;
- remote sessions;
- application migration;
- cloud execution;
- adaptive UI;
- replaceable desktop environments.

WitOS explicitly separates these concepts.

---

## 3. Fundamental Entities

The application model defines several distinct entities:

```text
Application
Application Instance
Execution Context
Address Space
Application State
Session
Presentation Environment
Presentation Endpoint
Shell
Resource Context
```

They are related but must not be treated as synonyms.

---

## 4. Application

An **Application** is a persistent logical software entity.

It is identified by an `ApplicationId`.

An application identity is independent of:

```text
process ID
machine
CPU architecture
current user interface
current shell
address space
current execution state
```

The application may currently be:

```text
running
suspended
not loaded
executing remotely
open on multiple devices
```

while retaining the same logical identity.

---

## 5. Application Package

An application is distributed as an application package.

A package may contain:

```text
managed assemblies
resources
metadata
UI resources
localization
manifest
capability declarations
optional native components
optional platform-independent assets
```

A package should not normally contain separate versions for:

```text
desktop
phone
tablet
server
```

Device differences are handled through capabilities and adaptive presentation.

---

## 6. Application Manifest

The application manifest describes requirements and intentions.

Example:

```text
Application:
    PhotoEditor

Requires:
    .NET >= N
    memory >= 512 MB

Optional:
    graphical presentation
    touch
    camera
    GPU compute

Background:
    allowed = false
```

The manifest describes needs.

It does not directly grant authority.

Capabilities are still acquired through the resource/security system.

---

## 7. Portable Application Identity

The same application package should retain the same application identity across:

```text
x64 desktop
ARM64 phone
ARM64 tablet
server
VM
future RISC-V device
```

Architecture must not become part of application identity.

---

## 8. Application Is Not a Process

A process is an execution mechanism.

An application is a logical entity.

One application may have:

```text
zero processes
one process
multiple processes
processes on multiple machines
```

For example:

```text
IDE Application
    ├── UI execution context
    ├── compiler execution context
    ├── language server
    └── remote build worker
```

All may belong to the same logical application.

---

## 9. Application Without a Process

A suspended application may exist without any active process.

Example:

```text
Application Identity
        │
Application State
        │
Persistent Capabilities
        │
(no active execution)
```

The application remains installed, known to the system, and resumable.

This is a normal state.

---

## 10. Application Instance

An `ApplicationInstance` represents one logical activation of an application.

Some applications may prefer a single logical instance.

Others may support many.

This is application policy rather than a kernel assumption.

---

## 11. Execution Context

An `ExecutionContext` is an actively scheduled execution environment.

It may correspond to:

```text
managed runtime context
OS process
isolated service
remote worker
sandbox
```

depending on implementation and trust requirements.

An application may create multiple execution contexts.

---

## 12. Address Space Is an Implementation Detail

Execution contexts may:

```text
share an address space
use separate address spaces
run remotely
run inside a sandbox
```

The application model does not require every component to map one-to-one to a hardware MMU address space.

---

## 13. Application Context

Each active application instance receives an `ApplicationContext`.

Conceptually:

```csharp
public interface IApplicationContext
{
    ApplicationId ApplicationId { get; }

    InstanceId InstanceId { get; }

    IResourceResolver Resources { get; }

    IApplicationLifecycle Lifecycle { get; }

    ISessionContext? Session { get; }

    IPresentationContext? Presentation { get; }
}
```

Some members may be absent.

For example, a headless service may have no presentation context.

---

## 14. GUI Is Not Mandatory

WitOS has no mandatory GUI.

A fully functional WitOS installation may consist of:

```text
kernel
.NET runtime
storage
networking
console
services
applications
```

with no graphical presentation stack installed.

This is not a special "server edition".

It is the same operating system with a different resource set.

---

## 15. Presentation Is Optional

An application may have:

```text
zero presentation endpoints
one presentation endpoint
multiple presentation endpoints
```

Presentation is not part of application identity.

Examples:

```text
Web Server
    no presentation

Compiler
    terminal presentation

IDE
    graphical presentation
    terminal endpoint
    remote endpoint

Media Player
    graphical UI
    lock-screen controls
```

---

## 16. Presentation Resource

The system exposes presentation through resources.

Possible interfaces include:

```text
ITextTerminal
IWindowingEnvironment
IDisplaySurface
IInteractiveSurface
IAudioOutput
INotificationEndpoint
```

These are resources governed by capabilities.

The OS itself does not assume that a display exists.

---

## 17. Console Applications

Standard .NET console applications remain first-class applications.

For example:

```csharp
Console.WriteLine("Hello");
```

may map to an available:

```text
ITextTerminal
```

The terminal may be:

```text
physical console
remote terminal
SSH session
serial interface
graphical terminal emulator
virtual console
```

Standard .NET behavior remains available.

---

## 18. Graphical Applications

A graphical application acquires graphical presentation capabilities.

Example:

```csharp
IWindowingEnvironment windowing =
    await context.Resources.AcquireAsync<IWindowingEnvironment>();
```

The application does not need to know whether the environment represents:

```text
desktop windows
phone fullscreen surfaces
tablet split-screen
remote desktop
kiosk display
```

---

## 19. Shell

A **Shell** is a replaceable application implementing session-level presentation policy.

The shell is not the operating system.

It is not hardcoded into the kernel.

It is not identified by executable name.

Conceptually:

```text
Shell =
application granted session-management
and presentation-policy capabilities
```

---

## 20. Shell Capabilities

A shell may receive capabilities such as:

```text
IApplicationLauncher
IApplicationEnumerator
IWindowManager
IFocusManager
INotificationHost
IWorkspaceManager
ISessionController
IPresentationPolicy
```

A shell receives these because policy grants them.

It does not receive special hidden privileges merely because it is the default shell.

---

## 21. Replaceable Shells

Users or administrators may install alternative shells.

Examples:

```text
DesktopShell
MinimalShell
TouchShell
WorkstationShell
GamingShell
KioskShell
AccessibilityShell
RemoteShell
CommunityShell
```

Applications must not require a particular shell implementation.

---

## 22. Shell Independence

Normal applications must not perform checks such as:

```csharp
if (Shell.Name == "DesktopShell")
{
    ...
}
```

Applications depend on presentation capabilities, not shell identity.

---

## 23. Shell vs Compositor

The shell and compositor are different concepts.

```text
Application
     ↓
Windowing API
     ↓
Compositor
     ↓
Display Resource
```

Separately:

```text
Shell
  ↓
window placement
workspaces
launcher
task switching
notifications
session policy
```

A shell may use a compositor without implementing the compositor itself.

---

## 24. Replaceable Compositor

The compositor may itself be replaceable through a stable system contract.

Possible implementations:

```text
StandardCompositor
LowPowerCompositor
GamingCompositor
RemoteCompositor
ExperimentalCompositor
```

Replacing a compositor is more privileged than replacing a shell but should remain architecturally possible.

---

## 25. Presentation Environment

A `PresentationEnvironment` is the presentation capability set available to an application.

Examples:

```text
Desktop Windowing
Phone Fullscreen
Kiosk
Terminal
Remote Session
VR Environment
No Presentation
```

An application may move between presentation environments during its lifetime.

---

## 26. Presentation Endpoint

A `PresentationEndpoint` is one active user-facing representation of an application.

Examples:

```text
window
fullscreen surface
terminal session
remote UI session
notification control surface
VR panel
```

One application may own multiple endpoints.

---

## 27. Multiple Presentation Endpoints

Example:

```text
Music Player
    ├── Main Window
    ├── Notification Controls
    ├── Phone Lock Screen Controls
    └── Remote Control Endpoint
```

These are views of one application.

They are not separate applications.

---

## 28. Presentation Adaptation

Presentation should adapt according to available resources.

Applications may observe:

```text
display size
DPI
orientation
input capabilities
touch
keyboard
pointer
pen
multiple displays
presentation policy
```

and adapt accordingly.

Device category must not be required.

---

## 29. Adaptive UI Frameworks

Frameworks such as Avalonia may provide high-level adaptive UI.

WitOS should expose one presentation backend rather than separate OS-specific backends.

Conceptually:

```text
Avalonia Application
        ↓
Avalonia.WitOS
        ↓
Universal Presentation API
        ↓
Presentation Environment
```

---

## 30. Window Is Not an Application

A window is a presentation resource.

Applications may:

```text
have no windows
have one window
have many windows
destroy all windows and remain alive
```

Application lifecycle must therefore not be defined purely through window lifecycle.

---

## 31. Closing the Last Window

Closing the last graphical window may result in several valid behaviors:

```text
terminate application
suspend application
continue in background
remain available without UI
```

The application declares or negotiates this policy.

---

## 32. Application Lifecycle

A logical application may transition through states such as:

```text
Installed
   ↓
Activating
   ↓
Active
   ↓
Background
   ↓
Suspended
   ↓
Persisted
   ↓
Restoring
   ↓
Active
```

Termination is separate from suspension.

---

## 33. Proposed Lifecycle States

Core lifecycle states:

```text
Inactive
Activating
Active
Background
Suspending
Suspended
Restoring
Terminating
Failed
```

Applications should not depend on unnecessary internal state detail.

---

## 34. Inactive

The application exists but has no active execution context.

Persistent application state remains available.

No CPU resources are consumed.

---

## 35. Activating

The application is acquiring execution resources and restoring required state.

Activities may include:

```text
create execution context
restore logical state
reacquire resources
create presentation endpoints
```

---

## 36. Active

The application is actively executing and is eligible for interactive resource priority.

An active application does not necessarily have GUI.

---

## 37. Background

The application is executing but does not currently own primary user attention.

Its resource priority may be reduced.

Background execution is capability- and policy-controlled.

---

## 38. Suspended

A suspended application has no normal execution activity.

It may retain:

```text
persistent state
logical identity
persistent grants
resource metadata
```

Transient resources may have been released.

---

## 39. Persisted

The system may persist sufficient application state to reconstruct the application later.

This is distinct from keeping process memory resident.

---

## 40. Restoring

The application is reconstructing active state from:

```text
persistent logical state
optional execution snapshot
current resource environment
```

Resource availability may differ from the previous execution environment.

---

## 41. Termination

Termination ends a logical activation.

It does not uninstall the application or erase persistent state unless explicitly requested.

---

## 42. Failure

An execution context may fail without destroying the logical application.

Example:

```text
application worker crashes
        ↓
logical application survives
        ↓
worker restarted
```

Failure recovery policy may be application-specific.

---

## 43. Execution State vs Logical State

WitOS distinguishes execution state from logical application state.

Execution state includes:

```text
stacks
registers
heap internals
temporary caches
open transient handles
```

Logical state includes:

```text
open documents
workspace
user actions
application model
persistent configuration
```

Logical state should survive execution replacement.

---

## 44. Persistent Application State

Applications should use explicit persistent state for information that must survive:

```text
suspend
reboot
migration
process failure
device change
```

The system may provide a standard application state store.

---

## 45. Snapshotting

Execution snapshots may be supported as an optimization.

However:

> **Snapshotting must not be the only persistence model.**

Applications must remain capable of restoration when exact execution state cannot be resumed.

---

## 46. Why Snapshotting Cannot Be Fundamental

Hardware may change between suspend and restore.

For example:

```text
desktop GPU
    ↓ handoff
tablet GPU
```

A raw GPU handle cannot simply be restored.

The application therefore requires logical state plus resource reacquisition.

---

## 47. Resource Reacquisition

After restoration, transient resources may need to be reacquired.

The logical application remains unchanged even if display, GPU, camera, input, or network resources differ.

---

## 48. Persistent Capabilities

Some capabilities may survive logical suspension.

For example, a user-granted read/write permission to a logical document may be securely reissued.

Transient physical capabilities usually do not survive.

---

## 49. Physical Capabilities

Examples of normally non-persistent capabilities:

```text
GPU queue
display surface
DMA region
camera lease
audio stream
exclusive input
```

These belong to a particular execution environment.

---

## 50. Application Handoff

An application may move presentation and execution between devices.

Example:

```text
Desktop
   ↓
persist logical state
   ↓
transfer / synchronize state
   ↓
Tablet
   ↓
restore
   ↓
reacquire local presentation
```

The application identity remains the same.

---

## 51. Handoff Is Not File Synchronization

Traditional systems often simulate continuity by syncing files.

WitOS should model continuity directly:

```text
Application Identity
        +
Application State
        +
Persistent Capabilities
        +
Resource Rebinding
```

The transferred object is the application context, not merely a folder.

---

## 52. Handoff Modes

Possible handoff modes:

```text
Move
Mirror
Fork
Remote Control
Presentation Transfer
```

### Move

Primary execution moves to another device.

### Mirror

Multiple devices display or interact with one logical application state.

### Fork

A new independent state branch is created.

### Remote Control

Execution remains on one device while presentation moves.

### Presentation Transfer

Only UI moves while computation remains elsewhere.

---

## 53. Application Migration

Migration may involve:

```text
logical state only
managed execution state
selected execution contexts
resource bindings
```

The system should not require all applications to support transparent process migration.

Migration is a layered capability.

---

## 54. Distributed Application

An application may naturally span multiple machines.

Example:

```text
Engineering Application
    │
    ├── UI on workstation
    ├── solver on compute server
    ├── rendering on GPU node
    └── storage on NAS
```

This remains one logical application environment.

---

## 55. Locality Is Explicit

The application may constrain execution locality or allow remote execution.

Migration and distribution must not pretend latency does not exist.

---

## 56. Sessions

A **Session** represents a user or operational interaction environment.

A session may contain:

```text
applications
presentation environment
shell
resource policies
identity context
security policy
```

A session is not equivalent to a desktop.

---

## 57. Headless Session

A session may have no graphical environment.

A remote server session with a terminal shell and no compositor remains a normal session.

---

## 58. Graphical Session

A graphical session may contain:

```text
compositor
shell
window manager policy
notification host
applications
```

These are components layered over the session model.

---

## 59. Multiple Sessions

A device may support multiple simultaneous sessions.

Each receives separate capability/resource views.

---

## 60. Session Is Not User Identity

A user may have multiple sessions, sessions on several devices, or no current session.

User identity is a separate security concept.

---

## 61. System Applications

System-level services may use the same application model.

Examples:

```text
network manager
storage service
notification service
device manager
```

They may run without presentation.

---

## 62. Services

A service is an application or component whose primary function is resource provision rather than presentation.

Services should not require a special operating-system executable format.

---

## 63. Application Activation

Applications may be activated by:

```text
user request
resource event
URL
file/object association
scheduled task
IPC request
notification action
device event
remote request
system policy
```

Activation does not necessarily create graphical UI.

---

## 64. Activation Contract

Activation carries structured context.

Examples:

```text
Open Document
Handle URL
Background Task
Create New Instance
Resume
Remote Handoff
```

---

## 65. Open With

Opening a document in an application should pass a capability to that document rather than merely a path.

The application does not need authority over the containing folder.

---

## 66. Application Launching

Launching an application is a capability-controlled operation.

A shell normally possesses `IApplicationLauncher`, but other authorized applications may also receive it.

---

## 67. Background Execution

Background execution is not automatically unlimited.

Applications may declare background requirements such as:

```text
network synchronization
audio playback
compute task
timer
device monitoring
```

The resource manager and session policy decide whether to grant them.

---

## 68. Background Work as Resource Usage

Instead of a generic unrestricted background flag, applications should preferably request the specific resources they require.

This allows more precise scheduling.

---

## 69. Resource Pressure

Applications must tolerate resource pressure.

The system may signal:

```text
memory pressure
GPU pressure
battery pressure
network cost change
thermal pressure
storage pressure
```

Applications may release caches or reduce activity.

---

## 70. Suspension Under Pressure

If pressure remains high, the system may:

```text
throttle
background
suspend
persist
terminate execution context
```

without destroying the logical application.

---

## 71. Execution Profiles

Applications may request an **Execution Profile** describing workload intent.

Examples:

```text
Interactive
Background
ComputeIntensive
Workstation
ExclusiveInteractive
LowPower
RealTimeCandidate
```

Profiles are requests rather than unconditional rights.

---

## 72. Presentation Profiles

Presentation policy may also use profiles.

Examples:

```text
NormalDesktop
Minimal
Workstation
Gaming
Kiosk
Media
Remote
Accessibility
```

A shell may change its behavior according to the active profile.

---

## 73. Workstation Profile

A high-load engineering or content-creation application may request a workstation profile.

Possible effects:

```text
reduce shell GPU usage
reduce animations
pause thumbnail generation
reduce indexing
reserve GPU memory
reserve CPU capacity
reserve memory
prioritize storage bandwidth
deprioritize cosmetic background services
```

This is a coordinated system policy, not a collection of unrelated application hacks.

---

## 74. Exclusive Interactive Profile

Games, VR, or latency-sensitive applications may request:

```text
exclusive presentation
low-latency input
high-performance power mode
GPU priority
audio priority
reduced background activity
```

The session controller decides which parts can be granted.

---

## 75. Shell Dormancy

During exclusive execution, the shell may enter a dormant state.

A minimal control component may remain available for:

```text
secure system actions
emergency UI
session switching
critical notifications
```

The full shell need not consume significant GPU or memory.

---

## 76. Shell Does Not Own Hardware

The shell does not own display, GPU, input devices, or audio.

It receives capabilities to manage them under normal presentation policy.

The system may reassign these resources when appropriate.

---

## 77. Exclusive Display

A performance-sensitive application may request an exclusive display surface if policy allows.

The normal compositor may be bypassed or reduced.

---

## 78. Exclusive Input

A session may grant an application a low-latency or exclusive input path.

System-reserved secure controls remain outside application authority.

---

## 79. Resource Reservations

Applications may request resource reservations.

Example:

```text
12 CPU cores
32 GB RAM
12 GB GPU memory
4 GB/s storage bandwidth
hardware decoder
low-latency audio
```

The system may return:

```text
Granted
PartiallyGranted
Denied
```

Applications should respond accordingly.

---

## 80. Reservation Is Not Ownership

Resource reservation guarantees availability or priority according to a contract.

It does not necessarily mean permanent exclusive ownership.

---

## 81. Dynamic Profiles

Execution and presentation profiles may change during an application's lifetime.

Example:

```text
Video Editor
    editing → Workstation
    idle → Interactive
    background export → ComputeIntensive
```

Profile transitions should not require application restart.

---

## 82. Profile Negotiation

The application requests intent.

The OS decides actual allocation.

Applications should be able to observe what was actually granted.

---

## 83. Application Priority

Traditional process priorities may remain available for .NET compatibility.

Native WitOS applications should prefer semantic workload declarations over numeric process priority.

---

## 84. Power Model

Applications may express power intent.

Example:

```text
PreferPerformance
Balanced
PreferEfficiency
MustRemainResponsive
```

The system combines this with battery status, thermal state, device policy, and current workload.

---

## 85. Mobile and Desktop Use the Same Lifecycle

WitOS should not have separate desktop and mobile lifecycle models.

All applications use the same lifecycle system.

Policy differs; semantics do not.

---

## 86. Device Class Is Policy

A battery-powered touch device naturally applies different policy from a multi-GPU workstation.

The application model remains unchanged.

---

## 87. Application Restoration

Restoration should provide applications with information about environmental changes.

The application can adapt presentation without treating the destination as a different platform.

---

## 88. Restoration Contract

The important principle is explicit lifecycle awareness.

The exact API remains open.

---

## 89. Crash Recovery

Application execution failure may result in:

```text
restart from logical state
restore recent snapshot
ask user
remain stopped
```

depending on policy.

---

## 90. Service Supervision

System services and application workers may be supervised.

A service crash should not require rebooting the whole OS.

---

## 91. Application Components

An application may consist of multiple components.

Example:

```text
Engineering Suite
    ├── UI
    ├── solver
    ├── renderer
    ├── plugin host
    └── indexer
```

Each component may receive separate capabilities and isolation.

---

## 92. Component Isolation

Components may execute:

```text
in-process
separate managed isolation domain
separate address space
remote
WASM sandbox
```

The application model does not force one isolation mechanism.

---

## 93. Plugins

Plugins are application components with delegated capabilities.

A plugin should not automatically inherit the full authority of its host.

---

## 94. Plugin Lifecycle

Plugins may have their own lifecycle independent of the host process.

They may load, suspend, restart, update, or unload without terminating the logical host application.

---

## 95. Application Updates

Application package updates should preserve:

```text
ApplicationId
persistent state
persistent authorized grants
```

where compatible.

Updating an application does not create a new logical identity.

---

## 96. Version Migration

Applications must be able to migrate persistent state between versions.

This should be explicit rather than relying on memory snapshots.

---

## 97. Atomic Application Updates

Where practical, application updates should be atomic and rollback-capable.

---

## 98. Multiple Application Versions

The system may temporarily retain multiple versions to support rollback, compatibility, state migration, or running old execution contexts.

---

## 99. Application Dependencies

Applications may depend on shared packages or frameworks.

.NET itself remains a platform runtime.

Dependency resolution should avoid unnecessary private duplication while preserving version isolation.

---

## 100. Native Dependencies

Applications may contain architecture-specific native components.

Such dependencies reduce portability but remain supported.

Managed-only packages remain preferred for universal applications.

---

## 101. Application Compatibility

A standard portable .NET application may use:

```text
System.IO
System.Net
System.Threading
System.Console
System.Diagnostics
```

through compatibility mappings.

It does not need to adopt the WitOS application model fully.

---

## 102. Compatibility Application Mode

Existing .NET applications may initially execute in a compatibility application environment:

```text
Existing .NET Application
        ↓
Standard .NET APIs
        ↓
Compatibility Services
        ↓
WitOS Resources
```

This enables gradual adoption.

---

## 103. Native Application Mode

A native WitOS application may use:

```text
resource discovery
capabilities
application state
handoff
distributed compute
presentation profiles
resource reservations
```

directly.

Both models coexist.

---

## 104. Application Location

Applications are not conceptually installed "on a machine" unless policy requires it.

Execution may cause required components to materialize on the current device.

---

## 105. Application State Location

Logical application state may be:

```text
local
replicated
remote
encrypted
device-bound
user-bound
```

Application code should not assume one fixed physical location.

---

## 106. Offline Operation

WitOS applications must be able to express whether they require connectivity.

A distributed architecture must not make network availability mandatory for ordinary local applications.

---

## 107. Application Autonomy

A local calculator should remain a local calculator.

It should not require cloud identity, remote state, or distributed infrastructure simply because the OS supports them.

---

## 108. Remote Presentation

Execution and presentation may be separated.

Example:

```text
Application execution
        Server
          │
          │ presentation protocol
          ▼
        Tablet
```

The application may still perceive a standard presentation resource.

---

## 109. Presentation Migration

A graphical session may move presentation without moving compute.

This is useful when data or compute resources are expensive to migrate.

---

## 110. Headless Transition

An application may lose all presentation endpoints and continue executing.

GUI destruction must therefore not inherently imply termination.

---

## 111. Presentation Reattachment

A user may later reopen a UI for an already-running application.

No new logical application need be created.

---

## 112. Kiosk Mode

Kiosk operation should not require starting a desktop and hiding it.

Instead:

```text
Boot
 ↓
Session
 ↓
Application receives display/input
 ↓
No shell required
```

---

## 113. Embedded Mode

An embedded device may run:

```text
kernel
runtime
services
application
```

with no shell, compositor, or console if none are required.

This remains WitOS.

---

## 114. Server Mode

A server may expose:

```text
network
storage
compute
terminal
remote management
```

with no GUI installed.

No separate operating-system architecture is required.

---

## 115. Desktop Mode

A desktop environment is constructed from:

```text
graphical compositor
desktop shell
terminal
notification service
windowing environment
```

These are optional components.

---

## 116. Phone Mode

A phone environment may use:

```text
touch shell
fullscreen presentation
background policy
telephony resources
battery-sensitive scheduling
```

The underlying application model remains identical.

---

## 117. Tablet Mode

A tablet shell may provide:

```text
touch
pen
split screen
floating windows
keyboard when available
```

without creating a new application platform.

---

## 118. Workstation Mode

A workstation may run the same OS with:

```text
high-memory policy
resource reservations
multi-display shell
GPU-heavy applications
specialized compute
```

No separate workstation edition is required.

---

## 119. Shell Marketplace / Community

Because shells are applications implementing published interfaces, the community may develop alternatives.

A shell package may be installed, selected, updated, or removed like other applications.

System policy determines whether a shell is trusted to receive session capabilities.

---

## 120. Shell Security

Installing a shell does not automatically grant it session-control capabilities.

The user or administrator must explicitly select/authorize it.

---

## 121. Safe Shell Recovery

The system should retain a minimal trusted recovery presentation path.

If a shell crashes:

```text
Shell crash
    ↓
session controller remains alive
    ↓
restart shell / select alternate shell
```

The user must not lose the entire OS session merely because a desktop shell failed.

---

## 122. Compositor Failure

Graphical presentation failure should ideally be recoverable without terminating unrelated applications.

Applications may temporarily lose presentation endpoints while retaining logical execution state.

---

## 123. Presentation Service Failure

A compositor may restart and applications reacquire surfaces.

This is preferable to system-wide failure.

---

## 124. Secure System Presentation

Some UI must be trusted independently of the current shell.

Examples:

```text
login
permission consent
secure attention
credential entry
critical security warning
shell recovery
```

These should use a trusted presentation capability controlled by system security services.

---

## 125. Permission Dialogs

Applications must not render their own authoritative permission dialogs.

Capability acquisition requiring consent should invoke trusted system presentation.

---

## 126. Application Termination Rights

Applications may request their own termination.

The session/system may terminate execution contexts when permitted.

Persistent logical state is preserved according to policy.

---

## 127. Forced Termination

Forced termination should still allow the system to distinguish:

```text
execution stopped
```

from:

```text
application data deleted
```

These must never be equivalent operations.

---

## 128. Application Uninstall

Uninstall removes the application package and registration.

Persistent state may be deleted, preserved, exported, or retained for reinstallation according to explicit user/policy choice.

---

## 129. Application Data Ownership

Application state and user-created resources are distinct.

Uninstalling a photo editor must not imply deleting user photos merely because the application held capabilities to them.

---

## 130. Sessions and Handoff

A user session may itself span devices.

Example:

```text
User Session
    ├── desktop presentation
    ├── phone presentation
    └── remote compute resources
```

The system need not require the session to be physically located on one device.

---

## 131. Multi-Device Sessions

A future implementation may allow concurrent presentation of one logical session on several devices.

Policy determines whether applications are mirrored, moved, independent, or shared.

---

## 132. Resource Rebinding

When execution or presentation environment changes, applications should be able to rebind abstract resources.

The semantic role remains while the physical provider changes.

---

## 133. Stable vs Replaceable Resources

Applications may distinguish:

```text
must remain same logical resource
```

from:

```text
any equivalent resource acceptable
```

This distinction is crucial for migration.

---

## 134. Application Contracts

An application may declare contracts describing:

```text
required capabilities
optional capabilities
restorable state
handoff support
background behavior
presentation needs
performance profiles
```

These allow the system to make lifecycle decisions intelligently.

---

## 135. Graceful Degradation

Applications should adapt when optional capabilities disappear.

Loss of one resource should not necessarily terminate the entire application.

---

## 136. Resource Arrival

Applications may gain capabilities dynamically.

Examples:

```text
keyboard attached
external display connected
GPU node available
network restored
```

Applications may respond without restart.

---

## 137. Dynamic Device Transformation

A device itself may change role.

Example:

```text
tablet
   ↓ dock connected
desktop-style environment
```

The OS does not reboot into another edition.

Capabilities change.

Shell/presentation policy may adapt.

Applications remain running.

---

## 138. Shell Switching

A user may switch shells during a session.

Applications should not require restart merely because the shell changed.

---

## 139. Profile Switching

Similarly:

```text
Normal Desktop
   ↓
Workstation Profile
   ↓
Normal Desktop
   ↓
Gaming Profile
```

may happen without reboot.

---

## 140. Presentation Policy Ownership

The shell normally implements presentation policy.

The underlying resources remain owned and enforced by system resource/security services.

Therefore:

> **The shell owns presentation policy, not presentation hardware.**

---

## 141. Application Resource Contracts

Applications may describe resource needs in semantic terms.

This is preferable to static device-class targeting.

---

## 142. Unsupported Environment

An application is unavailable on a device only when required capabilities cannot be satisfied.

Reason:

```text
Missing:
    minimum 4 GB RAM
    interactive display
```

Not:

```text
Android version unavailable
```

---

## 143. Universal Binary Goal

For managed applications, the preferred deployment artifact is architecture-independent IL.

The same package should be usable across x64, ARM64, and future RISC-V when no unavoidable native dependency exists.

---

## 144. Runtime Selection

The system may execute applications using:

```text
CoreCLR/JIT
NativeAOT
other approved managed runtime modes
```

without changing logical application identity.

---

## 145. Lifecycle and .NET Compatibility

Existing .NET applications that are unaware of advanced lifecycle semantics must still run.

The compatibility environment may provide traditional behavior:

```text
process starts
Main()
process ends
```

Native WitOS applications may opt into richer lifecycle contracts.

---

## 146. Progressive Adoption

An existing application may begin as an ordinary .NET app and gradually adopt:

```text
capabilities
adaptive presentation
persistent application state
handoff
resource reservation
```

without requiring a rewrite.

---

## 147. Application Model Invariants

1. Application identity is independent of process identity.
2. Application identity is independent of device identity.
3. Application identity is independent of presentation.
4. GUI is optional.
5. Shell is replaceable.
6. The shell is an application with granted capabilities, not a hardcoded OS component.
7. Closing presentation does not inherently terminate the application.
8. Logical state is distinct from transient execution state.
9. Physical resources may need reacquisition after restore or migration.
10. Desktop, mobile, server, and embedded environments use the same lifecycle model.
11. Applications depend on capabilities, not device classes.
12. Performance profiles are explicit resource-policy contracts rather than hidden heuristics.

---

## 148. Deferred Questions

This RFC intentionally leaves several details for later documents:

- application package format;
- signing;
- default state store;
- synchronization and replication;
- handoff protocol;
- exact windowing API;
- compositor protocol;
- process/address-space mapping;
- scheduling;
- reservation protocol;
- distributed sessions.

---

## 149. Suggested Follow-Up RFCs

```text
RFC 0004 — Security, Identity & Capability Delegation
RFC 0005 — Execution, Scheduling & Compute Model
RFC 0006 — IPC and Local/Remote Communication
RFC 0007 — Universal Hardware Interface
RFC 0008 — Storage & Persistent State
RFC 0009 — Presentation, Shell & Input Architecture
RFC 0010 — Application Packaging & Distribution
```

---

## 150. Summary

WitOS separates the logical application from the mechanics of executing and displaying it.

An application:

```text
has identity
has logical state
holds capabilities
may execute
may suspend
may migrate
may have UI
may have no UI
may have multiple UIs
may span devices
```

A process is only an execution mechanism.

A window is only a presentation endpoint.

A shell is only a replaceable presentation-policy application.

A device is only a collection of resources and capabilities.

The defining principle is:

> **An application is a persistent logical entity whose execution, resources, presentation, and physical location may change over time without changing what the application is.**
