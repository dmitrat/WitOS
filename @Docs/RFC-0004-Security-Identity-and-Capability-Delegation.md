# WitOS
## RFC 0004 — Security, Identity & Capability Delegation
### Draft v0.1

## 1. Status

Draft.

This document defines the initial security, identity, authority, capability issuance, delegation, attenuation, revocation, and trust model of WitOS.

It builds upon:

```text
RFC 0001 — Architecture Concept
RFC 0002 — Resource & Capability Model
RFC 0003 — Application & Lifecycle Model
```

The central principle is:

> **Identity answers who or what something is. Capabilities answer what it is allowed to do.**

These concepts must remain separate.

---

## 2. Motivation

Traditional operating-system security commonly combines several historically independent concepts:

```text
user identity
process identity
filesystem ownership
ACLs
administrator/root privilege
application identity
service accounts
device permissions
```

This often produces coarse authority models.

Examples include:

```text
run as administrator
run as root
application runs as the user
process owns all inherited handles
```

Modern systems require finer control.

A text editor that may edit one selected document does not inherently require authority over all user documents, camera, microphone, network, other applications, or system configuration.

WitOS therefore treats authority as explicit resource capabilities.

---

## 3. Fundamental Security Concepts

WitOS distinguishes at least:

```text
Identity
Authentication
Authority
Capability
Policy
Trust
Isolation
Audit
```

These concepts interact but are not interchangeable.

---

## 4. Identity

An identity describes a logical entity.

Possible identities include:

```text
UserIdentity
ApplicationIdentity
DeviceIdentity
ServiceIdentity
OrganizationIdentity
SessionIdentity
ResourceIdentity
```

Identity by itself grants no authority.

---

## 5. Authority

Authority represents permission to perform an operation.

In WitOS, authority should normally be represented by possession of a capability.

Conceptually:

```text
Identity
   ≠
Authority
```

A known application may have no access to a resource.

An anonymous component may temporarily receive a narrowly scoped capability.

---

## 6. Capability Principle

A capability is an unforgeable representation of authority over a resource or operation.

Example:

```csharp
IReadableStorage document;
```

means that the holder may perform the operations defined by `IReadableStorage`.

The application should not separately need a global security check for every operation.

Possession of the valid capability is the authorization.

---

## 7. Identity Does Not Grant Ambient Authority

WitOS should minimize ambient authority.

Applications should not implicitly receive:

```text
all files belonging to user
all network connectivity
all devices
all environment variables
all system configuration
```

simply because they run in a user's session.

Authority must be explicit or inherited through defined policy.

---

## 8. No Universal Administrator Capability

WitOS should avoid treating Administrator or Root as a universal software capability wherever possible.

Administrative identity may allow a user to authorize operations.

It should not necessarily cause every application launched by that user to execute with unlimited authority.

---

## 9. Privilege Is Multidimensional

Privilege is not a simple ordering:

```text
User < Administrator < Kernel
```

Instead authority forms a graph.

Example:

```text
Storage Service:
    raw block access
    no camera access

Camera Driver:
    camera MMIO
    no user documents

Network Service:
    network hardware
    no GPU control

Shell:
    application launching
    workspace management
    no arbitrary storage access
```

One component may be more privileged in one domain and completely powerless in another.

---

## 10. Root Capability Set

At early boot, WitOS necessarily begins with authority over fundamental machine resources.

Conceptually:

```text
Root Capability Set
    │
    ├── Physical Memory
    ├── CPU Resources
    ├── Interrupts
    ├── Device Resources
    ├── DMA/IOMMU
    └── Security Root
```

This authority is progressively attenuated and delegated.

---

## 11. Boot Authority Flow

Example:

```text
Hardware/Firmware
       ↓
NanoKernel
       ↓
Device Manager
       ↓
Driver
       ↓
System Service
       ↓
Application
```

At every boundary, authority should become narrower rather than broader.

---

## 12. Capability Attenuation

