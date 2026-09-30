# WitOS Developer Experience Manifesto
## Standard .NET First
### Draft v0.1

## 1. Principle

WitOS must not require developers to leave the .NET ecosystem in order to use WitOS.

A developer should be able to continue using:

```text
Visual Studio
Rider
VS Code
dotnet CLI
MSBuild
NuGet
Roslyn
NUnit / xUnit
profilers
debuggers
existing CI systems
```

on the operating system they already use.

WitOS is not intended to create a new C# ecosystem.

WitOS is intended to become another first-class platform for the existing .NET ecosystem.

> **WitOS must not require WitOS as a development environment.**

---

# 2. The Compatibility Promise

A portable managed application targeting a supported standard .NET TFM should run on WitOS without recompilation, provided it does not depend on platform-specific native code or platform-specific APIs.

For example, an application developed on Windows:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>

</Project>
```

should be buildable in Visual Studio and then moved to WitOS as ordinary managed output:

```text
MyApplication.dll
MyApplication.deps.json
MyApplication.runtimeconfig.json
other portable managed assemblies
```

and launched as:

```text
dotnet MyApplication.dll
```

without rebuilding the application specifically for WitOS.

The application should not need to know that WitOS exists.

---

# 3. Source Compatibility Is Not Enough

The WitOS goal is stronger than:

> "The same source code can be recompiled for WitOS."

The intended contract is:

> **Portable managed binaries should remain portable binaries.**

The reference workflow is:

```text
Windows / Linux / macOS
        ↓
ordinary .NET build
        ↓
portable managed assemblies
        ↓
copy to WitOS
        ↓
run unchanged
```

This should become a formal compatibility test.

A credible milestone is not:

```text
WitOS can compile a C# Hello World.
```

A credible milestone is:

```text
An existing .NET application,
built on another operating system,
runs unchanged on WitOS.
```

---

# 4. Full .NET Means Full .NET

WitOS does not define .NET compatibility as "C# can execute."

It does not define compatibility as "NativeAOT-compatible code can execute."

The long-term platform requirement is a real upstream-compatible .NET runtime with the normal dynamic features developers expect.

That includes, where supported by upstream .NET:

```text
CoreCLR
JIT compilation
GC
Thread
ThreadPool
Task
exceptions
runtime generics
reflection
Assembly.Load
AssemblyLoadContext
dynamic
DynamicMethod
Reflection.Emit
expression tree compilation
runtime-generated code
normal metadata
normal BCL semantics
```

This distinction is fundamental:

```text
C# execution environment
        ≠
.NET platform
```

WitOS aims to be the latter.

---

# 5. NativeAOT Is a Tool, Not the Application Model

NativeAOT is extremely valuable to WitOS.

It is especially useful for:

```text
early system bring-up
system services
small trusted components
bootstrapping
low-overhead utilities
appliance-style deployments
components that benefit from static compilation
```

But NativeAOT must not define what applications are allowed to be.

The architecture should distinguish:

```text
WitOS implementation technology
        from
WitOS application compatibility
```

The system may contain many NativeAOT components while still hosting a full CoreCLR runtime for ordinary applications.

> **NativeAOT is a supported deployment model and an important system implementation technology, but it does not define the WitOS application model.**

---

# 6. Upstream .NET Is the Runtime

WitOS should not create a permanently separate .NET dialect.

The long-term target is:

```text
upstream dotnet/runtime
        ↓
WitOS platform adaptation layer
        ↓
WitOS kernel and services
```

not:

```text
custom CoreLib
custom System.*
custom runtime semantics
custom managed ecosystem
```

A private compatibility layer may be useful during bootstrap, but it must not become the permanent platform contract.

The strategic goal should be to minimize the WitOS-specific delta required by upstream .NET.

This implies:

```text
standard C# semantics
standard BCL behavior
standard metadata
standard GC behavior
standard Thread/Task behavior
standard reflection behavior
standard dynamic-code behavior
```

wherever upstream .NET defines them.

---

# 7. WitOS APIs Are Additive

Applications must not be required to reference `OutWit.OS.*` merely to run on WitOS.

An ordinary application should be able to remain:

```text
ordinary .NET application
        ↓
standard .NET APIs
        ↓
WitOS
```

WitOS-specific APIs exist only when an application wants capabilities beyond standard .NET.

Examples include:

```text
typed resources
capability acquisition
resource placement
CPU topology
distributed execution
remote resources
application migration
explicit locality
WitOS presentation capabilities
```

The relationship should be:

```text
standard .NET
    +
