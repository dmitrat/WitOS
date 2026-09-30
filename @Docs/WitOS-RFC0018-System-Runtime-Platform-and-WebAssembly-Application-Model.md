# RFC0018 — System Runtime Platform and WebAssembly Application Model

**Project:** WitOS  
**Status:** Draft v0.1  
**Scope:** User-space runtime platform, WebAssembly as a first-class application format, system-provided JavaScript/WebAssembly runtimes, capability integration, browser integration, portability and versioning  
**Out of scope:** Browser DOM/CSS/layout design, kernel implementation details, a specific JavaScript engine implementation, a specific WebAssembly compiler strategy

---

## 1. Summary

WitOS should treat language and execution runtimes as reusable platform components rather than implementation details owned by individual applications.

The first three runtime families are:

1. **.NET / CoreCLR** — the primary and mandatory managed application platform.
2. **WebAssembly** — a first-class portable application and component format.
3. **ECMAScript / JavaScript** — a standard reusable scripting runtime, initially intended primarily for hosted execution such as the browser, automation, plugins and scripting.

The key architectural rule is:

> **System-provided does not mean system-centralized. Runtime implementations may be installed, versioned, discovered and updated globally, while execution instances, heaps, realms and mutable runtime state remain local to the consuming application or isolated execution context.**

A browser may use the system JavaScript and WebAssembly runtimes on WitOS, while the same portable browser engine can package the same managed runtime implementations on Windows, Linux or macOS.

This keeps the browser portable while allowing substantially deeper integration on WitOS.

WebAssembly should additionally become a native WitOS application format:

> **A `.wasm` application should be able to run directly on WitOS without Linux, Windows, POSIX emulation, a foreign kernel, Chromium, or another browser runtime.**

WASI and the WebAssembly Component Model should be supported through adapters onto the WitOS resource and capability model rather than by turning WitOS into a Unix compatibility environment.

---

## 2. Motivation

A modern browser contains several subsystems that are useful far beyond the browser itself:

- JavaScript execution;
- WebAssembly validation and execution;
- compilation and code caching;
- module loading;
- cryptography and compression;
- URL and encoding support;
- resource isolation;
- diagnostics and profiling.

If each browser, scripting tool, plugin host and application brings its own copy of these facilities, the platform gains duplicated code, duplicated security surfaces and incompatible execution models.

WitOS already intends to make standard .NET a platform-level contract. The same architectural principle can be extended selectively to other independently useful runtimes.

The desired relationship is:

```text
Applications
    │
    ├── ordinary .NET applications
    ├── WebAssembly applications
    ├── JavaScript hosts / scripting tools
    └── managed browser
            │
            ├── DOM / CSS / Layout / Web APIs
            ├── JavaScript realms
            └── browser WebAssembly instances
                    │
                    ▼
             Runtime Platform
                    │
        ┌───────────┼───────────┐
        ▼           ▼           ▼
      .NET      JavaScript   WebAssembly
        │           │           │
        └───────────┴───────────┘
                    │
          WitOS resources/capabilities
```

This can make the browser smaller, make WASM useful independently of the browser, and create a general portable execution platform rather than a collection of unrelated application-specific runtimes.

---

## 3. Non-goals

This RFC does **not** propose:

- putting JavaScript or WebAssembly into the kernel;
- making the GUI shell depend on JavaScript;
- making the browser non-removable;
- providing browser-only privileged system calls;
- creating one global JavaScript process that executes code for every application;
- creating one global WebAssembly process that executes all WASM modules;
- making WitOS emulate Linux, POSIX or Windows for WASM;
- exposing the browser DOM as an operating-system service;
- requiring JavaScript or WebAssembly in minimal/server WitOS installations;
- replacing standard .NET as the primary WitOS development platform.

The kernel remains unaware of JavaScript, WebAssembly, DOM, WASI and browser concepts.

---

## 4. Architectural principles

### 4.1 .NET remains the primary platform

The WitOS system itself should remain predominantly implemented in standard C#/.NET.

CoreCLR support remains a hard platform requirement.

```text
WitOS Core
    │
    ├── CoreCLR / standard .NET       mandatory
    ├── WebAssembly Runtime           optional standard component
    └── JavaScript Runtime            optional standard component
```

A minimal headless installation may contain only CoreCLR and normal system services.

A desktop profile may install all three.