A capability may be transformed into a weaker capability.

Example:

```text
Read + Write + Delete
          ↓
      Read + Write
          ↓
        Read
```

The reverse operation is impossible without another authority source.

---

## 13. Monotonic Delegation

Ordinary delegation must follow:

> **Delegation may preserve or reduce authority. It may never create authority that the delegator does not possess.**

---

## 14. Capability Delegation

A holder may explicitly transfer or share authority.

Example:

```text
Photo Manager
      │
      │ selected image read capability
      ▼
Photo Editor
```

The editor receives access to that image.

It does not receive access to the entire photo library.

---

## 15. Delegation Is Explicit

Capability transfer should be visible in source-level semantics where practical.

Example:

```csharp
var delegated =
    image.Restrict(ImageAccess.Read);

await editor.ActivateAsync(delegated);
```

Authority should not silently leak through unrelated APIs.

---

## 16. Delegation Scope

A delegated capability may be constrained by:

```text
operation
resource subset
time
session
number of uses
data size
location
trust domain
application identity
```

---

## 17. Temporary Capabilities

Many resource grants should naturally be temporary.

Examples:

```text
camera while application is foreground
microphone during call
exclusive GPU reservation during render
temporary file access
one-time signing operation
```

Temporary authority reduces the consequences of compromise.

---

## 18. One-Shot Capabilities

Some operations may issue capabilities usable exactly once.

Examples:

```text
authorize payment
open selected file
sign one document
consume one secret
```

After successful use, the capability becomes invalid.

---

## 19. Capability Revocation

Capabilities must support revocation where the underlying authority semantics require it.

Reasons include:

```text
user revokes permission
application loses foreground
device disconnects
session ends
resource provider fails
security compromise
lease expires
administrator changes policy
```

---

## 20. Revocation Semantics

Revocation should be explicit and observable.

Long-running operations should receive cancellation or an equivalent failure signal.

---

## 21. Capability Lifetime

Possible lifetimes include:

```text
single operation
scope
application activation
application instance
session
user grant
persistent grant
device lifetime
```

Lifetime should be part of the capability contract where relevant.

---

## 22. Persistent Grants

Some user decisions may survive restart.

The live capability itself should not simply be serialized.

Instead WitOS stores a secure grant record that may be used to reissue an appropriate capability.

---

## 23. Capability Rehydration

On application restoration:

```text
Persistent Grant
      ↓
policy validation
      ↓
resource validation
      ↓
new live capability
```

The new capability may differ internally from the previous one.

---

## 24. Resource Identity vs Authority

A `ResourceId` identifies.

It does not authorize.

This must remain true even if a resource ID is globally unique.

---

## 25. Application Identity

Applications have stable cryptographic/logical identity independent of process ID, installation path, device, current version, or execution context.

An application update normally preserves application identity.

---

## 26. Package Identity

The package format should eventually contain verifiable identity information.

Potential inputs include:

```text
publisher
package identifier
signature
version
content hash
```

Exact package-signing semantics are deferred to RFC 0010.

---

## 27. Application Version Is Not Identity

Different versions may represent the same `ApplicationId`.

Capabilities granted to the application may survive compatible updates according to policy.

---

## 28. Publisher Identity

Applications may optionally have publisher identity.

Publisher identity may help policy decisions such as trusting updates or verifying signatures.

Publisher identity must not automatically grant unrelated resource capabilities.

---

## 29. User Identity

A user identity describes a person or logical user principal.

Users may authenticate through:

```text
password
passkey
hardware key
biometric
remote identity provider
organization identity
```

Authentication mechanisms are outside the core capability model.

---

## 30. Authentication vs Authorization

Authentication answers:

> Who are you?

Authorization answers:

> What may you do?

Successful authentication must not imply unrestricted authority.

---

## 31. Session Identity

A user may have multiple concurrent sessions.

Each session may expose different capabilities.

---

## 32. Device Identity

A device may possess a persistent device identity.

