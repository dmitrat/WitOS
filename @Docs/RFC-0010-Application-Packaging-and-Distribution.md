# WitOS
## RFC 0010 — Application Packaging & Distribution
### Draft v0.3

## 1. Status

Draft.

This document defines the application packaging, deployment, installation, executable launching, software identity, signing, trust, dependency, update, distribution, and application-store model of WitOS.

It builds upon:

```text
RFC 0001 — Architecture Concept
RFC 0002 — Resource & Capability Model
RFC 0003 — Application & Lifecycle Model
RFC 0004 — Security, Identity & Capability Delegation
RFC 0005 — Execution, Scheduling & Compute Model
RFC 0006 — IPC & Local/Remote Communication Model
RFC 0007 — Universal Hardware Interface
RFC 0008 — Storage & Persistent State Model
RFC 0009 — Presentation, Shell & Input Architecture
```

The central principles are:

> **An application is fundamentally executable files, not a mandatory system-managed package container.**

> **Copying an application directory must be a valid deployment mechanism.**

> **Applications may be installed anywhere. Standard installation directories are conventions, not restrictions.**

> **An application store is a distribution service, not the authority that determines what software may run.**

> **Application signing is optional for execution, but first-class and intentionally easy to use.**

> **WitOS should make cryptographically identifying software publishers cheaper and simpler than distributing unsigned software, without requiring a commercial certificate, hardware token, application store, or online authority.**

> **Whether executable software is signed must always be clearly and consistently visible to the user.**

---

## 2. Motivation

General-purpose computers are valuable partly because software can be treated as ordinary data.

A traditional and useful deployment workflow is:

```text
download archive
      ↓
extract
      ↓
run executable
```

or:

```text
build application
      ↓
copy directory
      ↓
run executable
```

This gives users and developers:

```text
control
inspectability
portability
offline deployment
easy debugging
simple backups
software preservation
```

More restrictive systems often require combinations of:

```text
mandatory package format
mandatory application registration
fixed installation location
mandatory signing
centralized signing authority
application-store approval
opaque per-application containers
```

These mechanisms can provide useful services but should not define the fundamental WitOS application model.

WitOS should preserve the freedom of the general-purpose computer while providing strong optional mechanisms for:

```text
software identity
publisher verification
signing
secure updates
stores
automatic updates
organization policy
supply-chain security
```

---

## 3. Fundamental Deployment Model

The minimum valid WitOS deployment operation is:

```text
copy application directory
```

For example:

```text
MyApplication/
    MyApplication
    MyApplication.dll
    DependencyA.dll
    DependencyB.dll
    resources/
```

The application may then be launched directly.

No mandatory installation transaction is required.

---

## 4. Executable by Path

An executable may be launched directly through its filesystem location.

For example:

```text
/Tools/MyApplication/MyApplication
```

Launching it must not require that the application was previously:

```text
installed through a store
registered globally
signed
entered into a package database
```

unless administrative policy explicitly imposes such restrictions.

---

## 5. No Mandatory Installation Database

WitOS must not make an installation database the source of truth for software existence.

The source of truth is primarily:

```text
the files themselves
```

System metadata may index known applications, but deleting or corrupting that index must not make executable files cease to be executable.

---

## 6. Application Directory

An application may simply be a directory containing:

```text
managed assemblies
native libraries
resources
configuration
icons
plugins
documentation
manifest
executable host
```

No special filesystem object type is required.

---

## 7. No Mandatory Installation Location

WitOS may provide a conventional location such as:

```text
/Programs
```

or:

```text
/Applications
```

as the default installation destination.

This location is:

```text
convenient
discoverable
easy for shells to index
```

but not mandatory.

---

## 8. Arbitrary Installation Location

A user may install an application into:

```text
/Programs/MyApp
/Tools/MyApp
/Work/Engineering/Solver
/Portable/MyApp
```

or another writable location.

The program remains a valid application.

---

## 9. Relocatable Applications

Applications should be relocatable whenever technically possible.

Moving:

```text
/Programs/MyApp
```

to:

```text
/Tools/MyApp
```

should not inherently break the application.

Applications should therefore prefer:

```text
relative paths
resource discovery
AppContext.BaseDirectory
configuration
```

over installation-time hard-coded absolute paths.

---

## 10. Application Base Directory

Standard .NET mechanisms such as:

```csharp
AppContext.BaseDirectory
```

remain available.

Application location and current working directory remain separate concepts.

WitOS preserves standard .NET behavior.

---

## 11. User Filesystem Ownership

WitOS is a general-purpose operating system.

The machine owner must be able to:

```text
browse files
copy files
move files
rename files
inspect program directories
archive applications
modify files
delete applications
```

according to their filesystem authority.

---

## 12. Applications Are Not Hidden Containers

WitOS should not require installed applications to disappear into opaque directories that the user cannot meaningfully inspect.

A program directory is an ordinary directory.

---

## 13. No Mobile-Style Filesystem Lockout

WitOS must not require a model in which:

```text
application installation is store-centric
application directories are deliberately opaque
filesystem access is hidden from the machine owner
ordinary executables cannot be launched directly
```

Security should instead be provided through:

```text
capabilities
execution isolation
resource authority
signing and trust information
```

---

## 14. User Authority vs Application Authority

The user's filesystem authority and the authority of an application launched by that user are separate.

For example:

```text
User:
    broad filesystem authority

Application:
    selected-document capability
```

This allows:

```text
user ownership
+
application least privilege
```

without removing control from the user.

---

## 15. Portable Applications

A portable application may keep everything required for execution within its directory.

Example:

```text
Compiler/
    Compiler
    Compiler.dll
    lib/
    resources/
    settings.json
```

It may be copied, archived, placed on removable storage, or moved between directories and still run.

---

## 16. Portable State

Applications may deliberately keep writable state beside program files.

For example:

```text
./settings.json
./workspace.db
```

This is valid when the application directory is writable.

WitOS should not prohibit portable application designs.

---

## 17. Installed State

Applications may instead use separate application-state resources defined by RFC 0008.

Recommended conventional model:

```text
Program Directory
    mostly immutable code/resources

Application State
    configuration
    user state
    cache
```

This simplifies updating and rollback.

It remains a convention rather than a mandatory hidden container.

---

## 18. Standard .NET Deployment

WitOS should preserve existing .NET deployment models.

These include:

```text
framework-dependent application
self-contained application
NativeAOT application
managed DLL invocation
```

A typical:

```text
dotnet publish
```

output should already be a runnable application directory.

---

## 19. Developer Workflow

A developer should be able to:

```text
dotnet build
```

and directly run the build output.