### 4.2 Globally available, locally instantiated

A runtime can be globally installed and discoverable without becoming a global execution daemon.

Bad model:

```text
Browser Tab A ─┐
Browser Tab B ─┼──── IPC ───► GlobalJavaScriptService
Application C ─┤
Application D ─┘
```

JavaScript property access, function calls and WebAssembly instructions cannot cross IPC boundaries efficiently, and such a design would create an unnecessary shared failure and security domain.

Preferred model:

```text
               Installed JavaScript Runtime
                         │
          ┌──────────────┼──────────────┐
          ▼              ▼              ▼
   WebContent A     WebContent B    Application C
      Realm             Realm           Realm
      Heap              Heap            Heap
      JIT state         JIT state       JIT state

               Installed WebAssembly Runtime
                         │
          ┌──────────────┼──────────────┐
          ▼              ▼              ▼
      WASM App       Browser WASM    Plugin Host
      Instance         Instance        Instance
```

Immutable implementation files may be shared by the OS page cache. Mutable execution state remains isolated.

### 4.3 Host capabilities define authority

A language runtime must not automatically grant operating-system authority.

A fresh JavaScript realm has language semantics but no filesystem, network, camera or process access.

A fresh WebAssembly instance has computation and memory semantics but no ambient OS authority.

Authority enters through explicit host bindings.

```text
Language Runtime
      │
      ▼
Host Binding
      │
      ▼
Delegated WitOS Capability
      │
      ▼
Resource
```

This rule is especially important for WebAssembly because its portability otherwise encourages accidental recreation of a global Unix-like ambient environment.

### 4.4 Browser-specific semantics stay in the browser

The system JavaScript runtime should implement ECMAScript.

It should **not** implement:

- `window`;
- `document`;
- HTML elements;
- browser navigation;
- `navigator`;
- browser `fetch`;
- browser storage;
- browser permission semantics.

Those are supplied by the browser host:

```text
System JavaScript Runtime
        │
        └── ECMAScript
               │
Browser bindings
        │
        ├── Window
        ├── Document
        ├── EventTarget
        ├── fetch
        ├── navigator
        └── Web APIs
```

This keeps the JavaScript runtime reusable outside the browser.

### 4.5 No browser-only system privilege

Deep browser integration must use normal public WitOS APIs.

If the browser can request a presentation surface, camera capability, sandboxed execution context or compute resource, another authorized application must be able to request the same type of resource.

The platform must never contain logic equivalent to:

```text
if application == DefaultBrowser:
    allow privileged operation
```

The browser may be specially optimized, but it must not possess magical authority.

---

## 5. Runtime platform model

The following API is illustrative rather than normative:

```csharp
public interface IRuntimeProvider
{
    RuntimeDescriptor Descriptor { get; }

    bool Supports(RuntimeRequirement requirement);

    ValueTask<IRuntimeInstance> CreateAsync(
        RuntimeInstanceOptions options,
        CancellationToken cancellationToken = default);
}

public interface IRuntimeInstance : IAsyncDisposable
{
    RuntimeDescriptor Runtime { get; }
}

public sealed record RuntimeRequirement(
    string Family,
    VersionRange? Version = null,
    IReadOnlySet<string>? Features = null);
```

Possible runtime families:

```text
dotnet
wasm
ecmascript
```

Provider implementations may be:

```text
OutWit.OS.Runtime.DotNet
OutWit.OS.Runtime.WebAssembly
OutWit.OS.Runtime.JavaScript
```

The exact package and namespace layout remains open.

The important point is that applications request capabilities and semantic features rather than implementation brands.

For example:

```text
Family: wasm
Features:
    simd
    threads
    component-model
```

rather than:

```text
Engine: SomeSpecificWasmEngine 4.3
```

An application may still pin an implementation/version for reproducibility when required.

---

## 6. Runtime discovery and acquisition

Runtime discovery and runtime authority should follow the normal WitOS resource model.

Conceptually:

```text
Discover runtime providers
        ↓
select compatible provider
        ↓
policy
        ↓
acquire execution capability
        ↓
create local runtime instance
```

A runtime provider is a resource.

Possession of a runtime identifier is not authority to execute arbitrary code with arbitrary capabilities.

Execution policy may consider:

- application identity;
- runtime family;
- trust/signature state;
- requested features;
- requested executable-memory access;
- requested resource capabilities;
- quotas;
- origin, in browser-hosted cases;
- device policy.

---

## 7. WebAssembly as a first-class WitOS application format

### 7.1 Application model

WebAssembly should be able to serve as an ordinary application entry format.

A directory might contain:

```text
MyTool/
├── MyTool.wasm
├── assets/
└── optional application manifest
```

Launching the application should conceptually be equivalent to:

```text
Application
    ↓
WebAssembly Runtime
    ↓
WebAssembly Instance
    ↓
host imports backed by delegated capabilities
```

No browser is involved.

No Linux guest is involved.

No Windows compatibility layer is involved.

No foreign kernel is involved.

### 7.2 `.wasm` is architecture-neutral source executable state

The portable WebAssembly module/component is the authoritative executable artifact.

Machine-specific compiled code is cache.

```text
Portable .wasm
      │
      ├── x64 compiled cache
      ├── arm64 compiled cache
      └── riscv64 compiled cache
```

Deleting the cache must never make the application undeployable.

This preserves the strongest property of WebAssembly for WitOS: the same application artifact can move between heterogeneous machines.

### 7.3 Application identity remains independent of file format

A WebAssembly application is still a WitOS `Application`.

Its identity must not be derived merely from the hash or path of its `.wasm` file.

The existing distinction remains:

```text
ApplicationId
    logical stable application identity

InstallationId
    one installed/copied instance

ApplicationInstance
    one active execution
```

A WebAssembly application participates in ordinary WitOS lifecycle, trust, presentation and capability models.

---

## 8. WASI integration

WitOS should support relevant WASI contracts, but WASI must be implemented as an adapter onto native WitOS concepts.

Conceptually:

```text
WebAssembly application
        ↓
       WASI
        ↓
WitOS WASI Adapter
        ↓
capabilities/resources
        ↓
WitOS
```

Not:

```text
WebAssembly application
        ↓
       WASI
        ↓
fake Linux
        ↓
POSIX syscall emulation
        ↓
WitOS
```

Where WASI exposes handle-oriented access, those handles can naturally correspond to delegated authority.

For example:

```text
WASI directory handle
        ↓
delegated storage capability

WASI socket permission
        ↓
delegated network capability

WASI clock
        ↓
clock/timer capability

WASI random
        ↓
entropy capability
```

This is a good fit because modern WASI is already oriented toward explicit host-provided resources rather than universal ambient access.

---

## 9. No ambient filesystem or network for WebAssembly

A standalone WASM application should not automatically receive:

```text
/
C:\
all network interfaces
all environment variables
all devices
```

Instead, the launcher supplies explicit capabilities according to policy and user grants.

Example:

```text
PhotoConverter.wasm

granted:
    Read  /Photos/Incoming
    Write /Photos/Converted

not granted:
    Network
    Camera
    Other user files
```

The WASI adapter exposes only those resources.

This allows a WebAssembly application to be portable **and** strongly sandboxed without inventing a second browser-style permission system.

---

## 10. Browser-hosted WebAssembly is a separate security profile

Browser WebAssembly and native WitOS WebAssembly must not be conflated.

A web page containing WebAssembly:

```text
https://example.com/app.wasm
```

must **not** gain standalone WASI authority merely because the system has a native WASM runtime.

Browser-hosted execution is:

```text
Web page
    ↓
Browser WebAssembly bindings
    ↓
system WASM runtime implementation
    ↓
browser-controlled imports only
```

The browser remains the host and security authority for the web execution environment.

No filesystem/network/process capabilities are inherited from the browser process unless explicitly represented through Web APIs and browser policy.

---

## 11. WebAssembly Component Model

WitOS should track and support the WebAssembly Component Model as it matures.

The Component Model is especially attractive because it can provide typed interfaces between:

- WebAssembly components;
- .NET hosts;
- WitOS services;
- plugins;
- application components.

Potential structure:

```text
WASM Component
      ↓
typed imported interface
      ↓
generated managed adapter
      ↓
WitOS capability/service
```

This may become an excellent plugin mechanism for software that does not require full CLR access.

### Naming note

The WebAssembly ecosystem uses **WIT** for *WebAssembly Interface Types*. This acronym is unrelated to WitOS/OutWit. Documentation should spell out “WebAssembly Interface Types (WIT)” where ambiguity is possible.

