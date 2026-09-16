# WitOS
## RFC 0006 — IPC & Local/Remote Communication Model
### Draft v0.1

## 1. Status

Draft.

This document defines the inter-component communication model of WitOS, including:

```text
local IPC
kernel communication primitives
channels
messages
streams
request/response
events
capability transfer
shared memory
zero-copy communication
remote communication
service discovery
connection lifecycle
failure semantics
security
priority propagation
cross-platform compatibility
```

It builds upon:

```text
RFC 0001 — Architecture Concept
RFC 0002 — Resource & Capability Model
RFC 0003 — Application & Lifecycle Model
RFC 0004 — Security, Identity & Capability Delegation
RFC 0005 — Execution, Scheduling & Compute Model
```

The central principles are:

> **Communication is the transfer of data and authority between protection domains, not a special case of process interaction.**

and:

> **Local and remote communication should share semantic concepts without pretending that local and remote execution have identical physical properties.**

---

## 2. Motivation

Traditional operating systems typically expose several unrelated communication mechanisms:

```text
pipes
named pipes
Unix domain sockets
TCP sockets
shared memory
signals
events
message queues
RPC frameworks
window messages
process handles
```

These mechanisms are useful but were developed at different times for different purposes.

Applications therefore often need to distinguish:

```text
in-process call
local process call
service call
network call
remote object
```

even when the logical operation is the same.

At the same time, attempts to completely hide distribution introduce another class of problems:

```text
network latency becomes invisible
partial failure looks unexpected
serialization costs are hidden
retry semantics become ambiguous
remote property access becomes accidentally expensive
```

WitOS therefore aims for a middle ground:

> **Use one conceptual communication model, but preserve observable physical characteristics where they matter.**

---

## 3. Communication Is Not Process-Specific

Communication is not defined fundamentally as:

```text
Process A
   ↓
Process B
```

A more general model is:

```text
Component A
   ↓
Communication Endpoint
   ↓
Channel
   ↓
Communication Endpoint
   ↓
Component B
```

Components may be:

```text
applications
application workers
plugins
drivers
services
system components
remote applications
remote devices
cloud services
```

Whether they share a process or machine is secondary.

---

## 4. Protection Domain

A **protection domain** is an environment across which authority cannot be assumed to transfer automatically.

Possible protection domains include:

```text
managed component boundary
application component
address space
process
sandbox
virtual machine
device
remote trust domain
```

Communication across a protection boundary requires explicit transport and capability semantics.

---

## 5. Communication Endpoint

A communication endpoint is a resource capable of participating in communication.

Conceptually:

```csharp
public interface ICommunicationEndpoint
{
    EndpointId Id { get; }
}
```

An endpoint may represent:

```text
local service
application component
driver endpoint
remote device
remote service
logical application endpoint
```

An endpoint identity does not itself grant access.

---

## 6. Endpoint Identity vs Authority

Knowing:

```text
EndpointId
```

answers:

> Which endpoint is this?

It does not answer:

> Am I allowed to communicate with it?

Communication authority is represented separately through capabilities.

This follows RFC 0002.

---

## 7. Channel

A **Channel** is an active communication relationship between endpoints.

Conceptually:

```text
Endpoint A
   ║
 Channel
   ║
Endpoint B
```

A channel may be:

```text
local
remote
reliable
unreliable
ordered
unordered
stream-oriented
message-oriented
authenticated
encrypted
shared-memory backed
network backed
```

These characteristics are explicit properties.

---

## 8. Channel as a Resource

A channel is itself a resource.

Therefore it may have:

```text
identity
capabilities
lifetime
ownership
security policy
locality
performance characteristics
```

Applications may receive a channel capability from another component rather than discover or create the channel themselves.

---

## 9. Channel Creation

A channel may be created by:

```text
explicit connection
service activation
capability delegation
resource acquisition
application launch
driver registration
remote session establishment
```

Channel creation may require authorization.

---

## 10. Channel Lifetime

A channel has a finite lifetime.

Possible termination reasons include:

```text
explicit close
application exit
provider crash
capability revocation
network failure
device disconnect
timeout
session termination
remote reboot
```

Applications must treat channel loss as a normal possibility.

---

## 11. Kernel Communication Primitive

The WitOS kernel should provide a minimal communication primitive.

Conceptually this may be:

```text
Channel
Endpoint
Message
Wait
Signal
CapabilityTransfer
SharedMemoryReference
```

The kernel should not understand:

```text
RPC method names
C# interfaces
JSON
HTTP
application services
business protocols
```

These belong above the kernel.

---

## 12. Minimal Kernel IPC

A possible low-level kernel model:

```text
CreateChannel
SendMessage
ReceiveMessage
Wait
Close
TransferCapability
AttachSharedRegion
```

This is illustrative rather than normative.

The exact ABI will be defined later.

---

## 13. Kernel Messages

A kernel message should contain only a small amount of transport metadata plus payload references.

Conceptually:

```text
Message
 ├── small inline payload
 ├── optional shared buffers
 └── optional transferred capabilities
```

Large data should not require copying through the kernel unnecessarily.

---

## 14. Message-Oriented Semantics

The fundamental local IPC primitive should preferably preserve message boundaries.

This simplifies:

```text
capability transfer
request identification
event delivery
atomic metadata
small control operations
```

Stream abstractions can be built above messages.

---

## 15. Streams

Stream communication remains important.

The communication stack should support:

```text
byte streams
object streams
media streams
record streams
```

A stream may be implemented using:

```text
shared memory
kernel channels
TCP
QUIC
WebSocket
other transports
```

Applications should not need transport-specific code unless required.

---

## 16. Standard .NET Streams