No packaging step is required for debugging.

No signing service is required.

No developer account is required.

No store upload is required.

---

## 20. Package Is Optional

WitOS may define a standard application package format.

However:

> **A package is a distribution envelope, not the execution model.**

A package may conceptually contain:

```text
application directory
+
manifest
+
optional signature
+
optional distribution metadata
```

---

## 21. Package Extraction

A package should be extractable into an ordinary directory.

After extraction, the application remains normal files.

The package must not need to remain mounted as an opaque runtime container.

---

## 22. Package Format

A WitOS package should ideally be:

```text
simple
inspectable
documented
offline-verifiable
extractable with standard tooling
```

Its exact archive format is deferred.

---

## 23. Installation

For many applications, installation is simply:

```text
choose source
      ↓
choose destination
      ↓
copy/extract files
```

Optional integrations may then be registered.

---

## 24. Installer

An installer is simply an application that assists deployment.

A minimal installer may perform:

```text
1. inspect package
2. optionally verify signature
3. ask for destination
4. copy/extract files
5. optionally register application metadata
```

No privileged universal installer binary is required.

---

## 25. Advanced Installation

More complex software may additionally register:

```text
file handlers
URI handlers
services
shell integrations
drivers
scheduled activations
update sources
```

These operations require explicit appropriate capabilities.

---

## 26. Declarative Integration

System integration should preferably be declarative.

For example, the application manifest may describe:

```text
content handlers
services
commands
shell metadata
driver matching
```

This improves inspection, rollback, security, and automation.

---

## 27. Installation Scripts

Custom installer code remains allowed.

General-purpose computing sometimes requires arbitrary deployment logic.

Such code simply executes with explicit capabilities rather than receiving unrestricted authority automatically.

---

## 28. Manifest

An application directory may optionally contain a WitOS application manifest.

Possible contents:

```text
ApplicationId
version
display name
publisher identity
entry points
icons
content handlers
services
optional capabilities
native assets
update metadata
```

---

## 29. Manifest Is Optional for Execution

An executable without a WitOS manifest remains runnable.

Such an executable may have fewer integration features.

WitOS may treat it as an implicit application.

---

## 30. Application Identity

Applications needing stable permissions, updates, services, associations, handoff, or persistent grants should declare an `ApplicationId`.

---

## 31. Application Identity Is Independent of Path

This must remain true:

```text
ApplicationId
    ≠
installation path
```

Moving an application does not inherently change application identity.

---

## 32. Application Identity Is Independent of Filename

Renaming the executable does not necessarily change application identity when an explicit manifest provides one.

---

## 33. Installation Identity

The system may additionally use:

```text
InstallationId
```

to identify one physical installed copy.

For example:

```text
ApplicationId: OutWit.SomeTool

Installation A:
    /Programs/SomeTool

Installation B:
    /Development/SomeTool/bin/Debug
```

Both may represent the same logical application but different installations.

---

## 34. Explicit Path Launch

When a user explicitly launches:

```text
/Development/MyApp/MyApp
```

WitOS must execute that specific copy.

The system must not silently replace it with another installation registered under the same `ApplicationId`.

---

## 35. Application Registration

The system may maintain an index of known applications for:

```text
launcher
search
Open With
updates
uninstall UI
service activation
```

Registration is optional for basic execution.

---

## 36. Registration Is an Index

Registration metadata must not become the application's existential source of truth.

Where practical, it should be reconstructable from manifests, filesystem scanning, and user configuration.

---

## 37. Shell Discovery

A shell may discover applications through:

```text
registered applications
standard program directories
user-configured directories
pinned executable paths
store metadata
```

---

## 38. Pinning Arbitrary Executables

A user may pin any executable to the shell launcher.

Formal installation is not required.

---

## 39. Shortcuts

A shortcut may target:

```text
ApplicationId
InstallationId
absolute executable path
activation contract
resource
URL
```

Deleting a shortcut does not uninstall the application.

---

## 40. File Associations

Applications may register handlers for:

```text
content types
filename extensions
resource types
URI schemes
semantic operations
```

Applications may also be selected manually even when unregistered.

---

## 41. Service Registration

An application may contain service components.

Registration maps:

```text
logical service identity
    ↓
activation target
```

The service binaries remain ordinary files.

---

## 42. Shells and Compositors

Shells and alternative compositors may be distributed like ordinary applications.

Selection as a shell/compositor grants their privileged capabilities.

Their files do not require a fundamentally different storage model.

---

## 43. Drivers

Driver packages may contain:

```text
managed code
native code
hardware match metadata
firmware blobs
resource declarations
```

Driver registration is more security-sensitive but still builds on ordinary files and explicit metadata.

---

## 44. Software Identity, Integrity and Trust

WitOS explicitly separates:

```text
Signature Presence
Integrity
Publisher Identity
Identity Verification
Trust
Runtime Authority
```

These concepts must not be conflated.

---

## 45. Signature Presence

The first and simplest property is whether an executable application carries a cryptographic software signature.

Conceptually:

```text
SignatureStatus:
    Unsigned
    SignedValid
    SignedInvalid
```

This property must be available to the shell and other trusted presentation components.

---

## 46. Integrity

Integrity answers:

> Are these exactly the files that were signed?

A valid cryptographic signature establishes that signed content has not changed since it was signed.

---

## 47. Publisher Identity

Publisher identity answers:

> Which signing identity created this signature?

This may be represented by a public signing key.

A signature does not by itself prove that the key belongs to a particular real-world person or company.

---

## 48. Trust

Trust answers:

> Does the user, organization, store, or another authority trust this signing identity for this purpose?

Trust is policy.

It is not inherent in possession of a private key.

---

## 49. Hard Shell Visibility Requirement

Whether an application is signed must never be hidden as an obscure property-page detail.

Every conforming WitOS shell must make the signature state visibly distinguishable in normal application-selection surfaces.

This includes, where applicable:

```text
application launcher
application search
file manager executable view
Open With dialog
task/application management UI
first-launch UI
application properties
installer/store UI
```

---

## 50. Unsigned Application Badge

The reference WitOS presentation uses a **red circular badge in the corner of the application icon** to mark unsigned executable software.

Conceptually:

```text
┌──────────────┐
│              │
│     ICON     │
│          ●   │
└──────────────┘
```

The exact graphic design may evolve, but the semantics are fixed:

> **A red unsigned-software badge means that the executable has no valid cryptographic publisher signature.**

---

## 51. Badge Is System Metadata

The unsigned badge is not part of the application's own icon.

The application must not be able to:

```text
remove it
replace it
cover it
claim it is signed
supply a fake signed-state flag
```