---

## 12. JavaScript as a system runtime

JavaScript should initially be treated as a reusable hosted runtime rather than necessarily as a primary top-level application format.

Expected consumers include:

- the managed browser;
- automation;
- scripting tools;
- plugin systems;
- test infrastructure;
- development tools;
- user-created scripts.

Conceptually:

```csharp
await using var runtime = await runtimeManager.CreateAsync(
    new RuntimeRequirement("ecmascript"));

var realm = runtime.CreateRealm();
realm.SetHostObject("host", someExplicitBinding);
await realm.EvaluateAsync("host.write('hello')");
```

A plain realm has no operating-system APIs unless the host injects them.

---

## 13. JavaScript command-line experience

A desktop/developer installation may expose:

```text
js script.js
```

or an equivalent PowerShell command.

The important semantics are:

```text
JavaScript language
+
explicit host profile
```

rather than Node.js compatibility by default.

Optional compatibility profiles could later provide:

- a Web-like profile;
- a Node-compatible subset;
- a WitOS automation profile;
- a minimal pure-ECMAScript profile.

These profiles must be clearly separated because their authority and APIs differ substantially.

---

## 14. Browser integration

The browser should remain a portable standard .NET application.

Its architecture should permit:

```text
Managed Browser Engine
    │
    ├── HTML / DOM
    ├── CSS / Layout
    ├── Web APIs
    ├── Navigation
    ├── Rendering
    │
    ├── IJavaScriptRuntimeProvider
    └── IWebAssemblyRuntimeProvider
```

On WitOS:

```text
Browser
   ↓
system runtime providers
```

On other .NET platforms:

```text
Browser
   ↓
packaged managed runtime providers
```

Ideally these are the same implementation assemblies delivered differently.

This preserves:

```text
same browser engine
same JavaScript implementation
same WebAssembly implementation
different deployment/composition root
```

---

## 15. Why this improves browser portability

The browser must not reference WitOS-specific APIs in its core engine.

Recommended split:

```text
Browser.Core                  netX.0
Browser.Html                  netX.0
Browser.Dom                   netX.0
Browser.Css                   netX.0
Browser.Layout                netX.0
Browser.WebApi                netX.0
Browser.JavaScript.Binding    netX.0
Browser.WebAssembly.Binding   netX.0

Browser.Platform.Abstractions netX.0
Browser.Platform.Generic      netX.0
Browser.Platform.WitOS        netX.0 + OutWit.OS.*
```

A browser built and tested on Windows should be able to use the same portable assemblies on WitOS.

Deep WitOS integration belongs in the provider layer, not in DOM/layout/JavaScript semantics.

---

## 16. Runtime versioning

System runtimes are dangerous if “one globally installed version” becomes an ABI trap.

WitOS should therefore support side-by-side runtime versions.

```text
JavaScript Runtime
    v1
    v2
    v3

WebAssembly Runtime
    v1
    v2
```

Applications should normally request semantic capabilities rather than exact versions:

```text
ECMAScript >= required language level
WASM + SIMD + threads
```

A browser may choose to pin a particular runtime release because web compatibility can depend on exact engine behavior.

Updates should be independent of the WitOS kernel and, where practical, independent of the browser.

---

## 17. Compilation strategy

This RFC does not mandate an implementation strategy.

A pure managed runtime may evolve through several stages:

```text
Interpreter
    ↓
baseline managed compiler
    ↓
tiered compiler
    ↓
optimized generated IL
    ↓
CoreCLR JIT
```

A promising long-term approach for both JavaScript and WebAssembly is to leverage the existing CoreCLR JIT rather than build a complete machine-code backend immediately.

Possible managed mechanisms include:

- `DynamicMethod`;
- `Reflection.Emit`;
- dynamically generated assemblies;
- source/generated IL;
- interpreter fallback.

This reinforces the importance of WitOS supporting full dynamic CoreCLR/JIT behavior.

NativeAOT-only execution is insufficient for this model.

---

## 18. WebAssembly execution pipeline

A possible pipeline is:

```text
.wasm
  ↓
decode
  ↓
validate
  ↓
canonical internal representation
  ↓
interpreter / baseline execution
  ↓
profile hot functions
  ↓
compile to managed IL
  ↓
CoreCLR JIT
  ↓
machine code
```

This is not yet a committed implementation design.