Standard .NET `Stream` must remain usable.

WitOS APIs may expose adapters:

```csharp
Stream stream = channel.AsStream();
```

where stream semantics are appropriate.

---

## 17. Messages and Streams Are Complementary

WitOS should not force every communication workload into one abstraction.

Use:

```text
Message
```

when boundaries matter.

Use:

```text
Stream
```

when continuous ordered bytes are natural.

Use:

```text
Shared Buffer
```

when large zero-copy data exchange is needed.

---

## 18. Request/Response

A higher-level request/response abstraction may be layered over channels.

Example:

```text
Request
   ↓
Service
   ↓
Response
```

Requests should support:

```text
correlation ID
cancellation
deadline
error result
optional capability transfer
```

---

## 19. Asynchronous by Default

External communication should generally be asynchronous.

Example:

```csharp
Response response =
    await endpoint.SendAsync(
        request,
        cancellationToken);
```

This is necessary because the endpoint may be:

```text
same process
another process
another device
another network
```

---

## 20. Synchronous Compatibility

Synchronous APIs may exist for compatibility or specialized cases.

Native WitOS APIs should not assume synchronous low-latency communication unless explicitly guaranteed.

---

## 21. Events

Endpoints may expose event streams.

Examples:

```text
resource changed
device connected
application state changed
render complete
job progress
network state
```

Event delivery is a communication pattern layered over channels.

---

## 22. Event Subscriptions

Subscriptions should be explicit resources.

Conceptually:

```csharp
await using var subscription =
    await service.SubscribeAsync(...);
```

Subscription lifetime determines event-delivery authority.

---

## 23. Subscription Cleanup

If the consumer disappears, subscriptions should be reclaimable automatically.

The provider must not accumulate permanent dead subscriptions.

---

## 24. Event Backpressure

A producer may emit events faster than a consumer can process them.

The API must define behavior such as:

```text
buffer
drop oldest
drop newest
coalesce
block producer
disconnect
```

The choice depends on event semantics.

---

## 25. Backpressure

Backpressure is a first-class communication concern.

Without backpressure, one component can exhaust another component's:

```text
memory
queue capacity
CPU
network bandwidth
```

Channels therefore require flow-control semantics.

---

## 26. Queue Limits

Message queues should generally have bounded capacity.

When limits are reached, behavior may include:

```text
await capacity
reject message
drop according to policy
apply producer throttling
```

Unbounded hidden queues should be avoided.

---

## 27. Cancellation

Long-running communication operations must support cancellation.

Standard .NET `CancellationToken` should be used where practical.

Cancellation may propagate across a channel when supported.

---

## 28. Deadlines

A request may carry a deadline.

Example:

```text
must complete before T
```

A deadline is often more useful across distributed systems than a relative timeout because it can propagate through dependency chains.

---

## 29. Timeout vs Deadline

A timeout describes:

```text
how long I am willing to wait
```

A deadline describes:

```text
the latest acceptable completion time
```

Both may be supported.

Distributed systems should prefer deadline propagation where clock assumptions permit.

---

## 30. Capability Transfer

IPC must support capability transfer.

Example:

```text
Application A
    │
    │ message + document capability
    ▼
Application B
```

After successful transfer, B receives authority described by that capability.

---

## 31. Capability Transfer Must Be Explicit

Sending a resource identifier must not grant authority.

Bad:

```text
send ResourceId
→ receiver somehow gains access
```

Correct:

```text
send ResourceId
+
explicit delegated capability
```

---

## 32. Local Capability Transfer

Local capability transfer may be enforced by the kernel.

Conceptually:

```text
Sender capability table
        ↓
kernel validates delegation
        ↓
Receiver capability table
```

The receiver obtains a new valid handle/reference.

---

## 33. Atomic Capability Transfer

Where practical, message delivery and capability transfer should be atomic.

The system should avoid states where a message is delivered but its capability is not transferred, or a capability is transferred but the corresponding message is lost, for local reliable IPC.

---

## 34. Capability Attenuation During Transfer

A sender may delegate a weaker capability than it possesses.

Example:

```text
Sender:
    read/write document

Receiver:
    read-only document
```

The transfer mechanism should naturally support attenuation.

---

## 35. Capability Move vs Copy

Delegation may mean:

```text
share
copy authority
move authority
```

A moved capability causes the sender to lose its authority after successful transfer.

This may be useful for exclusive resources.

---

## 36. Exclusive Capability Transfer

Some capabilities may require ownership transfer.

Example:

```text
exclusive hardware queue
temporary write lease
single-consumer stream
```

The transport must respect resource semantics.

---

## 37. Remote Capability Transfer

Remote capability transfer cannot rely on local kernel handles.

It requires a remote authority representation.

Possible implementations include:

```text
signed capability token
session-bound reference
cryptographic capability
provider-issued opaque token
```

The exact wire format is deferred.

---

## 38. Remote Capability Semantics

A remote capability may contain or reference:

```text
resource identity
issuer
allowed operations
recipient constraints
expiration
session binding
delegation constraints
routing information
revocation state
```

The programming model should remain conceptually compatible with local capabilities.

---

## 39. Capability Confidentiality

Some remote capability representations are bearer authority.

Therefore they may need confidentiality protection.

They must not accidentally appear in:

```text
logs
exception strings
telemetry
clipboard
ordinary serialization
```

---

## 40. Replay Protection

Remote communication may require protection against replay.

Possible mechanisms include:

```text
session binding
nonces
sequence numbers
short-lived capability tokens
one-use tokens
```

The exact strategy depends on transport and capability type.

---

## 41. Revocation

A remote capability may be revoked.

Possible approaches include:

```text
short expiration
provider-side state
revocation version
active session invalidation
revocation service
```

No single universal mechanism is required.

---

## 42. Offline Capability Use

Some remote/persistent capabilities may be valid offline.

Others may require live validation.

This must be part of the capability contract.

---

## 43. Shared Memory

Local IPC should support capability-controlled shared memory.

Example:

```text
Producer
   │
 Shared Buffer
   │
Consumer
```

The kernel manages mapping authority.

---

## 44. Shared Memory Is a Capability

Knowing a memory address in one process must not grant shared access.

A process receives an explicit capability to map the region.

---

## 45. Shared Memory Permissions

Mappings may differ:

```text
read-only
write-only where meaningful
read/write
copy-on-write
executable where explicitly permitted
```

The sender may attenuate mapping authority.

---

## 46. Zero-Copy

Zero-copy communication should be a first-class optimization.

Useful workloads include:

```text
video frames
audio buffers
GPU data
large files
network packets
simulation results
database pages
```

---

## 47. Zero-Copy Is Not Mandatory

Small messages should remain simple.

The system must not require complex shared-memory setup for ordinary RPC calls.

A good implementation chooses the efficient path based on payload characteristics.

---

## 48. Buffer Ownership

Shared buffers require explicit ownership semantics.

Possible models:

```text
borrowed
shared immutable
single writer
transfer ownership
reference counted
leased
```

Ambiguous ownership causes synchronization and security problems.

---

## 49. Immutable Shared Buffers

Immutable shared data is particularly valuable.

A producer may publish a read-only buffer to many consumers without duplication.

---

## 50. Mutable Shared Buffers

Mutable shared memory requires synchronization policy.

WitOS should not implicitly invent one consistency model for all workloads.

Applications or higher-level libraries choose appropriate synchronization.

---

## 51. DMA and Shared Buffers

Some shared buffers may also be visible to devices.

This requires integration with:

```text
DMA capabilities
IOMMU mappings
device ownership
cache coherence rules
```

Such buffers are privileged resources.

---

## 52. Cross-Process Synchronization

Synchronization primitives may be shareable through capability transfer.

Examples:

```text
event
semaphore
waitable signal
queue notification
```

The kernel should provide a minimal set.

---

## 53. Avoid Excessive Kernel Primitives

High-level synchronization should preferably remain in managed code.

The kernel should provide only primitives necessary for efficient blocking, waking, and protection-boundary crossing.

---

## 54. Service

A **service** is a component exposing one or more communication contracts.

A service may run:

```text
in process
in another process
on another machine
inside a driver host
in the cloud
```

The contract is logically distinct from placement.

---

## 55. Service Identity

A service has a logical identity.

This identity may survive:

```text
process restart
machine migration
endpoint change
transport change
```

Service identity should not be equivalent to a port number or process ID.

---

## 56. Service Descriptor

Discovery may return a descriptor containing:

```text
service identity
contract types
version
characteristics
locality
trust properties
available transports
```

The descriptor does not itself imply communication authority.

---

## 57. Service Discovery

Service discovery answers:

> What services are available?

It does not answer:

> Which services may I use?

Authority remains capability-based.

---

## 58. Discovery Scope

Service discovery may be scoped to:

```text
current application
current device
current session
local network
organization
explicit resource domain
```

There need not be one global namespace.

---

## 59. Service Activation

Connecting to a service may activate it.

Example:

```text
Application requests service
        ↓
service not running
        ↓
WitOS starts service
        ↓
channel established
```

This avoids requiring all services to remain continuously resident.

---

## 60. Service Restart

A service may crash and restart.

Clients should be able to distinguish temporary endpoint loss from logical service permanently unavailable where the service contract supports reconnection.

---

## 61. Logical Service vs Physical Endpoint

The logical service may remain the same while its communication endpoint changes.

This is important for restart and migration.

---

## 62. Connection

A connection represents a communication relationship to a specific active endpoint or service session.

A service may outlive a particular connection.

---

## 63. Connection State

Possible states:

```text
Connecting
Connected
Degraded
Reconnecting
Closed
Failed
```

Not every transport requires every state.

---

## 64. Locality

Channels expose locality characteristics.

Possible values:

```text
InProcess
LocalMachine
LocalDevice
LocalNetwork
RemoteNetwork
Unknown
```

Applications may ignore this unless it matters.

---

## 65. Locality Must Remain Observable

A framework should not hide locality when it affects:

```text
latency
failure
bandwidth
cost
security
data residency
```

This is especially important for RPC-like APIs.

---

## 66. Same Contract, Different Locality

The same semantic interface may be implemented locally or remotely.

Example:

```csharp
IImageProcessor processor;
```

Possible providers:

```text
in-process implementation
local service
LAN workstation
cloud service
```

The application may use one contract while still being able to inspect characteristics.

---

## 67. Remote Calls Are Not Method Calls Physically

A remote interface may look like a C# interface.

But implementation must acknowledge:

```text
serialization
latency
failure
versioning
cancellation
transport security
```

The system must not encourage accidental high-frequency remote property access.

---

## 68. RPC

RPC is a higher-level communication model built over channels.

It should provide:

```text
typed requests
typed responses
method invocation
events
streaming
error mapping
cancellation
contract versioning
```

RPC does not belong in the kernel.

---

## 69. Relationship to WitRPC

Existing WitRPC is a natural candidate for high-level communication in the OutWit ecosystem.

WitOS should not hard-code WitRPC semantics into the kernel.

Instead:

```text
WitRPC
   ↓
communication provider
   ↓
WitOS channel / stream / shared-buffer APIs
```

This allows WitRPC to use optimized WitOS transports while remaining a cross-platform independent project.

---