Signature state comes from trusted system verification.

---

## 52. Shell Must Not Trust Application Metadata for Signature State

Application-provided fields such as:

```text
"Signed": true
"Trusted": true
"PublisherVerified": true
```

have no security meaning.

The shell uses the system software-verification service.

---

## 53. Canonical Trust Descriptor

WitOS should expose a trusted descriptor conceptually similar to:

```csharp
public sealed record SoftwareTrustDescriptor
{
    public SignatureStatus SignatureStatus { get; init; }

    public PublisherIdentity? Publisher { get; init; }

    public PublisherVerification Verification { get; init; }

    public TrustAssertions Trust { get; init; }

    public bool ContentModified { get; init; }
}
```

The exact API is deferred.

---

## 54. Signature Badge vs Trust Badge

Signature presence and publisher trust are separate visual concepts.

For example:

```text
Unsigned
    red unsigned badge

Signed, identity unknown
    no unsigned badge
    publisher may be marked unverified

Signed, trusted publisher
    valid signature
    trusted publisher indication may be shown

Signature invalid / files modified
    explicit integrity warning
```

A valid self-signed application is still **signed**.

It must not be marked as unsigned merely because the publisher identity is unverified.

---

## 55. Invalid Signature Is Distinct from Unsigned

An application that has no signature and an application whose signed contents have been modified are different states.

WitOS should present them differently.

For example:

```text
Unsigned:
    red circular unsigned badge

Signature invalid / content modified:
    red integrity-failure badge
    or red badge with X/broken-signature symbol
```

An invalid signature is potentially more significant than absence of a signature.

---

## 56. Badge Semantics Must Be Stable

Alternative shells may use different visual styling for accessibility or form factor, but must preserve unambiguous semantics.

A shell must not make unsigned software visually indistinguishable from validly signed software.

---

## 57. Accessibility

The signature indicator must not depend solely on color.

Accessible presentation should also expose semantic text such as:

```text
Unsigned application
```

or:

```text
Signature invalid
```

to screen readers, high-contrast themes, and non-visual shells.

---

## 58. Terminal Representation

Text-oriented shells should provide an equivalent hard indication.

For example:

```text
[UNSIGNED] MyTool
[SIGNED]   PhotoEditor
[INVALID]  ModifiedApp
```

Exact syntax is shell-specific.

---

## 59. Icon Caching

Shells must not cache signature decoration indefinitely without invalidation.

If executable content changes, trust/signature state must be re-evaluated.

---

## 60. Modification Invalidates Signed Presentation

If any protected application file changes after signing, the shell must stop presenting the application as validly signed.

The visual trust state should update accordingly.

---

## 61. Signature Verification Service

Signature validation should be provided by a trusted WitOS system service or library.

Shells should not independently invent incompatible validation rules.

---

## 62. Signing Must Be Easy

A developer should be able to create a signing identity locally.

Conceptually:

```text
wit identity create
```

produces:

```text
Public Key
Private Key
```

The private key may be stored as an encrypted file or in an optional secure key provider.

No commercial certificate is required.

No hardware token is required.

No store account is required.

---

## 63. Signing an Application

A developer should be able to perform something conceptually equivalent to:

```text
wit sign ./publish
```

The command signs the application manifest and cryptographic representation of the application files.

For example:

```text
MyApp/
    MyApp
    MyApp.dll
    witos.app.json
    witos.signature
```

The files remain normal inspectable files.

---

## 64. Directory Signatures

A directory signature may cover:

```text
manifest
entry points
file names
file hashes
version
ApplicationId
publisher key
```

An efficient implementation may use a Merkle tree or equivalent structure.

Exact format is deferred.

---

## 65. Package Signatures

A distribution package may also be signed.

WitOS should distinguish:

```text
package integrity
```

from:

```text
installed application integrity
```

where useful.

---

## 66. Signing Is Optional for Execution

Unsigned software remains executable on a normal user-owned WitOS installation.

WitOS clearly identifies it as unsigned but does not automatically block it.

---

## 67. No Mandatory Commercial CA

Basic signing must not require purchasing a certificate from a commercial certificate authority.

Cryptographic authenticity and commercial identity verification are different services.

---

## 68. No Mandatory Hardware Token

Developers must be able to sign software using a protected software key.

For example:

```text
encrypted private key
+
passphrase
```

Hardware-backed signing is supported as a stronger option.

It is not a prerequisite.

---

## 69. Hardware-Backed Signing

Developers or organizations may choose:

```text
TPM
secure enclave
hardware signing device
HSM
organization signing service
```

for stronger private-key protection.

WitOS should report this as an additional signing property when verifiable.

---

## 70. Signing Key as Publisher Identity

The most basic publisher identity may simply be:

```text
PublicKey = K
```

A developer can therefore maintain continuity across releases without external identity verification.

---

## 71. Self-Signed Publisher

A self-created signing identity remains useful.

Example:

```text
Signature: Valid
Publisher Name: OutWit
Publisher Identity: Self-asserted
Key: A7F3...
```

This proves continuity of the same signing key across versions.

It does not prove the real-world identity claim by itself.

---

## 72. Trust on First Use

WitOS should support TOFU-style trust.

On first execution:

```text
Publisher: OutWit
Signing key: A7:F3:...
Identity not externally verified.

[Run Once]
[Trust This Publisher]
[Cancel]
```

If the user chooses:

```text
Trust This Publisher
```

the signing key becomes a user trust assertion.

---

## 73. Key Continuity

Later versions signed with the same trusted key can display:

```text
Signature valid
Signed by previously trusted publisher
```

This provides meaningful protection even without a global certificate authority.

---

## 74. Unexpected Key Change

If an application claiming the same identity suddenly appears signed by another key:

```text
ApplicationId: OutWit.MyApp
Previous key: A
Current key: B
```

WitOS should warn clearly unless a valid key-transition chain exists.

---

## 75. Verified Publisher Identity

A publisher may optionally prove that its signing key corresponds to a real-world identity.

Examples:

```text
verified individual
verified organization
verified domain
verified source repository account
store-verified publisher
organization-directory identity
```

---

## 76. Multiple Identity Verifiers

WitOS should not require one universal identity authority.

Possible trust assertions may come from:

```text
user
organization administrator
application store
domain ownership proof
source-code hosting identity
certificate authority
community trust service
```

---

## 77. Trust Assertions

Conceptually:

```text
Publisher Key
      │
      ├── trusted by User
      ├── verified by Store A
      ├── verified for example.com
      ├── trusted by Organization X
      └── verified by Identity Provider Y
```

Applications may display these assertions separately.