optional OutWit.OS.* capabilities
```

not:

```text
OutWit.OS.* required for all applications
```

> **WitOS-specific APIs are additive. Using them must not be a prerequisite for running ordinary .NET software.**

---

# 8. No Mandatory WitOS Project Type

Ordinary applications should continue to use:

```xml
<Project Sdk="Microsoft.NET.Sdk">
```

A special WitOS SDK should not be required simply because the application may run on WitOS.

Likewise, a new target framework should not be required merely to identify WitOS.

A portable application should normally target a standard TFM:

```text
netX.0
```

WitOS-specific packages can be added through ordinary NuGet references when needed:

```xml
<PackageReference Include="OutWit.OS.Resources" Version="..." />
```

The presence of a WitOS-specific API should be explicit and optional.

---

# 9. Development Happens Where the Developer Is Comfortable

A developer writing a WitOS-compatible application should be able to remain on:

```text
Windows
Linux
macOS
```

using the tools already familiar to them.

A typical development loop should be:

```text
Visual Studio / Rider / VS Code
        ↓
edit
        ↓
build
        ↓
unit test
        ↓
debug locally
        ↓
deploy portable assemblies
        ↓
run on WitOS
```

WitOS should not impose:

```text
a custom IDE
a custom editor
a custom compiler frontend
a custom source language
a mandatory WitOS development machine
```

Developer comfort is not cosmetic.

It is part of the adoption strategy.

---

# 10. Hosted Providers Are a Core Development Tool

Where practical, WitOS system libraries and WitOS-aware applications should support hosted providers on existing operating systems.

For example:

```text
OutWit.OS.Storage.Ext4
        │
        ├── FileBlockStorage on Windows/Linux
        │       ↓
        │    unit/integration tests
        │
        └── VirtioBlockStorage on WitOS
                ↓
             real system
```

Likewise:

```text
OutWit.OS.ResourceManager
OutWit.OS.Network
OutWit.OS.Execution
OutWit.OS.ApplicationModel
```

should separate portable managed logic from the platform provider whenever possible.

This allows the same code to be:

```text
tested under normal .NET
debugged with mature tools
profiled with existing profilers
covered by normal test frameworks
```

before it is executed inside WitOS.

This follows the same OutWit architectural rule used elsewhere:

> **Depend on what a component can do, not on which implementation happens to provide it.**

---

# 11. Visual Studio Integration Should Be Integration, Not Reinvention

WitOS may eventually need IDE integration for:

```text
deploy
run
attach debugger
remote logs
resource inspection
device selection
VM selection
profiling
```

But this should look like:

```text
Visual Studio
    +