This identity may be backed by secure hardware or software depending on device capability.

Device identity can support:

```text
secure boot
device trust
encrypted storage
remote resource authorization
```

---

## 33. Device Identity Is Optional

WitOS must support hardware without dedicated security hardware.

Strong hardware-backed device identity improves trust guarantees but must not be mandatory for running the OS.

---

## 34. Trust Levels

Resources or execution environments may expose trust characteristics.

Possible examples:

```text
Untrusted
Sandboxed
LocalUser
LocalTrusted
OrganizationTrusted
HardwareTrusted
```

These should describe properties, not simplistic global rankings.

---

## 35. Trust Domains

Resources may belong to security domains such as:

```text
same process
same application
same device
same user
home network
organization
public Internet
external cloud
```

Applications may constrain where sensitive information can flow.

---

## 36. Data Flow Constraints

Security constraints may participate in resource selection.

For example, an application may require a compute resource to remain inside the local-device trust domain.

---

## 37. Capability Acquisition

Applications request authority from a capability authority or resource resolver.

The request may be automatically allowed, denied, policy-controlled, user-consent controlled, or administrator-controlled.

---

## 38. Request Is Not Grant

An application request describes intent.

The system decides authority.

```text
Request Camera
       ↓
Policy
       ↓
Consent if needed
       ↓
Capability
```

Applications cannot manufacture authority by declaring requirements.

---

## 39. User Consent

Certain capabilities may require explicit user consent.

Examples:

```text
camera
microphone
location
selected documents
screen capture
contacts
```

Consent should be requested at the moment it becomes meaningful where practical.

---

## 40. Trusted Consent UI

Permission prompts must be rendered by a trusted system presentation service.

An application must not be able to create UI indistinguishable from the authoritative permission UI.

This requirement applies even when the normal shell is community-developed.

---

## 41. Shell Independence

The shell may provide visual integration but must not own security authority.

Security decisions belong to security services and trusted presentation, not to the shell.

---

## 42. Community Shell Security

A third-party shell may receive application launching, window placement, workspace, and notification capabilities.

It must not automatically receive all application data, credentials, device capabilities, or security root.

---

## 43. Secure Attention

WitOS should reserve a secure mechanism for reaching trusted system UI.

A shell or application must not be able to block or impersonate this mechanism completely.

---

## 44. Credential UI

Password, passkey, biometric, or cryptographic signing prompts should use trusted presentation.

Applications should request authentication or signing operations rather than directly receiving sensitive credentials where possible.

---

## 45. Secret Isolation

Applications should avoid handling long-lived secrets unnecessarily.

Instead of receiving a private signing key, an application may receive an `ISigningCapability`.

The private key never leaves its trusted provider.

---

## 46. Security Services as Resources

Authentication and cryptographic services themselves fit the resource model.

Examples:

```text
IAuthenticator
ISigningService
ISecretStore
ICertificateStore
```

Applications receive narrowly scoped capabilities to them.

---

## 47. Capability Brokers

Some system services act as capability brokers.

A file picker may allow the user to select a document and return an `IReadableStorage` capability to the application.

The file picker need not expose filesystem paths as the security primitive.

---

## 48. Open With

A document manager may delegate a document capability to another application.

This naturally implements `Open With...` without granting access to the surrounding directory.

---

## 49. Drag and Drop

Drag-and-drop may transfer capabilities.

The transfer itself becomes a security operation.

---

## 50. Clipboard

Clipboard data may contain:

```text
value data
resource references
temporary capabilities
```

Authority transfer must be explicit.

Serializing a `ResourceId` must not transfer authority automatically.

---

## 51. Plugins

Plugins must not automatically inherit all host capabilities.

Capability delegation provides natural plugin sandboxing.

---

## 52. Plugin Trust

Different plugins may execute at different trust levels.

Possible environments:

```text
same managed context
isolated managed context
separate process
WASM sandbox
remote execution
```

Capability semantics remain consistent across these boundaries.