The acceptance requirement is semantic compatibility and sandboxing, not a particular compiler architecture.

---

## 19. Shared compilation and code caches

The platform may provide system-wide cache infrastructure without sharing mutable runtime state.

Example cache key:

```text
module/content hash
+
runtime implementation/version
+
target architecture
+
enabled WASM features
+
compiler mode
+
security/trust profile
```

Possible cached artifacts:

- validated WASM metadata;
- decoded bytecode;
- generated IL;
- compiled machine code where safely reusable;
- JavaScript parse/bytecode cache.

However, browser caches require stronger partitioning.

A universal cross-origin JavaScript/JIT cache can create privacy or side-channel problems.

Browser-originated caches should be partitioned by appropriate security keys such as profile/origin/site policy.

---

## 20. Runtime service vs runtime execution

Some functionality can legitimately be system services:

```text
Runtime Registry
Runtime Version Manager
Runtime Update Service
Compilation Cache
Diagnostics Registry
Module Metadata Cache
Policy
```

But instruction execution should normally remain in the application isolation domain.

Therefore:

```text
global management
+
local execution
```

is the default model.

---

## 21. Isolation

Runtime type safety is not a substitute for OS isolation.

A browser renderer using managed JavaScript or WebAssembly should still be placed in an isolated execution context.

Likewise, an untrusted standalone WASM application should not simply run inside a privileged system process because WebAssembly validation exists.

Defense in depth:

```text
WASM/JS language safety
        +
runtime validation
        +
address-space/process isolation
        +
capability restrictions
        +
resource quotas
```

---

## 22. Resource limits

Runtime instances should support policy-defined limits such as:

- maximum linear memory;
- managed heap budget;
- CPU quota or scheduling class;
- maximum thread count;
- maximum executable-code cache;
- timer precision;
- filesystem/storage quota;
- network scope;
- execution deadline;
- background execution policy.

The runtime interface should describe limits semantically, while WitOS execution/resource providers enforce what they can guarantee.

---

## 23. Application lifecycle

A runtime instance is not an application identity.

For example:

```text
Application
    persistent logical entity
        ↓
ApplicationInstance
        ↓
ExecutionContext
        ↓
WebAssembly runtime instance
```

Suspending an application does not require serializing an opaque live JIT/heap image.

Normal lifecycle should prefer:

```text
persist application state
destroy execution instance
later create new instance
reacquire authorized capabilities
restore application state
```

Special snapshotting may be added later but must not define the base model.

---

## 24. Interop with .NET

A .NET application should be able to host WebAssembly safely.

Example use cases:

- plugins;
- portable computation;
- untrusted extensions;
- user-provided formulas;
- application scripting;
- distributed tasks.

The default contract should be narrow typed imports/exports, not unrestricted CLR object exposure.

```text
.NET Host
    ↓
typed adapter
    ↓
WASM component
```

The Component Model may eventually make generated adapters the normal path.

Direct CLR reflection from arbitrary WASM should not be part of the default sandbox.

---

## 25. Distributed execution

WebAssembly is particularly interesting for WitOS because the same portable artifact can run on heterogeneous CPUs.

Potential flow:

```text
Application submits WASM computation
        ↓
requirements:
    WASM runtime
    SIMD
    memory >= X
    trust >= Y
        ↓
WitOS resource placement
        ↓
x64 / arm64 / riscv64 node
        ↓
local runtime compiles/executes module
```

The transferable unit remains the portable `.wasm` artifact rather than machine code.

This could become a natural execution format for some OmnibusCloud/WitOS distributed workloads without making distributed placement a required part of the base WASM application model.

---

## 26. Browser-specific opportunities on WitOS

When the managed browser uses system runtimes, it can integrate deeply without becoming part of the OS.

Examples:

### 26.1 Renderer isolation

```text
Browser Host
    ├── WebContent A
    ├── WebContent B
    ├── Network Service
    └── GPU/Compositor Service
```

Each receives explicit capabilities.

### 26.2 Web permissions

```text
getUserMedia()
     ↓
browser permission bridge
     ↓
trusted WitOS consent UI
     ↓
delegated Camera/Microphone capability
```

### 26.3 Installed web applications

A PWA/web application may become a real WitOS `Application` while still using the browser engine.

### 26.4 WebAssembly