## 70. Existing OutWit.Communication

Existing packages such as:

```text
OutWit.Communication
OutWit.Communication.Server
```

may gain WitOS-specific providers.

The core OS API may use:

```text
OutWit.OS.Communication
```

for OS-level abstractions.

These packages should not unnecessarily duplicate each other.

---

## 71. Layering

Preferred layering:

```text
Application contract
        ↓
WitRPC / other RPC framework
        ↓
OutWit.OS.Communication
        ↓
WitOS kernel channels
        ↓
hardware/network transport
```

For ordinary network applications:

```text
Application
    ↓
System.Net / Socket
    ↓
WitOS networking
```

Both remain valid.

---

## 72. Typed Contracts

Managed communication should strongly prefer typed contracts.

Example:

```csharp
public interface IImageProcessor
{
    ValueTask<ImageResult> ProcessAsync(
        ImageInput input,
        CancellationToken cancellationToken = default);
}
```

The runtime may implement this contract locally, through IPC, or through network RPC depending on provider.

---

## 73. Contract vs Transport

Application contracts must not encode transport details unnecessarily.

Avoid:

```text
HttpImageProcessor
PipeImageProcessor
TcpImageProcessor
```

unless transport is itself relevant.

Prefer:

```text
IImageProcessor
```

with replaceable transport providers.

---

## 74. Serialization

Remote and isolated communication may require serialization.

Serialization format is transport/provider policy.

Possible formats include:

```text
MemoryPack
MessagePack
Protocol Buffers
binary custom format
JSON
shared-memory representation
```

The OS does not mandate one universal application serializer.

---

## 75. Serialization Is Avoided When Possible

Local trusted communication may use:

```text
shared buffers
direct managed calls
binary messages
```

without unnecessary serialization.

The communication stack should optimize according to locality.

---

## 76. Cross-Architecture Communication

Remote communication may cross:

```text
x64
ARM64
RISC-V
big/little endian architectures
different pointer sizes
```

Therefore wire protocols must never depend on native in-memory layout unless explicitly scoped to compatible peers.

---

## 77. Endianness

Portable serialized protocols must define byte order or use formats that abstract it.

Native zero-copy layouts may require architecture compatibility checks.

---

## 78. Pointer Values

Raw process pointers must never be meaningful across protection domains.

Shared-memory references must use offsets or explicitly mapped structures.

---

## 79. Contract Versioning

Communication contracts evolve.

Providers and clients should be able to negotiate compatible versions.

---

## 80. Additive Evolution

Where practical, contracts should favor additive evolution:

```text
new optional members
new message fields
new capability interfaces
```

rather than breaking existing clients.

---

## 81. Unknown Fields

Serialization formats intended for version evolution should tolerate unknown optional fields where possible.

---

## 82. Interface Evolution

.NET interface evolution requires care.

Versioned contract interfaces or source-generated negotiation may be preferable to breaking existing interface binaries.

---

## 83. Version Negotiation

Endpoints may exchange:

```text
contract ID
supported versions
feature set
```

during connection setup.

The highest mutually compatible contract may be selected.

---

## 84. Feature Negotiation

Applications should prefer feature negotiation over peer-version checks.

Good:

```text
SupportsStreaming = true
```

Bad:

```text
if ServerVersion >= 4.2
```

where the actual requirement is a capability.

---

## 85. Reliability

A channel may expose reliability semantics.

Examples:

```text
BestEffort
ReliableUntilDisconnect
Durable
AtMostOnce
AtLeastOnce
```

Exactly-once execution should not be promised casually.

---

## 86. Exactly-Once Warning

Across failures, truly exactly-once semantics are difficult.

A framework should distinguish:

```text
message delivery
operation execution
result delivery
```

Retrying a lost response may execute an operation twice.

---

## 87. Idempotency

Operations intended for automatic retry should preferably be idempotent or carry an idempotency key.

---

## 88. Retry Policy

Automatic retry is a higher-level policy.

The kernel should not automatically retry failed logical requests.

A communication framework may retry according to explicit operation semantics.

---

## 89. Reconnect

Remote and restartable local services may support reconnect.

Reconnect may restore logical service session, subscriptions, leases, or capabilities only where explicitly supported.

---

## 90. Reconnect Does Not Imply State Preservation

A reconnected endpoint may represent a restarted service with lost transient state.

Clients must not assume otherwise.

---

## 91. Session

A communication session may hold:

```text
authentication state
negotiated features
capability bindings
subscriptions
sequence numbers
encryption keys
```

Sessions have explicit lifecycle.

---

## 92. Session Resumption

Remote protocols may support session resumption.

The exact security design is transport-specific.

Resumption must not accidentally restore revoked authority.

---

## 93. Authentication

Remote peers may need mutual authentication.

Possible identities include:

```text
application identity
device identity
user identity
service identity
organization identity
```

Authentication does not itself define granted capabilities.

---

## 94. Authorization

After peer authentication, policy determines which capabilities are issued.

This remains consistent with RFC 0004.

---

## 95. Encryption

Communication crossing untrusted boundaries should support authenticated encryption.

The exact cryptographic protocol should use established standards rather than custom cryptography.

---

## 96. Local Encryption

Communication within one machine may not require encryption when MMU/kernel isolation already provides confidentiality.

Applications with stronger threat models may still request encrypted channels.

---

## 97. Channel Trust

A channel may expose trust properties.

Example:

```text
same process
kernel-isolated local
authenticated local
authenticated encrypted remote
organization-trusted remote
```

Applications may require a minimum trust characteristic.

---

## 98. Channel Characteristics

A channel may expose:

```text
locality
latency estimate
bandwidth
reliability
ordering
encryption
authentication
maximum message size
shared-memory support
streaming support
```