---

## 78. No Universal Trust Score

WitOS should avoid reducing software trust to one opaque score.

Instead it should report factual properties:

```text
signature valid
publisher identity unverified
publisher identity verified
publisher trusted by user
publisher trusted by organization
hardware-backed key
transparency proof present
content modified
```

---

## 79. Store as Trust Provider

An application store may verify publisher identity.

For example:

```text
Application
    ↓ signed by
Publisher Key
    ↓ verified by
Store
```

The same signed application downloaded outside that store can still retain its publisher signature and verification evidence.

---

## 80. Store Does Not Own Publisher Identity

Publisher identity must not depend on downloading software through one particular store.

The store is one verifier, not the root of all software identity.

---

## 81. Domain Verification

A publisher may prove control of a domain by publishing a signing-key assertion through a standard mechanism.

This can produce:

```text
Publisher key verified for example.com
```

without requiring an expensive code-signing certificate product.

---

## 82. Repository Identity Verification

A publisher may optionally associate a signing key with a public source repository or developer identity.

This may be especially useful for open-source software.

---

## 83. Offline Verification

Once required trust material has been acquired, signature verification should work offline wherever cryptographically possible.

Launching a signed application must not require contacting:

```text
store
certificate service
WitOS cloud
publisher server
```

on every execution.

---

## 84. Revocation

WitOS should support publisher-key revocation.

A key may be:

```text
compromised
retired
replaced
```

Revocation semantics must distinguish historical signatures from current trust policy.

---

## 85. Offline Revocation Limitation

A fully offline computer may not know about newly issued revocations.

WitOS must report actual verification freshness honestly.

---

## 86. Key Rotation

Signing identities require a normal key-rotation mechanism.

Example:

```text
Old Key A
    ↓ signs delegation
New Key B
```

A valid transition preserves publisher identity continuity.

---

## 87. Rotation Statement

A key-rotation statement may contain:

```text
old key
new key
publisher/application scope
effective version/time
delegation constraints
```

---

## 88. Multiple Active Keys

Organizations may use several authorized signing keys concurrently.

Examples:

```text
stable release key
CI release key
beta key
emergency key
```

---

## 89. Emergency Recovery Key

A publisher may maintain an offline recovery key capable of replacing or revoking normal signing keys.

Exact semantics are deferred.

---

## 90. Key Scope

Signing keys may be scoped to:

```text
all publisher applications
one ApplicationId
one release channel
drivers
system components
```

Narrower scope limits compromise impact.

---

## 91. Release Channels

An application may define:

```text
Stable
Beta
Nightly
```

as authenticated release channels.

Different channels may use different signing policies or keys.

---

## 92. Update Authenticity

Automatic updates should normally require cryptographic continuity with the currently accepted software identity.

The same download URL alone is not sufficient proof.

---

## 93. Signed Update Chain

Conceptually:

```text
Installed Application
    ↓ trusted publisher key
New Version
    ↓ valid signature
Accepted update
```

or:

```text
Old Key
    ↓ authorized rotation
New Key
    ↓ signs new version
Accepted update
```

---

## 94. Unsigned Updates

Unsigned applications may still be updated manually.

Automatic unattended update of unsigned software should be treated cautiously because publisher continuity cannot be established cryptographically.

---

## 95. User Override

The machine owner may explicitly replace software with a different build, fork, or publisher.

WitOS may warn about identity change.

It should not make such replacement impossible on a normal user-controlled system.

---

## 96. Modified Signed Applications

If signed program files are modified:

```text
Signature:
    invalid / content changed
```

The application remains a file the user controls.

The system simply stops claiming that it is the intact publisher-signed build.

---

## 97. Local Re-Signing

A user or developer may re-sign modified software using another key.

The resulting software carries the new signing identity unless an authorized delegation connects it to the previous publisher identity.

---

## 98. Transparency Logs

WitOS may support optional software transparency logs.

A publisher may publish records such as:

```text
ApplicationId
Version
Package Hash
Publisher Key
Timestamp
```

---

## 99. Transparency Purpose

Transparency can help detect:

```text
targeted malicious releases
compromised distribution servers
unexpected signing-key use
hidden package substitution
```

---

## 100. Transparency Is Optional

Transparency infrastructure must not become mandatory for ordinary execution.

Offline systems remain first-class.

---

## 101. Build Attestation

Publishers may optionally attach information such as:

```text
source revision
build system
CI identity
SBOM
reproducible-build information
```

These are supply-chain assertions, not runtime authority.

---

## 102. Trust UI

WitOS presentation should distinguish clearly between:

```text
Unsigned

Signed
Publisher self-asserted

Signed
Previously trusted publisher

Signed
Verified individual

Signed
Verified organization

Signature invalid / content modified

Signing key revoked
```

These states describe evidence.

They must not claim that software itself is universally safe.

---

## 103. Mandatory Signature Decoration

For normal graphical shells, executable application icons must carry a system-generated signature-state decoration when the application is:

```text
unsigned
signature-invalid
cryptographically modified
```

A conforming shell may not suppress this decoration merely for cosmetic reasons.

---

## 104. Reference Visual Semantics

Reference WitOS semantics:

```text
No valid signature:
    red circular corner badge

Signature invalid / signed files changed:
    red integrity-failure badge

Valid signature:
    no unsigned badge
```

Additional publisher-trust indicators may be rendered separately.

---

## 105. Signed Does Not Mean Trusted

A valid self-signed application is cryptographically signed.

Therefore it does not receive the unsigned badge.

However, the shell may separately state:

```text
Publisher identity not verified
```

This keeps the visual model semantically correct.

---

## 106. Trusted Does Not Mean Safe

A trusted publisher indicator means only that the signing identity is trusted according to policy.

It does not mean:

```text
bug-free
malware-free
secure
appropriate for all users
```

Runtime capabilities remain independent.

---

## 107. First Launch

First-run UI may show:

```text
Application
Publisher
Signature status
Publisher verification
Download origin
Requested capabilities
```

when appropriate.

---

## 108. User Choices

For an unrecognized but validly signed application, possible actions may include:

```text
Run Once
Trust Publisher
Trust This Application Only
Cancel
```

Exact UI remains deferred.

---

## 109. Trust Scope

A user trust decision may apply to:

```text
specific binary hash
specific installation
ApplicationId
publisher key
publisher + ApplicationId
```

depending on policy and user intent.

---

## 110. Trust Is Not Runtime Authority

Trusting a publisher must not grant its applications unrestricted:

```text
filesystem
camera
microphone
network
administrative
hardware
```

capabilities.

Runtime authority remains governed by RFC 0004.