Browser WebAssembly uses the same system runtime implementation, but with the browser security host profile rather than standalone WASI authority.

---

## 27. System update and security consequences

Making runtimes system components creates both advantages and risks.

Advantages:

- one security update can protect multiple consumers;
- smaller application distributions;
- consistent diagnostics;
- shared conformance testing;
- less duplicated runtime code.

Risks:

- a runtime vulnerability has a larger blast radius;
- an incompatible update can affect many applications;
- browser behavior may change when the JS runtime changes.

Mitigations:

- side-by-side versions;
- explicit runtime requirements;
- strong process/address-space isolation;
- rapid independent runtime updates;
- compatibility test suites;
- browser ability to pin a tested runtime version;
- rollback support.

---

## 28. Packaging and deployment

A WebAssembly application should preserve the general WitOS packaging philosophy:

> Copying an application directory must remain a valid deployment mechanism unless the application explicitly requires installation-time registration.

A `.wasm` file may therefore be runnable directly.

An optional manifest can add:

- stable `ApplicationId`;
- human-readable metadata;
- entry point;
- runtime requirements;
- requested capabilities;
- associations;
- presentation preferences;
- trust/signature metadata.

The manifest must not become mandatory merely to execute a simple command-line WASM module.

---

## 29. Illustrative manifest

The syntax is deliberately provisional:

```yaml
application:
  id: org.example.photo-converter
  entry:
    kind: wasm
    path: PhotoConverter.wasm

runtime:
  family: wasm
  features:
    - simd

capabilities:
  optional:
    - storage.open
```

This describes requirements and requested authority; it does not embed live capabilities.

Live authority is always issued by the running system.

---

## 30. Command-line model

Possible user experience:

```powershell
PS> ./hello.wasm
Hello from WebAssembly

PS> wasm run tool.wasm

PS> js script.js
```

File association may make direct `.wasm` execution convenient while internally resolving the appropriate runtime provider.

The command line must not imply POSIX process semantics where they conflict with the native WitOS application model.

---

## 31. Acceptance tests

### 31.1 WebAssembly portability

Build one standard WASM program outside WitOS.

Copy the identical `.wasm` file to:

- WitOS x64;
- WitOS arm64;
- another supported host.

It must execute without source recompilation.

### 31.2 Capability isolation

Run a WASM module with no storage capability.

It must not read arbitrary user files.

Grant access to one directory/resource.

It must access that resource without gaining access to unrelated storage.

### 31.3 Network isolation

A WASM application without a network capability must not open arbitrary connections.

### 31.4 Browser separation

Load arbitrary web-hosted WASM in the browser.

The module must not gain standalone WASI filesystem/network capabilities.

### 31.5 JavaScript realm isolation

Create two JavaScript realms in separate application contexts.

Mutating globals in one must not mutate the other.

### 31.6 Host API separation

Plain system JavaScript:

```javascript
typeof document
```

must not expose browser DOM unless a browser host installed those bindings.

### 31.7 Side-by-side runtimes

Install two compatible runtime versions.

An application pinned to the older version must continue to run while another application uses the newer version.

### 31.8 Browser portability

The browser's core assemblies should run on Windows and WitOS without requiring WitOS-specific references in the browser engine projects.

Only provider/host composition should differ.

---

## 32. Proposed implementation milestones

This work should not precede the standard .NET/CoreCLR milestone. The runtime platform depends on the execution semantics WitOS is already required to support.

### R0 — Runtime abstractions

- runtime descriptors;
- requirement matching;
- provider discovery;
- local instance lifecycle;
- hosted implementation on Windows for tests.

### R1 — Minimal WebAssembly runtime

- decode;
- validate;
- instantiate;
- execute basic modules;
- no ambient host capabilities;
- interpreter acceptable.

### R2 — Capability-backed WASI subset

- console;
- clocks;
- entropy;
- explicitly delegated storage;
- explicit network access where applicable.

### R3 — First-class WASM application launch

- `.wasm` association;
- Application/ApplicationInstance integration;
- optional manifest;
- lifecycle;
- quotas;
- trust integration.

### R4 — WebAssembly Component Model

- typed imports/exports;
- generated C# adapters;
- WitOS service/capability bindings.

### R5 — Performance

- tiered execution;
- generated IL/CoreCLR JIT experiments;
- compilation cache;
- SIMD;
- threads where semantics permit.