Applications should avoid depending on these unless necessary.

---

## 99. Ordering

A channel may guarantee:

```text
total message ordering
per-stream ordering
per-request ordering
no ordering
```

The guarantee must be explicit.

---

## 100. Multiplexing

One physical channel may host multiple logical streams or service contracts.

This reduces connection overhead.

Multiplexing should preserve appropriate isolation and flow control.

---

## 101. Logical Streams

A multiplexed connection may contain:

```text
request stream
event stream
data stream
control stream
```

Each may have separate flow control.

---

## 102. Head-of-Line Blocking

Communication transports should avoid unnecessary head-of-line blocking where parallel streams are expected.

The exact transport implementation remains provider-specific.

---

## 103. Large Payloads

Large payload transfer should support:

```text
streaming
chunking
shared memory
zero-copy
compression where useful
```

A large byte array should not necessarily be serialized into one enormous message.

---

## 104. Batching

High-frequency operations may support batching.

Example:

```text
100 small updates
```

may be sent as one batch rather than 100 round trips.

This is particularly important for remote communication.

---

## 105. Chatty APIs

Contract designers must avoid APIs that look cheap locally but become expensive remotely.

Example:

```csharp
for (...)
{
    x = remoteObject.Items[i].Name;
}
```

may imply thousands of remote calls.

Framework tooling should make such patterns visible where possible.

---

## 106. Local Fast Path

When client and provider are in the same protection domain, an implementation may use direct invocation.

Conceptually:

```text
typed contract
   ↓
same-process provider
   ↓
direct call
```

No transport serialization is required.

---

## 107. Local IPC Fast Path

Across local address spaces:

```text
typed contract
    ↓
generated proxy
    ↓
kernel channel
    ↓
shared buffers
```

may provide low overhead.

---

## 108. Remote Path

Across devices:

```text
typed contract
    ↓
proxy
    ↓
serialization
    ↓
encrypted transport
    ↓
remote channel
```

The application contract remains the same where semantics permit.

---

## 109. Transparent Optimization, Not Transparent Physics

The framework may optimize transport automatically.

It must not hide meaningful differences in:

```text
failure
latency
bandwidth
authority
```

This is a core WitOS principle.

---

## 110. Provider Placement

A service provider may be:

```text
in-process
local service
remote node
cloud provider
```

Resource resolution may select placement according to application constraints.

---

## 111. Placement Constraints

A consumer may require:

```text
same device
same trust domain
local preferred
maximum latency
minimum bandwidth
offline capable
```

The resolver uses these constraints during provider selection.

---

## 112. Service Migration

A service may migrate to another execution environment.

Logical service identity may remain stable while channels reconnect.

---

## 113. Resource Mobility

A resource referenced through a service may also move.

Clients should prefer logical resource identity rather than embedding machine addresses.

---

## 114. Routing

Distributed resource/service resolution may require routing.

Routing information should not become part of stable application identity.

---

## 115. Addressing

Addresses answer:

> Where can this endpoint currently be reached?

Identity answers:

> What logical endpoint/service is this?

Capabilities answer:

> What am I authorized to do?

These must remain distinct.

---

## 116. Human-Readable Names

Human-readable or network-visible names may exist.

They remain discovery/addressing mechanisms, not authority tokens.

---

## 117. Application Activation Through Communication

A message or connection request may activate an application.

Example:

```text
incoming resource request
      ↓
WitOS activates provider application
      ↓
channel established
```

This integrates communication with RFC 0003 lifecycle semantics.

---

## 118. Suspended Services

A suspended service may be reactivated on demand.

Clients should not need to care whether the provider was already running.

---

## 119. Activation Cost

A descriptor may expose whether connecting is likely to trigger expensive activation.

This may inform scheduling or batching.

---

## 120. Failure Semantics

Communication APIs must expose failure clearly.

Possible failures:

```text
endpoint unavailable
service stopped
capability revoked
transport lost
authentication failed
protocol incompatible
deadline exceeded
resource moved
peer crashed
```

---

## 121. Local Failure Is Possible Too

Local IPC is not failure-free.

A local provider may crash, hang, be terminated, lose authority, or restart.

Local calls therefore also require robust failure semantics.

---

## 122. Partial Failure

Distributed communication introduces partial failure:

```text
client alive
server alive
network broken
```

or:

```text
request executed
response lost
```

Frameworks must not hide these cases behind generic application exceptions.

---

## 123. Error Classification

Errors should distinguish at least:

```text
application error
authorization error
protocol error
transport error
availability error
timeout/deadline error
cancellation
```

This supports sensible recovery.

---

## 124. Remote Exception Handling

Arbitrary remote exception objects should not automatically cross trust boundaries.

A remote error should be represented as a defined contract error.

Detailed stack traces may be available only under debugging/trusted conditions.

---

## 125. Cancellation Propagation

If client cancellation occurs, the server may receive cancellation notification.

Cancellation is best-effort unless the operation contract states stronger semantics.

---

## 126. Cancellation Does Not Roll Back Automatically

Canceling a request does not imply that already-executed side effects are undone.

Transactional rollback is a separate contract.

---

## 127. Priority Propagation

Communication interacts with RFC 0005 scheduling.

If a high-priority task depends synchronously on a lower-priority service, uncontrolled priority inversion may occur.

WitOS should allow controlled priority propagation.

---

## 128. Priority Donation

A request may temporarily donate execution priority to the provider handling it.

Conceptually:

```text
High-priority client
        ↓
normal-priority service
        ↓
temporary scheduling boost
```

The boost ends when the dependency ends.

---

## 129. Priority Propagation Is Bounded