---

## 111. Execution Trust vs Resource Authority

These are separate questions:

```text
May this code execute?

What resources may this code access?
```

WitOS must preserve this distinction.

---

## 112. Organization Policy

Managed environments may require stronger rules such as:

```text
signed only
verified publishers only
organization-approved key
approved store
hardware-backed signing
transparency proof
```

This is administrative policy.

It does not redefine the normal user-owned WitOS system.

---

## 113. No Developer Mode Requirement

A normal WitOS user should not need to enable a special developer mode merely to execute locally built, modified, or unsigned software.

---

## 114. Native Assets

Packages may include architecture-specific assets using normal .NET conventions.

Likely runtime identifiers include:

```text
witos-x64
witos-arm64
witos-riscv64
```

Exact RID design should follow upstream .NET guidance.

---

## 115. Standard .NET Target Framework

Pure managed applications should normally target:

```text
netX.0
```

A special WitOS TFM should not be required simply to execute on WitOS.

---

## 116. WitOS APIs

Applications intentionally using:

```text
OutWit.OS.Resources
OutWit.OS.Execution
OutWit.OS.Presentation
```

may still target standard `netX.0` where packages provide appropriate portable contracts/fallbacks.

---

## 117. Dependencies

Applications may use:

```text
private dependencies
shared .NET runtime
NuGet libraries
native libraries
plugins
```

---

## 118. Private Dependencies Preferred

The most robust deployment model remains:

```text
application directory contains its private dependencies
```

This minimizes machine-wide dependency conflicts.

---

## 119. Shared Frameworks

Large frameworks such as .NET may be shared.

Framework resolution should follow upstream .NET behavior.

---

## 120. Side-by-Side Dependencies

Different applications may use different versions of the same dependency simultaneously.

---

## 121. NuGet

NuGet remains the standard .NET developer/library package ecosystem.

WitOS application packaging should not attempt to replace it.

---

## 122. Application Package vs NuGet Package

These are distinct:

```text
NuGet package
    developer/library dependency

WitOS application package
    deployable application distribution
```

---

## 123. Plugins

Application-specific plugin systems remain application-level concerns.

Plugins may live:

```text
inside application directory
inside user plugin directory
inside configured shared directory
```

No universal WitOS plugin directory is required.

---

## 124. Application Updates

A directory application may be updated by:

```text
download new version
extract to temporary directory
verify
replace/switch directory atomically
```

---

## 125. Avoid In-Place Binary Mutation

Where practical, update systems should prefer replacing a complete application version rather than modifying binaries one-by-one.

---

## 126. Side-by-Side Versions

Applications may use:

```text
MyApp/
    1.4/
    1.5/
    current
```

or equivalent.

This is optional implementation policy.

---

## 127. Rollback

Previous versions may remain available for rollback.

---

## 128. Application State

Updating program files must not automatically delete or overwrite persistent application/user state.

State migration belongs to the application.

---

## 129. Manual Update

The user may manually replace or copy an application directory.

This remains a valid update path.

---

## 130. Downgrade

WitOS must not inherently block an older version merely because a newer version exists.

Application-state compatibility is separate.

---

## 131. Update Sources

An application may use:

```text
vendor server
source repository release
application store
organization repository
LAN repository
local media
```

as update sources.

---

## 132. Offline Updates

Updates must be possible through:

```text
USB
local archive
LAN share
offline repository
```

No cloud service is required.

---

## 133. Application Store

WitOS may offer application-store services providing:

```text
application discovery
search
publisher verification
reviews
payments
automatic updates
package hosting
```

---

## 134. Store Is Optional

A system without any configured application store remains a complete WitOS system.

---

## 135. Store Is Not Execution Authority

Software distributed through:

```text
website
source repository
USB
email
source build
another store
```

remains legitimate software.

---

## 136. No Sideloading Category

WitOS should not treat non-store software as a special category called "sideloaded software".

It is simply software.

---

## 137. Multiple Stores

Users and organizations may configure multiple stores or repositories.

No single store is architecturally privileged.

---

## 138. Direct Distribution

A developer may distribute:

```text
directory
archive
WitOS package
source code
```

directly.

---

## 139. Build From Source

The workflow:

```text
git clone
dotnet build
run
```

must remain first-class.

---

## 140. Store Installation Model

Store installation ultimately reduces to:

```text
download
verify
extract/copy
register optional integrations
```

The result remains an ordinary application installation.

---

## 141. Store Failure

If the store later becomes unavailable:

```text
installed applications continue to run
```

unless the application's own licensing architecture fundamentally requires remote service.

---

## 142. Store Account Independence

Running ordinary local applications should not require a global WitOS account.

---

## 143. Store Trust Assertion

A store may attach an assertion such as:

```text
Publisher key K verified as organization Example Corp
```

This assertion may remain useful even when the application was obtained through another channel.

---

## 144. Commercial Software

Commercial applications may implement licensing through:

```text
license files
accounts
offline keys
organization licensing
store purchases
```

WitOS does not mandate one system.

---

## 145. DRM

Stores or publishers may provide optional DRM mechanisms.

DRM does not define the baseline application model.

---

## 146. Application Backup

Backing up a portable application may simply mean copying its directory.

Application state may be backed up separately.

---

## 147. Software Preservation

File-based deployment improves long-term software preservation.

An application should not depend unnecessarily on a central store existing years later merely to start.

---

## 148. Air-Gapped Systems

An air-gapped WitOS system must support:

```text
application installation
signature verification
application execution
uninstallation
offline updates
```

without Internet access.

---

## 149. Uninstallation

For a simple application:

```text
uninstall
    =
delete application directory
```

This should genuinely be sufficient.

---

## 150. Registered Uninstallation

Applications with optional integrations additionally remove:

```text
launcher metadata
file associations
service registrations
update metadata
```

---

## 151. User Documents

Uninstalling an application must not automatically delete user documents merely because that application created them.

---

## 152. Application Private State

Private state may be:

```text
kept
deleted
archived
```

according to explicit user choice or policy.

---

## 153. Manual Application Removal

If a user simply deletes a registered application directory, WitOS should tolerate it.

Metadata may later be cleaned up.

This is not filesystem corruption.

---

## 154. Manual Application Move

If the user manually moves an application directory, direct execution at the new location should immediately work.

Management tools may offer to update registrations.

---

## 155. Read-Only Applications

Applications may execute from read-only storage when they do not require modifying their own directory.

Writable state may live elsewhere.

---

## 156. Removable Media

Portable applications may run from removable storage.

If the medium disappears during execution, normal storage/resource failure semantics apply.

---

## 157. Network Storage