---

## 53. Child Components

The same principle applies to application workers.

A solver worker may receive only its input dataset, compute resource, and output target.

---

## 54. Services

System services must also follow least privilege.

A service should not become trusted merely because it starts during boot.

---

## 55. Driver Security

Managed drivers may receive low-level capabilities such as:

```text
MMIO region
IRQ
DMA domain
device reset
power control
```

These capabilities should be specific to the device being driven.

---

## 56. Driver Isolation

A network driver should not inherently receive arbitrary physical memory, all PCI devices, or all storage.

DMA should preferably be constrained through IOMMU domains where hardware permits.

---

## 57. DMA Authority

DMA is fundamentally equivalent to memory authority.

A device with unconstrained DMA can bypass software isolation.

Therefore WitOS must treat DMA configuration as a privileged capability.

---

## 58. IOMMU

On hardware with an IOMMU, driver/device memory access should be explicitly mapped rather than allowing unrestricted DMA.

---

## 59. Memory Capabilities

Low-level memory authority may include:

```text
Map
Read
Write
Execute
Pin
Share
DMA
```

These should remain distinct where technically useful.

---

## 60. W^X Principle

Executable memory should preferably follow:

```text
Writable XOR Executable
```

unless explicit capability/policy allows otherwise.

JIT runtimes such as CoreCLR require controlled transitions.

---

## 61. .NET JIT Compatibility

WitOS must preserve standard .NET runtime functionality.

CoreCLR may require writable code pages, machine-code generation, and controlled transition to executable pages.

The security model must support this without modifying standard .NET semantics.

---

## 62. Runtime Trust

The upstream .NET runtime forms part of the trusted computing base for managed execution.

Trusted components include, depending on configuration:

```text
kernel
.NET runtime
JIT
GC
interop layer
critical security services
```

---

## 63. Managed Safety

Managed execution provides important memory-safety guarantees but does not automatically solve:

```text
logic bugs
incorrect authorization
resource exhaustion
unsafe interop
runtime vulnerabilities
side channels
```

Capabilities complement memory safety.

---

## 64. Unsafe Code

Standard .NET `unsafe` code remains supported for compatibility.

However, `unsafe` in C# must not imply unrestricted OS authority.

---

## 65. P/Invoke

Standard .NET P/Invoke functionality should be supported where appropriate.

Native libraries execute with the capabilities and isolation assigned to their containing execution environment.

P/Invoke does not bypass the capability model.

---

## 66. Native Library Loading

Loading native code may itself require policy.

Applications should not necessarily be able to execute arbitrary downloaded machine code merely because they have network access.

---

## 67. Native Components

Native application components may require stronger sandboxing because the runtime cannot enforce managed memory safety within them.

Possible policies include separate address spaces, WASM sandboxes, and restricted native hosts.

---

## 68. Network Authority

Network access should be capability-based.

Possible capabilities:

```text
InternetClient
ListenLocal
ListenPublic
ConnectToService X
LocalNetworkDiscovery
RawNetwork
```

These represent very different authority levels.

---

## 69. Raw Network Access

Ordinary applications should not need raw packet access.

Raw network interfaces should be highly restricted.

---

## 70. Standard .NET Networking

Existing applications using:

```text
HttpClient
Socket
TcpClient
UdpClient
```

must retain standard semantics.

Their compatibility environment may receive a general network capability according to application policy.

---

## 71. Filesystem Compatibility

Existing .NET applications commonly assume filesystem access.

WitOS may provide them with a compatibility filesystem view.

Security policy defines which resources appear within that view.

---

## 72. Standard File APIs

Existing code:

```csharp
File.OpenRead(path);
```

should continue working.

The path resolves inside the application's authorized namespace.

Path knowledge alone does not bypass resource authority.

---

## 73. Environment Isolation

Standard APIs such as `Environment`, `Process`, filesystem paths, and temporary directories should expose application-appropriate views rather than uncontrolled global machine state.