A client must not be able to escalate arbitrary services to unrestricted real-time priority.

Propagation is constrained by client authority, server policy, maximum service class, and system policy.

---

## 130. Dependency Chains

Priority/deadline information may propagate through multiple services.

Example:

```text
UI
 ↓
Document Service
 ↓
Storage Service
 ↓
Driver
```

The system may propagate urgency sufficiently to avoid inversion.

---

## 131. Deadline Propagation

A request deadline may propagate through dependent calls.

A downstream service can avoid work that cannot complete before the remaining deadline.

---

## 132. Resource Accounting

Communication work should be attributable to the responsible application or request where practical.

This helps avoid situations where a low-privilege application causes large amounts of system-service CPU usage that appears unrelated to it.

---

## 133. Cross-Domain Resource Accounting

The system may account:

```text
CPU
memory
network
storage I/O
GPU work
```

triggered by a request back to the initiating execution/resource context.

Exact accounting policy is deferred.

---

## 134. Driver Communication

Managed drivers may expose communication endpoints.

Applications normally communicate with higher-level device services rather than drivers directly.

---

## 135. Driver Fast Paths

High-performance devices may expose shared-memory queues or mapped buffers under controlled capabilities.

The ordinary driver/service contract remains available for control operations.

---

## 136. Network Stack

The WitOS network stack is itself composed of services/resources.

Standard socket APIs operate above this stack.

Higher-level remote channels may also use it.

---

## 137. Socket Compatibility

Existing .NET applications using:

```text
Socket
TcpClient
UdpClient
NetworkStream
HttpClient
```

must continue to work with standard semantics.

WitOS communication APIs are additive.

---

## 138. Local IPC Compatibility

Where useful, compatibility layers may provide named pipes, local sockets, or pipes over WitOS communication primitives.

These are compatibility views, not necessarily the native IPC model.

---

## 139. Standard Input/Output

Console input/output may use communication channels internally.

Applications continue to use:

```text
Console.In
Console.Out
Console.Error
```

normally.

---

## 140. Process Launch Communication

Parent/child standard stream wiring may be represented through channels.

This preserves normal .NET process semantics in compatibility mode.

---

## 141. Remote Terminal

A remote terminal may use the same channel abstractions as other streamed communication.

No special kernel terminal protocol is required.

---

## 142. Notifications

System notifications may be delivered through capability-controlled communication endpoints.

The notification service is not hardwired into every application process.

---

## 143. Logging

Logging providers may use channels, streams, or remote communication.

Logging APIs should avoid blocking critical execution paths unnecessarily.

---

## 144. Telemetry

Telemetry must not accidentally serialize capability tokens, secrets, or sensitive IPC payloads.

Security rules apply equally to local and remote diagnostics.

---

## 145. Auditing

Security-sensitive communication events may be audited.

Examples:

```text
channel opened
capability delegated
remote peer authenticated
authority revoked
service activation
```

Audit verbosity is policy-controlled.

---

## 146. IPC Namespace

WitOS should avoid requiring one global string namespace for all IPC.

Services and endpoints should primarily be discovered through resource/service mechanisms.

Human-readable names may exist as convenience views.

---

## 147. No Magic Global Ports

Applications should not require permanent globally-known numeric ports for local system services.

Logical discovery should be preferred.

Network protocols may still use standard ports where interoperability requires them.

---

## 148. Brokered Communication

Some communication may be brokered through a system service.

This can support:

```text
sandbox isolation
routing
capability validation
remote transport
service activation
```

Broker usage is implementation-specific and not required for all channels.

---

## 149. Direct Communication

Where policy allows, two components may communicate directly after capability establishment.

The system should not force all high-bandwidth traffic through a central broker.

---

## 150. Broker Control Plane, Direct Data Plane

A useful pattern is:

```text
Broker:
    discovery
    authorization
    channel setup

Direct Channel:
    actual high-volume data
```

This reduces central bottlenecks.

---

## 151. Local Communication Security

Kernel isolation should protect local channel endpoints from unauthorized access.

Guessing endpoint identifiers or handles must not allow connection.

---

## 152. Remote Communication Security

Remote communication crossing trust boundaries should require explicit authentication and authorization appropriate to the resource.

---

## 153. Trust Boundary Crossing

Moving from local to remote communication may change:

```text
threat model
encryption requirement
identity requirement
latency
failure semantics
cost
```

Applications may constrain whether such a transition is allowed.

---

## 154. Network Is Optional

A local WitOS system must remain fully functional without network connectivity.

Core local IPC must not depend on:

```text
Internet
cloud identity
remote broker
central service registry
```

---

## 155. Local Service Discovery Is Local

Local service discovery should function without external infrastructure.

Distributed discovery is an optional extension.

---

## 156. Distributed Discovery

Distributed environments may use:

```text
LAN discovery
organization registry
explicit peers
federated resource catalog
cloud directory
```

No one mechanism is mandated globally.

---

## 157. Privacy of Discovery

A device should not automatically advertise every service or resource to every nearby peer.

Discovery itself is security-sensitive.

---

## 158. Service Visibility

Policy may limit service visibility by:

```text
application
user
session
device
network
organization
trust domain
```

---

## 159. Communication Capabilities

High-level capabilities may include:

```text
Connect
Accept
Send
Receive
Subscribe
Publish
Delegate
Administer
```

A service need not grant all operations to every client.

---

## 160. Listening Authority

Opening a public network listener is a stronger capability than creating an outbound connection.

These should be represented separately.

---

## 161. Publish Authority

Publishing a discoverable service may require explicit authority.

A sandboxed application should not automatically become globally discoverable.

---

## 162. Local Client Authority