Applications may execute from network storage where policy and runtime semantics permit.

Availability and latency remain observable.

---

## 158. Multiple Application Copies

The same application may legitimately exist at:

```text
/Programs/MyTool
/Development/MyTool
/USB/MyTool
```

WitOS must support this.

---

## 159. Preferred Installation

Activation by `ApplicationId` may use a configured preferred installation.

Direct path activation remains exact.

---

## 160. Persistent Grants and Multiple Installations

Persistent grants may bind to:

```text
ApplicationId
publisher + ApplicationId
specific installation
specific binary hash
```

depending on security policy.

This is especially important for unsigned or locally modified builds.

---

## 161. Signed Application Continuity

For a signed application, stable:

```text
Publisher Identity
+
ApplicationId
```

provides a strong basis for retaining persistent grants across authentic updates.

---

## 162. Developer Builds

Locally compiled development builds should support convenient explicit development trust without requiring production certificates.

They remain visibly unsigned unless the developer signs them.

---

## 163. Download Provenance

WitOS may record optional provenance:

```text
download URL
store
repository
local build
removable media
```

This informs users.

It does not establish publisher identity by itself.

---

## 164. Internet-Origin Warning

A shell may warn before first execution of newly downloaded unknown software.

The user remains able to proceed on a normal user-owned system.

---

## 165. Manifest Capability Declarations

An application may declare:

```text
required capabilities
optional capabilities
purpose descriptions
```

for user understanding, store metadata, and organization policy.

Declarations do not grant authority.

---

## 166. Required Capability Failure

If a required capability is denied or unavailable, launch may fail with an understandable explanation.

---

## 167. Optional Capability Failure

An application should continue when optional capabilities are unavailable if its design permits.

---

## 168. Compatibility Applications

Existing .NET applications without WitOS metadata remain valid.

They may execute under a compatibility profile.

---

## 169. Broad Filesystem Applications

WitOS must support software such as:

```text
IDE
compiler
terminal
file manager
backup utility
source-control client
build system
```

which naturally requires broad filesystem authority.

Capability security must not make these workflows impractical.

---

## 170. Explicit Broad Grants

A user may deliberately grant an application access to:

```text
entire development workspace
home directory
whole volume
raw storage
```

when appropriate.

Fine-grained security must remain flexible rather than paternalistic.

---

## 171. PATH-Like Execution

WitOS may support executable search paths for command-line convenience.

This is a user/session namespace convention, not an installation mechanism.

---

## 172. Command Aliases

Users may define aliases or command registrations pointing to arbitrary executable paths.

---

## 173. Environment

Standard .NET environment semantics remain supported.

Applications should avoid global environment modifications when explicit configuration is sufficient.

---

## 174. Application Inspection

Standard tools should allow inspection of:

```text
application files
manifest
signature
publisher key
verification assertions
version
dependencies
```

without proprietary online infrastructure.

---

## 175. Signature Inspection

A user should be able to run a tool conceptually equivalent to:

```text
wit verify /Programs/MyApp
```

and receive:

```text
Signature: valid
ApplicationId: OutWit.MyApp
Publisher key: A7F3...
Publisher identity: OutWit
Identity verification:
    domain verified
    user trusted
Key rotation chain: valid
Transparency proof: present
```

or:

```text
Signature: none
Application is unsigned
```

---

## 176. Trust Information Is Factual

WitOS should prefer factual descriptions such as:

```text
unsigned
signature valid
signature invalid
publisher identity unverified
publisher trusted by user
```

rather than opaque claims that an application is "safe".

---

## 177. Supply-Chain Security

The ecosystem may support:

```text
publisher signatures
dependency hashes
SBOM
build attestations
reproducible builds
transparency logs
organization signatures
```

without universally requiring them.

---

## 178. Security Layers

A strong release may therefore have:

```text
Application files
      ↓
Content signature
      ↓
Publisher signing key
      ↓
Publisher identity assertions
      ↓
User / Organization / Store trust
      ↓
Runtime capabilities
```

Each layer answers a different question.

---

## 179. Trust Does Not Replace Runtime Security

Even a strongly verified publisher can ship buggy or vulnerable software.

Therefore:

```text
publisher trust
≠
application correctness
≠
runtime authority
```

Capability enforcement remains necessary.

---

## 180. Package Manager Security

Package managers and stores should execute with only the capabilities required to:

```text
download
verify
write installation destination
update registration metadata
```

They do not require universal system authority for ordinary installations.

---

## 181. System-Wide Installation

Writing into a protected shared program directory may require administrative filesystem authority.

Installing into a user-owned directory need not.

---

## 182. User-Specific Installation

Users should be able to install most ordinary software without administrator privileges.

---

## 183. System and User Copies

A system-wide copy and user-specific copy of the same application may coexist.

WitOS must not create unnecessary machine-global dependency conflicts.

---

## 184. Cross-Architecture Distribution

Pure managed application assemblies may be architecture-independent.

The same code can run on:

```text
x64
ARM64
future RISC-V
```

where dependencies permit.

---

## 185. Multi-Architecture Directory

A directory may include:

```text
runtimes/
    witos-x64/
        native/
    witos-arm64/
        native/
```

---

## 186. Architecture-Specific Archives

Publishers may alternatively distribute separate archives for different CPU architectures.

Both strategies are valid.

---

## 187. Application Packaging Tooling

WitOS packaging should integrate with:

```text
dotnet publish
MSBuild
NuGet
```

---

## 188. Packaging Must Not Rebuild

Packaging normally operates on already-produced publish output:

```text
dotnet publish
      ↓
publish directory
      ↓
wit package
      ↓
distribution archive
```

---

## 189. Signing Tooling

Signing likewise operates directly on application output:

```text
dotnet publish
      ↓
wit sign
      ↓
signed directory/package
```

---

## 190. CI Signing

Automated release systems may use:

```text
encrypted CI key
HSM
organization signing service
hardware-backed key
```

depending on desired protection.

---

## 191. Local Open-Source Signing

A small open-source developer can use:

```text
local encrypted signing key
```

without recurring commercial fees or specialized hardware.

This is an explicit WitOS design objective.

---

## 192. Store Integration

A store may automatically:

```text
verify publisher identity
verify package signature
record transparency entry
host package
provide updates
```

without changing the underlying file-based application model.

---

## 193. Shell Trust-State API

The application-management and shell APIs must expose signature state as a first-class immutable system property.

A shell should not need to parse signature files itself.

Conceptually:

```csharp
var app = await Applications.ResolveAsync(path);

switch (app.Trust.SignatureStatus)
{
    case SignatureStatus.Unsigned:
        ...
        break;

    case SignatureStatus.Valid:
        ...
        break;

    case SignatureStatus.Invalid:
        ...
        break;
}
```

---

## 194. Trust Decoration Is Part of Shell Conformance

A graphical shell that intentionally presents unsigned applications exactly like validly signed applications is not conformant with WitOS presentation requirements.

The operating system should provide common trust-decoration resources to keep alternate shells visually consistent.

---

## 195. Application Cannot Opt Out

There is no manifest property such as:

```text
HideUnsignedBadge = true
```

An application cannot request removal of trust-state decoration.

---

## 196. Shell Themes Cannot Remove Semantics

Themes may alter colors, contrast, or geometry for accessibility and styling.

They may not remove the semantic distinction between:

```text
unsigned
validly signed
invalid signature
```

---

## 197. File Manager Executable Decoration

Executable files should expose their signature state in normal file-manager presentation as well.

For example, an unsigned executable may receive the same red corner indicator even if it is not formally registered as an application.

---

## 198. Launcher Decoration

Pinned or discovered application entries retain the system signature-state decoration.

Copying an unsigned application to `/Programs` must not make the decoration disappear.

---

## 199. Open-With Decoration

When the user chooses an application in `Open With`, unsigned applications should remain visibly marked.

This is particularly useful when selecting arbitrary executable handlers.

---

## 200. Application Manager Decoration

Application/task-management tools should display signature status for active applications.

This helps answer:

```text
What executable is currently running?
Was it signed?
By whom?
Has the signed content been modified?
```

---

## 201. Running Application Identity

The trust state displayed for a running application should correspond to the executable content that was actually launched, not merely the current contents of a path that may have changed afterwards.

Exact loader semantics are deferred.

---

## 202. Conformance Tests

Packaging/deployment tests should include:

```text
copy directory and run
run from arbitrary path
move directory and run
rename directory and run
run without manifest
run unsigned
run self-signed
run offline
run from removable storage
framework-dependent .NET launch
self-contained launch
multiple installations
manual update
manual downgrade
manual deletion
```

---

## 203. Signing Conformance Tests

Signing tests should include:

```text
valid signature
unsigned executable
modified signed file
missing signed file
self-signed identity
trusted key
untrusted key
key rotation
revoked key
offline verification
multiple active keys
```

---

## 204. Shell Decoration Conformance Tests

Every conforming graphical shell should be tested to ensure that:

```text
unsigned application
    visibly shows unsigned indicator

validly signed application
    does not show unsigned indicator

modified signed application
    shows integrity-failure indicator

signature state changes
    invalidate cached shell presentation
```

---

## 205. Accessibility Conformance Tests

Trust-state presentation must remain distinguishable when:

```text
color perception is unavailable
high-contrast mode is active
screen reader is used
terminal shell is used
```

---

## 206. Trust Conformance Tests

Trust logic should verify that:

```text
signature presence
signature validity
publisher identity
identity verification
user trust
organization trust
store verification
runtime authority
```

remain separate concepts.

---

## 207. Store Conformance

An application installed through a store must continue to run if that store later becomes unavailable.

---

## 208. Relocation Conformance

Applications should be tested from randomized directory paths to detect hidden installation-path assumptions.

---

## 209. Read-Only Conformance

Applications claiming relocatable/read-only installation support should be tested from read-only program directories.

---

## 210. Offline Conformance

Application installation, signature verification, and launch must be testable with networking disabled.

---

## 211. Compatibility Invariants

1. An application can exist as ordinary files in an ordinary directory.
2. Copying an application directory is a valid deployment mechanism.
3. An executable may run by path without mandatory package registration.
4. Applications may be installed in arbitrary user-selected directories.
5. A standard application directory is a convenience, not a restriction.
6. The filesystem remains visible and manageable by its user.
7. Application security does not depend on hiding application files from the machine owner.
8. User filesystem authority and application runtime authority are separate.
9. A package is an optional distribution envelope.
10. An application store is optional.
11. Non-store software is normal software, not a special sideloaded class.
12. A store does not determine whether software fundamentally has the right to exist or execute.
13. Signing is optional for execution on a normal user-owned WitOS system.
14. Cryptographic signing must be easy and inexpensive for individual developers.
15. Basic signing must not require a commercial certificate authority.
16. Basic signing must not require a hardware token.
17. Hardware-backed signing remains available as a stronger optional mechanism.
18. Signature presence, integrity, publisher identity, verification, trust, and runtime authority are separate concepts.
19. Self-created signing identities are supported.
20. TOFU-style publisher trust is supported.
21. Publisher-key continuity across versions is a first-class security property.
22. Key rotation and revocation must be supported.
23. Application-store verification is one possible trust assertion, not the root of all software identity.
24. Signature verification should work offline wherever possible.
25. Transparency and build attestations are optional strengthening mechanisms.
26. Unsigned software may run.
27. Unsigned does not mean malicious.
28. Signed does not mean safe.
29. A valid self-signed application is signed even when its publisher identity is unverified.
30. Unsigned executable software must be visibly identifiable by the shell.
31. The reference graphical indication for unsigned software is a red circular corner badge on its icon.
32. Signature-invalid or modified signed software must be distinguishable from merely unsigned software.
33. The application cannot remove, falsify, or opt out of its system trust-state indicator.
34. Trust-state presentation must remain accessible without relying solely on color.
35. Runtime resource authority remains capability-based regardless of signing or publisher trust.
36. Development builds require no packaging, signing, or store process.
37. `dotnet build` and `dotnet publish` output should be directly executable when dependencies are satisfied.
38. Program files and application state are logically separate, but portable applications may intentionally combine them.
39. Application identity is independent of installation path.
40. Moving or renaming an application does not inherently change application identity.
41. Pure managed applications should normally continue targeting standard `netX.0`.
42. Application updates, downgrades, archival, and offline installation remain possible without central infrastructure.

---

## 212. Deferred Questions

### Application Manifest

```text
manifest filename
schema
ApplicationId format
entry points
handler metadata
capability declarations
publisher identity fields
```

### Software Signing Format

```text
signature algorithm agility
manifest signing
Merkle tree format
file inclusion/exclusion rules
timestamp semantics
```

### Software Trust Descriptor

```text
SignatureStatus enum
publisher identity representation
trust assertions
modification state
cache invalidation
loader integration
```

### Shell Trust Decoration

```text
canonical badge assets
graphical placement
high-contrast variants
terminal representation
accessibility semantics
```

### Publisher Identity

```text
public-key identity format
human-readable identity claims
domain verification
repository verification
organization verification
```