### R6 — System JavaScript runtime

- ECMAScript conformance;
- realm isolation;
- host binding API;
- CLI/scripting profile;
- diagnostics;
- versioning.

### R7 — Browser runtime integration

- browser uses the same JS/WASM provider contracts;
- browser-hosted security profiles;
- origin-partitioned caches;
- Web API bindings remain browser-owned.

The ordering of R4–R7 may change as the browser project develops.

---

## 33. Relationship to the WitOS roadmap

This RFC extends rather than changes the existing architectural direction.

It depends strongly on:

- full CoreCLR/JIT support;
- executable-memory/W^X mechanisms;
- normal .NET threading and GC support;
- resource/capability model;
- application identity/lifecycle;
- execution contexts and isolation;
- storage/network/presentation providers.

The first-class WASM application model should therefore be considered a **post-CoreCLR platform capability**, not a prerequisite for bringing up the OS.

Conceptually:

```text
M6 Standard .NET Platform
        ↓
Runtime Platform foundation
        ↓
WebAssembly first-class applications
        ↓
JavaScript system runtime
        ↓
Managed browser integration
```

Some implementation work may happen in parallel on hosted Windows/Linux test backends.

---

## 34. Open questions

1. Should JavaScript ever become a direct first-class application format, or remain primarily a hosted scripting runtime?
2. Which WASI surface should be considered the minimum supported compatibility contract?
3. How much of the WebAssembly Component Model should be exposed directly to ordinary WitOS services?
4. Should the default WASM runtime compile to managed IL, use an interpreter plus tiering, or support multiple execution backends?
5. How should runtime provider selection interact with application signing and trust?
6. Which runtime caches are safe to share across applications?
7. How should browser-origin partitioning affect shared compilation caches?
8. Should RuntimeProvider itself be exposed as a normal `Resource`/`Capability`, or resolved through a higher-level application launcher service?
9. How should runtime requirements be represented in the optional WitOS application manifest?
10. What is the smallest WebAssembly feature set required for the first useful standalone WitOS application?
11. How should debugger/profiler protocols unify .NET, JavaScript and WebAssembly execution?
12. Should distributed WebAssembly placement be a generic execution feature or an OutWit higher-level service built on top?

---

## 35. Architectural invariants

The following should be treated as hard constraints unless a later RFC explicitly revises them.

> **The WitOS kernel does not know about JavaScript, WebAssembly or browser concepts.**

> **.NET remains the mandatory primary managed platform; additional runtimes are user-space platform components.**

> **System-provided runtimes are globally discoverable but execution state is locally instantiated and isolated.**

> **A language runtime grants computation, not ambient operating-system authority.**

> **WebAssembly is a first-class portable WitOS executable/application format, not a synonym for browser content.**

> **WASI is adapted onto WitOS capabilities; WitOS does not become a Unix compatibility layer in order to support WASI.**

> **Browser-hosted WebAssembly never automatically inherits standalone WASI/system authority.**

> **Browser DOM/Web APIs remain browser-owned even when JavaScript execution is provided by a system runtime.**

> **The browser may integrate deeply with WitOS but must remain removable, replaceable and portable.**

> **No browser-only privileged API is permitted; deep integration must use public capability-based WitOS services.**

> **Portable runtime artifacts are authoritative; architecture-specific compiled output is cache.**

> **Runtime versions may exist side by side; a single global runtime version must not become a platform-wide compatibility trap.**

---

## 36. Long-term platform view

If successful, the WitOS execution platform becomes:

```text
                         Applications
                              │
          ┌───────────────────┼───────────────────┐
          │                   │                   │
       .NET apps           WASM apps         Web applications
          │                   │                   │
          ▼                   ▼                   ▼
       CoreCLR          WASM Runtime        Managed Browser
                                                │
                                     ┌──────────┴──────────┐
                                     ▼                     ▼
                               JS Runtime             WASM Runtime
          │                   │                   │
          └───────────────────┴───────────────────┘
                              │
                    WitOS resources/capabilities
                              │
                         WitOS kernel
```

This gives WitOS access to three large software models without hosting or virtualizing another operating system:

- the existing .NET ecosystem;
- portable WebAssembly software;
- the web application ecosystem through a managed browser.

That combination can become one of the central platform advantages of WitOS rather than merely an implementation convenience.