WitOS extension / tooling
```

not:

```text
WitOS IDE
```

The IDE remains the developer's IDE.

WitOS supplies platform integration.

The desired experience is similar in spirit to developing for:

```text
remote Linux
WSL
Docker
Android
cloud targets
```

where the host development environment remains familiar.

---

# 12. The Binary Portability Boundary

WitOS does not promise that every artifact produced on another operating system will run unchanged.

The compatibility boundary must be precise.

A normal portable managed application should work.

Applications may require adaptation if they depend on:

```text
Win32 P/Invoke
COM
Windows Registry
Windows Services
WPF
WinForms
DirectX-specific code
Linux-specific syscalls
macOS-specific frameworks
native libraries without WitOS assets
self-contained win-x64 / linux-x64 runtime distributions
platform-specific apphosts
```

For example, a Windows-specific:

```text
MyApp.exe
```

does not define the portable application.

The portable managed entry point is:

```text
dotnet MyApp.dll
```

The promise applies to portable managed software, not to arbitrary foreign native binaries.

---

# 13. Compatibility Must Be Tested with Foreign Applications

WitOS compatibility should not be measured primarily with applications written specifically for WitOS.

The strongest tests are applications whose authors did not know WitOS existed.

A future compatibility suite should include existing software and libraries such as:

```text
Newtonsoft.Json
Math.NET
Microsoft.Extensions.*
Roslyn APIs
NUnit / xUnit
ASP.NET Core
database clients
serialization libraries
DI containers
reflection-heavy libraries
dynamic proxy libraries
scientific libraries
CLI applications
```

The same binaries and test suites should be exercised on:

```text
Windows
Linux
WitOS
```

with no WitOS-specific source branches.

A compatibility regression is a platform bug, not an application-porting exercise.

---

# 14. Dynamic Features Are Compatibility Tests

Simple managed programs can hide missing runtime features because an optimizer may remove the feature being tested.

Therefore WitOS must explicitly test real runtime behavior.

Examples:

```text
true virtual dispatch
true interface dispatch
runtime generic instantiation
boxing/unboxing
escaping object allocation
GC roots
exceptions across call frames
delegates
reflection
Assembly.Load
AssemblyLoadContext
DynamicMethod
Reflection.Emit
expression tree compilation
dynamic binding
runtime-generated proxies
JIT code generation
```

A successful `Hello World` proves very little.

A runtime capable of executing dynamic .NET software proves platform compatibility.

---

# 15. The Kernel Must Enable CoreCLR, Not Reimplement It

Full .NET support has architectural consequences for WitOS.

The kernel and low-level services must eventually provide the primitives required by CoreCLR.

Examples include:

```text
virtual address reservation
page commitment/decommitment
memory protection changes
W^X transitions for JIT code
thread creation
thread-local storage
wait/wake primitives
timers
CPU topology
stack management
signals/fault delivery or equivalent
safe points
unwind support
executable memory
file mapping
high-resolution time
entropy
```

The design goal is not to implement managed runtime semantics in the kernel.

The design goal is to provide a clean operating-system substrate on which the upstream runtime can implement them.

---

# 16. M3 and M6 Are Different Milestones

WitOS should distinguish two very different achievements.

## M3 — Managed System

```text
WitOS can be implemented substantially in C#.
```

This milestone may rely heavily on NativeAOT.

It proves that managed code can participate in the implementation of the operating system.

It does **not** yet prove general .NET compatibility.

## M6 — Standard .NET Platform

```text
An ordinary portable .NET application,
built on another operating system,
runs unchanged on WitOS under CoreCLR/JIT.
```

This is the platform-identity milestone.

A representative M6 demonstration should include:

```text
JIT startup
GC
threads
Task/ThreadPool
reflection
runtime generics
assembly loading
dynamic code generation
ordinary NuGet libraries
```

M3 says:

> WitOS can use C#.

M6 says:

> WitOS is a .NET platform.

---

# 17. Ecosystem Strategy

A new operating system normally faces:

```text
no users
→ no applications
→ no users
```

WitOS should avoid creating this problem unnecessarily.

The initial software ecosystem is not empty.

It begins with the portable .NET ecosystem.

This does not mean every .NET application will work immediately.

It means that compatibility is approached by subtraction:

```text
existing .NET software
        -
platform-specific dependencies
        =
potential WitOS software
```

rather than by requiring an entirely new application ecosystem to be created from zero.

---

# 18. Adoption Principle

Trying WitOS should not require a developer to make an irreversible decision.

The ideal first experience is:

```text
I already have a .NET application.
I copied it to WitOS.
It ran.
```

Only after that should the developer need to learn:

```text
OutWit.OS.Resources
capabilities
resource topology
distributed execution
application mobility
other WitOS-native features
```

The platform should provide immediate familiarity first and additional power second.

---

# 19. Developer Experience Invariants

The following should be treated as long-term invariants.

1. **WitOS does not require WitOS as a development host.**

2. **Portable managed .NET applications should not require recompilation merely because they run on WitOS.**

3. **Upstream .NET compatibility is a platform contract, not a compatibility add-on.**

4. **NativeAOT is not the definition of the WitOS application model.**

5. **CoreCLR/JIT and dynamic .NET features are first-class long-term requirements.**

6. **Ordinary applications do not need to reference WitOS-specific packages.**

7. **WitOS-specific APIs are additive and optional.**

8. **Ordinary projects continue to use standard .NET project formats and tooling.**

9. **Existing IDEs should be extended, not replaced.**

10. **Portable system code should be testable on existing operating systems wherever practical.**

11. **Compatibility should be measured using existing third-party .NET applications, not only WitOS demos.**

12. **Platform-specific native dependencies must remain explicit rather than being hidden behind claims of universal binary compatibility.**

---

# 20. Final Statement

WitOS should not ask developers to choose between the .NET ecosystem they already use and the operating system we want to build.

The intended relationship is simpler:

```text
Existing .NET development experience
                +
Existing .NET software ecosystem
                +
WitOS operating/resource model
```

A developer should be able to open Visual Studio on Windows, write ordinary .NET code, run normal tests, use familiar packages and tools, and only encounter WitOS when the resulting software is deployed.

For software that does not require WitOS-specific features, that may be the entire story.

For software that does, WitOS should add capabilities without taking away the existing development experience.

> **The easiest way to develop for WitOS should be to keep developing normally.**

> **WitOS should become a new place where .NET runs, not a new place where developers must start over.**