Connecting to an already-authorized local service should not necessarily require broad networking permission.

Local IPC authority and Internet authority are distinct.

---

## 163. Remote Client Authority

An application may receive capability to connect only to a specific service, organization domain, or resource provider without unrestricted network access.

---

## 164. Data Movement Authority

Holding a read capability to data and a network capability does not necessarily mean the application should be allowed to export the data everywhere.

Future policy may include data-flow constraints.

---

## 165. Trusted Compute Communication

Sensitive resources may require communication only with endpoints meeting specified trust properties.

This integrates with RFC 0004 trust domains.

---

## 166. Remote Resource Proxy

A remote resource may be represented locally by a proxy implementing the resource interface.

The proxy remains a capability.

It does not imply that remote access is free or failureless.

---

## 167. Proxy Lifetime

When the underlying remote capability expires or disconnects, the proxy becomes invalid or degraded.

Applications should receive explicit failure.

---

## 168. Proxy Identity

Multiple proxies may represent the same logical remote resource.

Resource identity should therefore remain separate from proxy object identity.

---

## 169. Local Proxy Optimization

If a previously remote resource becomes local, the provider may replace or optimize the communication path without changing logical resource identity.

---

## 170. Application Handoff

During application handoff, communication endpoints may need rebinding.

Example:

```text
Application moves to Tablet
        ↓
local document service disappears
        ↓
remote or equivalent provider acquired
```

The logical contract remains.

---

## 171. Service Handoff

Services may similarly move while clients reconnect through logical service identity.

---

## 172. Network Partition

During a network partition, remote resources may become:

```text
Unavailable
Degraded
ReadOnly
Cached
```

depending on resource semantics.

Communication APIs should expose these states rather than hang indefinitely.

---

## 173. Cached Communication

Some services may provide offline caches or queued operations.

This is a service-level feature, not a universal property of channels.

---

## 174. Store-and-Forward

Some communication patterns may deliberately support disconnected operation.

Examples:

```text
email-like messaging
telemetry
background sync
job submission
```

These differ from live RPC.

---

## 175. Messaging Services

Durable messaging may be implemented as a higher-level resource.

The kernel channel itself need not provide persistence.

---

## 176. Pub/Sub

Publish/subscribe is a higher-level communication pattern.

Topics or subscriptions are resources governed by capabilities.

---

## 177. Broadcast

Broadcast should be used carefully.

Global unrestricted broadcast can create security leaks, performance issues, and tight coupling.

Scoped publish/subscribe is preferable.

---

## 178. In-Process Communication

Not all logical communication requires IPC.

Components in the same trust/execution domain may use normal C# calls.

A provider abstraction may select this automatically.

---

## 179. Communication Abstraction Must Not Penalize Local Calls

Using a typed service abstraction should not necessarily imply serialization or kernel transition when both components are local and trusted.

---

## 180. Source Generation

Source generators may create:

```text
proxies
dispatchers
serializers
contract metadata
```

This fits existing OutWit patterns and avoids runtime reflection overhead where appropriate.

---

## 181. NativeAOT

Communication tooling should remain compatible with NativeAOT where practical.

Generated metadata is preferable to reflection-heavy runtime discovery for system components.

---

## 182. Reflection

CoreCLR applications may still use reflection-based dynamic contracts if desired.

The OS model does not require NativeAOT restrictions for ordinary applications.

---

## 183. Generic Host Independence

WitOS communication services should not require ASP.NET Core or Kestrel merely to expose a local service.

Higher-level web servers remain optional applications/frameworks.

---

## 184. HTTP

HTTP remains an important interoperability protocol.

It is not the native IPC model of WitOS.

Applications may expose HTTP services through ordinary .NET networking.

---

## 185. WebSocket and QUIC

Transports such as WebSocket and QUIC may be used by providers.

The communication model remains transport-neutral.

---

## 186. Transport Selection

A communication framework may choose transport based on:

```text
locality
latency
security
payload size
stream requirements
peer capability
network availability
```

The application contract should not usually care.

---

## 187. Transport Negotiation

Peers may negotiate the best mutually supported transport.

Example:

```text
same machine:
    shared memory + kernel channel

LAN:
    QUIC

browser peer:
    WebSocket
```

This should be transparent at the provider layer.

---

## 188. Fallback

If an optimized transport is unavailable, a provider may fall back to a more general transport.

The semantic contract remains.

---

## 189. Transport Guarantees

Fallback must not silently weaken required guarantees.

If the application requires:

```text
local only
encrypted
ordered
zero-copy
```

a fallback that cannot provide them must be rejected.

---

## 190. Performance Characteristics

Communication resources may expose approximate:

```text
latency
bandwidth
maximum message size
copy cost
serialization cost
```

These values may change over time.

---

## 191. Performance Hints

Applications may request:

```text
low latency
high throughput
low memory
low power
bulk transfer
interactive
```

Providers may choose appropriate transport behavior.

---

## 192. QoS

Communication may eventually support quality-of-service reservations:

```text
minimum bandwidth
maximum latency target
priority class
```

These are resource contracts and may be denied.

---

## 193. Interaction with Scheduling

A communication request may carry execution intent.

The receiving service can schedule handling appropriately within its own resource limits.

---

## 194. Interaction with Resource Resolver

Communication providers are resources.

The resolver may choose among local, remote, cached, or replicated providers based on requirements.

---

## 195. Interaction with Security

Opening or receiving a channel requires capability checks.

Capability transfer follows RFC 0004 rules.

Communication does not bypass the security model.

---

## 196. Interaction with Application Lifecycle

Application suspension may:

```text
close channels
pause channels
keep selected channels alive
delegate communication to background service
```

according to capability and application policy.

---