---

## 74. Process Introspection

Applications should not necessarily see or inspect every execution context on the machine.

Compatibility APIs may expose only authorized process views.

---

## 75. Resource Visibility

Security operates before capability acquisition.

An application may not even discover sensitive resources.

Visibility and authority are separate.

---

## 76. Privacy of Resource Metadata

Resource descriptors may reveal sensitive information.

Descriptor access itself may therefore be filtered.

---

## 77. Namespaces as Security Views

Application namespaces may be generated from capabilities.

A `/Documents` view does not necessarily represent a physical directory.

---

## 78. Sandboxing

An application sandbox consists of:

```text
execution isolation
+
resource visibility
+
capability set
+
policy
```

It is not defined merely by filesystem virtualization.

---

## 79. Default-Deny

For native WitOS applications, the default should generally be:

> **No authority unless granted.**

Compatibility profiles may provide broader conventional authority when necessary for existing .NET applications.

---

## 80. Compatibility Profiles

A legacy portable .NET application may execute under a profile such as:

```text
ConsoleCompatibility
DesktopCompatibility
ServerCompatibility
```

These profiles map expected standard .NET behavior onto reasonable capability sets.

---

## 81. Progressive Security Adoption

An existing application should be able to run without becoming fully WitOS-aware.

Later it may adopt document capabilities, fine-grained network grants, plugin delegation, or secure storage without abandoning standard .NET compatibility.

---

## 82. Capability-Aware Applications

Native applications should prefer narrow APIs.

Editing one document should require a document capability rather than full filesystem authority.

---

## 83. Authority Inspection

Applications should be able to inspect the authority they currently hold.

This supports adaptive behavior.

---

## 84. Security Must Be Capability-Based, Not OS-Based

Applications check the actual capability rather than OS identity.

This follows the same rule used throughout WitOS.

---

## 85. Capability Negotiation

A resource request may return:

```text
Granted
GrantedWithRestrictions
Denied
Unavailable
```

An application can adapt to weaker authority.

---

## 86. Delegation Across Processes

Kernel IPC should support capability transfer.

Receiving a message may atomically grant authority.

---

## 87. Delegation Across Devices

Remote capability delegation requires stronger representation.

A remote capability may involve:

```text
resource identity
grant identity
issuer
permissions
expiration
cryptographic proof
routing information
```

The programming model should remain consistent with local capabilities.

---

## 88. Remote Capabilities Are Not Raw References

A managed object reference cannot simply be serialized and treated as remote authority.

Remote delegation requires an authenticated authority protocol.

---

## 89. Capability Authenticity

Remote capabilities must be verifiable.

Possible implementations include signed tokens, secure session-bound handles, or cryptographic object capabilities.

---

## 90. Capability Confidentiality

Some capability tokens may themselves be secrets.

Possession grants authority.

Such representations must be protected from logs, accidental serialization, clipboard, and untrusted storage.

---

## 91. Replay Protection

Remote or persistent capability protocols may require replay protection.

Possible mechanisms include nonces, expiration, session binding, monotonic counters, and one-shot grants.

---

## 92. Revocation Across Devices

Distributed revocation is inherently more complex than local revocation.

Potential strategies include:

```text
short-lived capabilities
online revocation service
versioned grants
session-scoped tokens
provider-side validation
```

---

## 93. Offline Capabilities

WitOS must support offline operation.

Some persistent capabilities may remain usable without contacting a central server.

---

## 94. No Mandatory Cloud Authority

Running ordinary local applications must not require cloud login, central capability service, or continuous network connectivity.

Distributed identity is optional infrastructure.

---

## 95. Organization Policy

Organizations may impose policy such as:

```text
block untrusted native code
restrict external compute
require signed applications
prevent data leaving organization resources
```

Policy affects capability issuance.

It does not replace capability enforcement.

---

## 96. Policy vs Mechanism

The kernel and resource system provide mechanisms.

Policy decides how capabilities are granted.