### Trust Store

```text
user trust
organization trust
scope
ApplicationId binding
publisher binding
installation binding
```

### Key Rotation

```text
delegation format
multiple active keys
emergency recovery
revocation
offline behavior
```

### Transparency

```text
log protocol
inclusion proofs
checkpoint distribution
privacy
offline verification
```

### Package Archive

```text
archive format
compression
metadata
hashing
streaming installation
```

### Registration

```text
application index
service registration
relocation tracking
multiple installations
```

### Updates

```text
atomic replacement
rollback
state migration
publisher continuity
release channels
```

### Store Protocol

```text
catalog
publisher verification
package retrieval
payments
reviews
updates
```

---

## 213. Example: Unsigned Utility

The user downloads:

```text
ChecksumTool.zip
```

extracts:

```text
/Tools/ChecksumTool
```

and launches it.

The shell displays the program icon with the system unsigned marker, for example a red corner badge, and may show:

```text
Signature: None
Publisher: Unknown
```

The user may still run it.

---

## 214. Example: Self-Signed Open-Source Tool

The developer creates a local signing identity and signs:

```text
MyTool 1.0
```

The user sees:

```text
Signature: Valid
Publisher: Example Developer
Identity verification: Self-asserted
Key: 7A92...
```

The application does **not** receive the unsigned red badge because the cryptographic signature is valid.

The shell may separately indicate:

```text
Publisher identity not externally verified
```

---

## 215. Example: Modified Signed Application

The user modifies one signed DLL.

The shell no longer presents the application as validly signed.

Instead:

```text
Signature: Invalid
Reason: Signed content modified
```

and displays the integrity-failure decoration.

The user may still run it according to policy.

---

## 216. Example: Verified Organization

A company proves that its signing key belongs to its domain and store publisher account.

The user may see:

```text
Signature: Valid
Publisher: Example Corp
Organization identity: Verified
Domain: example.com — Verified
Store publisher: Verified
```

No unsigned badge is displayed.

---

## 217. Example: Key Rotation

Version 3.0 is signed by Key A.

Key A authorizes Key B for version 4.0 and later.

Version 4.0 is signed by Key B.

WitOS reports:

```text
Signature: Valid
Publisher identity continuity: Valid
Signing key rotated
```

---

## 218. Example: Compromised Distribution Server

An attacker modifies application files without the signing key.

Verification reports:

```text
Signature: Invalid
Content modified
```

The shell shows the integrity-failure marker regardless of where the files are installed.

---

## 219. Example: User Fork

The user modifies an open-source signed application.

The original signature stops validating.

The user signs the modified build with their own key.

The program is now validly signed under the user's signing identity.

WitOS reports that identity accurately.

---

## 220. Example: Store Installation

A store downloads and verifies:

```text
PhotoEditor.witpkg
```

then extracts:

```text
/Programs/PhotoEditor
```

and registers launcher metadata.

The application remains ordinary files.

Its signature state is visible in the shell independently of the fact that the store installed it.

---

## 221. Example: Direct Website Installation

The same signed application is downloaded directly from the publisher website.

Its signature and publisher identity remain verifiable.

No store is required.

---

## 222. Example: Developer Workflow

Developer runs:

```text
dotnet build
```

and launches directly from:

```text
bin/Debug/netX.0/
```

The build is visibly unsigned unless signed.

For release:

```text
dotnet publish
wit sign ./publish
wit package ./publish
```

may produce the final signed distribution.

---

## 223. Example: Air-Gapped Organization

An organization maintains its own publisher trust roots.

Packages are signed externally and transferred by removable media.

Offline WitOS machines verify:

```text
content integrity
publisher identity
organization trust
```

without Internet access.

---

## 224. Example: Arbitrary Installation Location

An installer suggests:

```text
/Programs/EngineeringTool
```

The user instead selects:

```text
/Data/MyTools/EngineeringTool
```

The application works identically.

Its signing indicator is identical in both locations.

---

## 225. Example: Manual Move

The user moves:

```text
/Programs/MyApp
```

to:

```text
/Archive/Tools/MyApp
```

Direct path launch works immediately.

The signature state follows the executable content, not the previous installation path.

---

## 226. Example: Multiple Versions

The user keeps:

```text
/Tools/Compiler-12
/Tools/Compiler-13
/Tools/Compiler-nightly
```

All three may run.

Each copy independently displays its actual signature status.

---

## 227. Relationship to Future RFCs

RFC 0010 completes the initial high-level architecture set:

```text
architecture
resources
applications
security
execution
communication
hardware
storage
presentation
packaging/distribution
```

Several topics are now mature enough for dedicated lower-level RFCs.

Likely future documents include:

```text
RFC 0011 — Kernel Architecture & ABI
RFC 0012 — Driver Runtime & Device Manager
RFC 0013 — Boot, Recovery & System Updates
RFC 0014 — Distributed Resource Discovery & Placement
RFC 0015 — .NET Runtime Port & Compatibility Contract
RFC 0016 — Networking Architecture
RFC 0017 — Software Identity, Signing & Trust
```

RFC 0017 may formalize the signing, trust-store, key-rotation, verification, transparency, and system trust-decoration model introduced here.

---

## 228. Summary

WitOS deliberately preserves the freedom of traditional general-purpose computing:

```text
application
    =
ordinary files
```

The minimum deployment workflow remains:

```text
copy directory
      ↓
run executable
```

Applications may live wherever the user chooses.

The filesystem remains visible.

Packages are optional.

Installers are helpers.

Stores are optional distribution services.

Signing is optional for execution.

At the same time, WitOS makes cryptographic signing sufficiently simple that even a small open-source developer should have little reason not to use it.

And the system makes the distinction visible.

An unsigned executable is not silently mixed into the same visual category as signed software.

In the reference graphical shell:

```text
unsigned application
    ↓
red circular corner badge on application icon
```

while signed-but-unverified, signed-and-trusted, and signature-invalid applications remain distinct states.

The badge is derived from trusted system verification.

The application cannot remove it.

This preserves user freedom without hiding security-relevant information.

The trust chain may therefore be:

```text
Application Files
      ↓
Signature State
      ↓
Content Integrity
      ↓
Publisher Key
      ↓
Identity Assertions
      ↓
User / Organization / Store Trust
      ↓
Runtime Capabilities
```

The machine owner remains free to:

```text
inspect
copy
move
rename
archive
modify
sign
delete
run
```

their software.

The defining principles are:

> **WitOS should make trustworthy software easy to identify without making unapproved software impossible to run.**

and:

> **Freedom to run unsigned software must not mean hiding the fact that it is unsigned.**