## 197. Persistent Connections

Persistent connections should not prevent suspension automatically.

Applications must explicitly request background communication authority.

---

## 198. Background Communication

Examples:

```text
audio streaming
message synchronization
download
service monitoring
remote computation
```

These require appropriate background resource capabilities.

---

## 199. Wake on Communication

Some applications may be activated by incoming communication.

This must be explicitly registered and capability-controlled.

---

## 200. Denial-of-Service Protection

Communication endpoints must resist resource exhaustion.

Potential controls include:

```text
connection limits
queue limits
message-size limits
CPU quotas
bandwidth limits
rate limits
authentication before expensive work
```

---

## 201. Message Size Limits

Every transport should have explicit or discoverable practical message limits.

Large data should use streaming or shared buffers.

---

## 202. Admission Control

Services may reject new channels or requests under resource pressure.

This should produce a meaningful availability response rather than system instability.

---

## 203. Fairness

One client should not be able to monopolize a shared service's communication capacity without explicit reservation.

---

## 204. Resource Reservation for Communication

High-performance applications may request reserved:

```text
network bandwidth
queue capacity
shared-memory buffers
service concurrency
```

This integrates with the broader resource model.

---

## 205. Local vs Remote Error Equivalence

Semantic errors should remain similar across local and remote providers.

Transport-specific failures remain distinguishable.

---

## 206. Testing

Communication providers should support conformance testing.

Categories include:

```text
message delivery
streaming
cancellation
capability transfer
ordering
backpressure
disconnect
reconnect
version negotiation
security
fallback
```

---

## 207. Fault Injection

The development framework should support testing:

```text
latency
packet loss
disconnects
service restart
capability revocation
partial response
queue saturation
```

Distributed failure should be testable locally.

---

## 208. Loopback Provider

A loopback/in-process provider should make communication contracts easy to test without kernel IPC or networking.

---

## 209. Local IPC Provider

A native WitOS local provider should exercise kernel channels and capability transfer.

---

## 210. Remote Test Provider

A remote provider should validate actual distributed semantics, not merely in-process serialization.

---

## 211. Cross-Platform Providers

Communication libraries should remain cross-platform where practical.

Possible providers:

```text
WitOS
Windows
Linux
macOS
Generic .NET
```

Advanced features degrade explicitly.

---

## 212. Windows Backend

A Windows backend may use combinations of named pipes, shared memory, sockets, and native synchronization depending on required semantics.

---

## 213. Linux Backend

A Linux backend may use combinations of Unix domain sockets, shared memory, eventfd-like primitives, and sockets depending on required semantics.

---

## 214. Generic Backend

A generic backend may use TCP, Stream, Socket, and managed queues for broad portability.

---

## 215. Cross-Platform Capability Semantics

Non-WitOS hosts may not enforce all capability semantics natively.

The backend must report weaker guarantees honestly.

---

## 216. Compatibility Invariants

1. Communication authority is separate from endpoint identity.
2. A ResourceId or EndpointId never grants authority.
3. Capability transfer must be explicit.
4. Local and remote communication share semantic concepts but not identical physical assumptions.
5. Remote latency and failure must remain observable when relevant.
6. The kernel provides communication mechanisms, not RPC semantics.
7. Standard .NET networking and stream APIs remain compatible.
8. Large local data transfer must permit shared-memory/zero-copy paths.
9. Remote capability representation must be authenticated and non-forgeable.
10. Communication must support cancellation and bounded resource usage.
11. Automatic retries must not silently create duplicate side effects.
12. Service identity is independent of process identity and physical endpoint.
13. Communication APIs should prefer typed contracts while remaining transport-neutral.
14. Cross-platform fallbacks must report unsupported guarantees explicitly.
15. Local WitOS operation must not depend on cloud or network infrastructure.

---

## 217. Deferred Questions

### Kernel Channel ABI

```text
message representation
queue implementation
wait model
handle transfer
shared-buffer descriptors
```

### Remote Capability Wire Format

```text
token representation
signing
session binding
expiration
delegation chain
revocation
```

### Distributed Service Discovery

```text
registry model
LAN discovery
federation
organization domains
```

### Transport Selection

```text
QUIC
TCP
WebSocket
custom protocols
shared memory
```

### Priority Propagation

```text
donation algorithm
limits
cross-machine behavior
deadline propagation
```

### Durable Messaging

```text
persistence
delivery guarantees
acknowledgement
replay
```

### Contract Metadata

```text
interface identity
version negotiation
source generation
NativeAOT support
```

---

## 218. Relationship to Future RFCs

This RFC interacts strongly with:

```text
RFC 0007 — Universal Hardware Interface
    interrupt and device communication primitives

RFC 0008 — Storage & Persistent State
    durable streams, replication and data movement

RFC 0009 — Presentation, Shell & Input
    input/event channels and compositor communication

RFC 0010 — Application Packaging
    service contracts and communication dependencies
```

A future distributed-computing RFC may further define remote execution, service placement, application distribution, compute migration, and distributed capability routing.

---

## 219. Summary

WitOS communication is built from a small number of general concepts:

```text
Endpoint
Channel
Message
Stream
Shared Buffer
Capability
Service
```

The kernel provides only the mechanisms needed for:

```text
safe message transfer
waiting and signaling
capability delegation
shared-memory mapping
```

Higher layers implement:

```text
RPC
typed contracts
events
service discovery
remote transport
serialization
retries
distributed capabilities
```

Applications may use the same logical contract whether a provider is in process, in another local process, on another device, or in a remote service, but physical differences remain visible when they matter.

The defining principle is:

> **WitOS unifies communication semantics without pretending away locality, latency, failure, or authority boundaries.**