Policy sources may include user preference, application manifest, organization administration, device policy, parental control, resource provider, and security service.

---

## 97. Policy Composition

Several policies may apply simultaneously.

A capability should only be issued if required policies agree.

---

## 98. Security Decisions Must Be Explainable

The system should be capable of explaining why a capability was denied where revealing the reason is safe.

---

## 99. Audit

Security-sensitive actions may be audited.

Examples:

```text
capability requested
capability granted
capability delegated
capability revoked
secure authentication
administrative policy change
```

---

## 100. Capability Provenance

For debugging and security diagnostics, the system may retain provenance.

This can make authority understandable.

---

## 101. Provenance Is Not Runtime Authorization

The security decision is possession of valid authority.

Provenance assists audit, debugging, explanation, and incident response.

---

## 102. Resource Exhaustion

Security includes resource isolation.

Applications may attack the system by consuming:

```text
CPU
memory
GPU
storage
network bandwidth
handles
IPC queues
```

Resource capability limits should therefore also support quotas and reservations.

---

## 103. CPU Authority

An application may receive ordinary shared compute authority by default.

Advanced execution capabilities such as exclusive physical cores, real-time scheduling, or topology control must require explicit authority.

---

## 104. WitThreads Security

`OutWit.OS.Execution` is an optional managed API exposing advanced kernel execution capabilities.

A program cannot gain exclusive cores merely by referencing the assembly.

It must acquire the corresponding capability from WitOS.

---

## 105. Portable WitThreads Behavior

On non-WitOS platforms:

```text
OutWit.OS.Execution
    ↓
Windows/Linux/macOS backend
    ↓
best available native mechanisms
    ↓
standard .NET fallback
```

Security guarantees degrade to what the host OS can enforce.

Applications inspect granted guarantees, not OS identity.

---

## 106. GPU Authority

GPU access may be divided into:

```text
graphics presentation
general compute
video decoding
video encoding
exclusive performance reservation
low-level device access
```

These should not necessarily be one universal permission.

---

## 107. Presentation Security

An application with normal windowing capability must not automatically gain screen capture, other-application input, global keyboard logging, or trusted security UI.

These are distinct capabilities.

---

## 108. Screen Capture

Screen capture is security-sensitive.

Possible authority:

```text
capture own surface
capture selected window
capture selected display
capture all presentation
```

These should be separate.

---

## 109. Input Security

Input resources require similar separation.

Normal applications typically need only focused input.

---

## 110. Secure Input

Credential entry may request a secure input path that prevents observation by ordinary applications and shells.

The exact hardware guarantee varies by device.

---

## 111. Application Launch Authority

Launching arbitrary applications is itself a capability.

Shells normally receive it.

Other applications may receive narrower forms.

---

## 112. Session Control Authority

Operations such as logout, lock, switch user, replace shell, or terminate another application require explicit session capabilities.

---

## 113. Shell Replacement

Installing a shell package does not grant `ISessionController`.

Selecting that shell through trusted system configuration may grant it during session startup.

---

## 114. System Recovery

WitOS must maintain a trusted recovery path independent of the current shell, compositor, and ordinary applications.

---

## 115. Secure Boot

On supported hardware, firmware may verify the next boot stage.

Conceptually:

```text
Hardware Root
     ↓
Firmware
     ↓
Kernel
     ↓
Core OS
```

---

## 116. Secure Boot Is Optional Hardware Enhancement

Legacy hardware without WitOS-native firmware remains supported.

The security guarantees may be weaker.

The OS should report actual guarantees rather than pretending they exist.

---

## 117. Measured Boot

Platforms capable of secure measurement may expose firmware, kernel, and OS image measurements to security services.

---

## 118. Remote Attestation

Remote services may optionally require proof of platform state.

Ordinary local application execution must not depend on remote attestation.

---

## 119. Encryption

Storage encryption may be implemented as a resource transformation:

```text
Physical Storage
      ↓
Encrypted Storage Provider
      ↓
Authorized Storage Resource
```

Applications need not know encryption implementation details.

---

## 120. Key Authority

Encryption keys should preferably be represented through cryptographic capabilities rather than raw byte arrays.

---

## 121. Secret Store

Applications may request an `ISecretStore` scoped to their identity.

Cross-application secret access requires explicit delegation.

---

## 122. Application Data Isolation

Private application data may be represented as resources automatically granted to the application identity.

Other applications do not see them unless explicitly delegated.

---

## 123. User Documents

User-created documents should not necessarily belong to the creating application.

Application uninstall must not destroy user documents merely because it originally created them.

---

## 124. Multi-User Resources

A resource may grant different capabilities to different users or applications.

Capabilities replace the need for every API to understand ACLs directly.

---

## 125. ACL Compatibility

ACLs are useful for persistent policy.

Capabilities are useful for active authority.

WitOS may therefore use:

```text
persistent ACL/policy
      ↓
capability issuance
      ↓
runtime authority
```

---

## 126. Security Boundaries

Potential isolation boundaries include:

```text
managed object boundary
managed execution context
process/address space
VM
device
remote trust domain
```

The system chooses boundaries according to trust requirements.

---

## 127. Same-Process Capabilities

Capabilities remain useful even inside one process.

They prevent accidental authority propagation through API design and make future isolation possible.

---

## 128. Hardware-Enforced Capabilities

High-risk authority should ultimately map to enforceable low-level mechanisms where possible.

Examples:

```text
address-space mappings
kernel handle tables
IOMMU mappings
IPC endpoint access
```

Managed references alone are insufficient against compromised native code.

---

## 129. Defense in Depth

WitOS security should combine:

```text
managed memory safety
capability discipline
kernel isolation
MMU
IOMMU
cryptographic identity
trusted presentation
resource quotas
```

No single mechanism should be treated as infallible.

---

## 130. Capability API Design

Capabilities should generally use ordinary .NET interfaces.

Security should feel like normal typed programming.

---

## 131. No Parallel Security Type System

WitOS should not invent a separate language-level object system.

Use .NET types, interfaces, generics, async, `IDisposable`, and `IAsyncDisposable` where appropriate.

---

## 132. Capability Serialization

Ordinary serializers must not accidentally turn live capabilities into transferable authority.

---

## 133. Explicit Capability Export

Exporting authority outside the current protection domain should require an explicit API.

This makes trust-boundary crossing visible.

---

## 134. Logging Safety

Default object formatting should avoid exposing sensitive capability representations.

---

## 135. Revocable Proxies

High-level capabilities may be implemented as revocable proxies.

This is appropriate for many managed resources.

---

## 136. Kernel Handles

Low-level local capabilities may map onto opaque kernel handles.

Guessing another numeric value must not grant authority.

---

## 137. Handle Inheritance

Execution contexts should not automatically inherit every parent capability.

Inheritance must follow explicit application/security policy.

---

## 138. Child Process Compatibility

Traditional .NET code may expect child process behavior.

Compatibility profiles may provide conventional inheritance for environment, standard streams, and selected handles without exposing the parent's entire capability set.

---

## 139. Capability Cleanup

When an application instance terminates, transient capabilities are revoked/released automatically.

Persistent grants remain separate.

---

## 140. Lease Cleanup

Resource leases should be tied to liveness where appropriate.

If an application crashes, exclusive camera, GPU reservation, CPU reservation, or temporary storage locks should return to the system.

---

## 141. Security and Application Lifecycle

Lifecycle transitions may change authority.

Example:

```text
Active
    Camera capability valid

Background
    Camera capability revoked

Active again
    capability may be reacquired
```

The application must handle this normally.

---

## 142. Device Handoff

During handoff, capabilities fall into categories:

```text
persistent logical
transferable
reissuable
device-bound
non-transferable
```

---

## 143. Capability Portability

Application code should request semantic capabilities rather than exact hardware resources unless the exact physical resource matters.

This enables migration.

---

## 144. Cross-Device Authority

Persistent grants may be:

```text
device-local
user-wide
organization-wide
resource-provider-wide
```

The scope must be explicit.

---

## 145. User Control

Users should be able to inspect meaningful active and persistent grants.

Security should be understandable without exposing kernel handles.

---

## 146. Revocation UI

Users may revoke persistent grants.

Revocation should immediately affect new acquisitions and, where possible, existing live capabilities.

---

## 147. Application Security Manifest

Applications may declare why they request capabilities.

The manifest does not grant permission.

It assists policy and user understanding.

---

## 148. No Permission Inflation Through Updates

An application update requesting materially new authority should not silently inherit approval for those new capabilities.

---

## 149. Least Privilege as Default SDK Design

WitOS APIs should make narrow authority easier than broad authority.

The easiest API should usually also be the safest API.

---

## 150. Compatibility Is Not Sacrificed

None of these security extensions may require modifications to standard .NET application semantics.

Existing .NET applications should continue using:

```text
System.IO
System.Net
System.Threading
Process
Console
```

through compatibility mappings.

WitOS-specific APIs are additive.

---

## 151. Optional WitOS Assemblies

Advanced functionality should be provided through optional assemblies/packages.

Examples:

```text
OutWit.OS.Resources
OutWit.OS.Security
OutWit.OS.Execution
OutWit.OS.Presentation
OutWit.OS.Compute
OutWit.OS.Storage
```

A plain .NET application need not reference any of them.

---

## 152. Cross-Platform Wit Libraries

Where practical, WitOS extension libraries should run on other operating systems.

For example:

```text
OutWit.OS.Execution
    WitOS backend
    Windows backend
    Linux backend
    generic .NET fallback
```

Capabilities unavailable on the host degrade or report unsupported guarantees.

---

## 153. Capability-Based Portability

Portable WitOS-aware applications must test feature/capability availability, not whether they are running on WitOS.

This allows extension libraries themselves to remain useful on Windows/Linux/macOS.

---

## 154. Security Invariants

1. Identity does not imply authority.
2. Resource identity does not imply authority.
3. Possession of a valid capability represents authority.
4. Authority may be delegated only within the delegator's existing authority.
5. Capability attenuation cannot increase authority.
6. Capabilities must not be forgeable from identifiers.
7. Discovery and access are separate.
8. Applications do not implicitly inherit all authority of the user.
9. The shell is not a security root.
10. Managed safety complements but does not replace isolation.
11. Unsafe code does not imply OS authority.
12. Standard .NET semantics must remain compatible.
13. WitOS-specific security APIs are additive.
14. Native hardware security features improve guarantees but are optional.
15. Local operation must not require a cloud authority.

---

## 155. Deferred Questions

The following require dedicated RFCs:

- distributed capability protocol;
- application packaging and signatures;
- user identity;
- trusted presentation;
- kernel capability representation;
- secure hardware;
- remote revocation;
- attestation policy.

---

## 156. Relationship to Future RFCs

Security influences:

```text
RFC 0006 — IPC & Local/Remote Communication
RFC 0007 — Universal Hardware Interface
RFC 0008 — Storage & Persistent State
RFC 0009 — Presentation, Shell & Input
RFC 0010 — Application Packaging
```

---

## 157. Summary

WitOS does not treat a user, process, application, or administrator flag as a universal container of authority.

Instead:

```text
Identity
    tells the system
    what an entity is

Capability
    tells the system
    what that entity may do
```

Applications receive only the resources they require.

Capabilities may be:

```text
attenuated
delegated
revoked
leased
persistently reissued
transported
audited
```

according to their semantics.

The same model applies from hardware through kernel, drivers, services, applications, plugins, and potentially across devices.

The defining principle is:

> **WitOS security is based on explicit authority, not ambient privilege. Identity establishes who you are; capabilities establish what you can do.**
