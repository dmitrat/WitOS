# RFC0020 — Terminal, Console and PowerShell Architecture

**Project:** WitOS  
**Status:** Draft v0.1  
**Scope:** Terminal and console architecture, PowerShell as the default interactive shell, shell portability, resource/capability integration, headless operation, remoting, scripting, system administration and developer experience  
**Related:** RFC0003 — Application & Lifecycle; RFC0004 — Security, Identity, Capability & Delegation; RFC0005 — Execution, Scheduling & Compute; RFC0006 — IPC; RFC0009 — Presentation, Shell & Input; RFC0015 — .NET Runtime Port & Compatibility Contract; RFC0018 — System Runtime Platform and WebAssembly Application Model; RFC0019 — Shared Platform Services for Browser, GUI and Applications

---

## 1. Summary

WitOS should treat the command-line environment as a first-class part of the operating system rather than as a legacy compatibility feature.

The default interactive shell should be **modern open-source PowerShell (PowerShell 7+) running on the standard upstream .NET runtime**.

The architectural target is:

```text
User
  |
  v
Terminal / Console Presentation
  |
  v
PowerShell
  |
  v
standard .NET
  |
  v
WitOS public APIs
  |
  +--> Resources
  +--> Applications
  +--> Capabilities
  +--> Services
  +--> Execution
  +--> Storage
  +--> Network
  +--> Devices
```

PowerShell must not be fused into the kernel, shell, desktop or browser.

It is the default command shell because it is a mature object-oriented automation environment built on .NET and therefore closely matches the intended WitOS developer and administration model.

The key principles are:

> **The terminal is a presentation resource, not the operating system shell.**

> **PowerShell is the default command shell, not a privileged system component.**

> **WitOS should integrate PowerShell directly with its resource and capability model instead of pretending to be POSIX.**

> **Standard .NET console and process semantics must remain compatible with existing .NET applications.**

> **A headless WitOS machine must be a first-class configuration, not a desktop system with the GUI disabled.**

---

# 2. Terminology

Several concepts that are often conflated should remain distinct.

## 2.1 Console

A logical text-oriented I/O endpoint.

Conceptually:

```text
stdin
stdout
stderr
control/input events
terminal characteristics
```

For standard .NET applications:

```text
System.Console
      |
      v
WitOS console/text-terminal provider
```

---

## 2.2 Terminal

A presentation endpoint capable of displaying text and receiving keyboard or equivalent input.

Examples:

```text
local graphical terminal window
serial console
remote terminal
SSH session
debug console
embedded display
```

A terminal does not have to run PowerShell.

---

## 2.3 Command shell

A program that interprets commands and orchestrates applications and resources.

The default WitOS command shell is PowerShell.

Alternative shells remain possible.

---

## 2.4 Desktop shell

The GUI environment responsible for application launching, task switching, panels, desktop UI and related presentation policy.

It is not PowerShell and must not depend on PowerShell.

---

## 2.5 System shell

This term should be avoided when it creates ambiguity.

Prefer:

```text
command shell
desktop shell
terminal
console
```

depending on what is actually meant.

---

# 3. Why PowerShell

PowerShell is a strong fit for WitOS for reasons that go beyond familiarity.

## 3.1 It is .NET-native

The intended stack is:

```text
PowerShell
    |
    v
CoreCLR
    |
    v
standard .NET libraries
    |
    v
WitOS
```

A successful PowerShell port is therefore also a substantial validation of the WitOS .NET platform.

---

## 3.2 Object pipelines match the WitOS resource model

Traditional shells are fundamentally text-oriented:

```text
command
  |
text
  |
grep/awk/sed
```

PowerShell pipelines transport objects:

```text
Get-Thing
   |
typed objects
   |
Where-Object
   |
Select-Object
```

WitOS itself is intended to expose typed resources and capabilities.

This is a natural fit.

Example:

```powershell
Get-WitResource -Kind Camera
```

should return objects describing resources rather than formatted text requiring reparsing.

---

## 3.3 PowerShell is both shell and automation language

It provides:

- interactive command use;
- scripts;
- functions;
- modules;
- structured objects;
- .NET interoperability;
- remoting concepts;
- jobs;
- error handling;
- formatting;
- serialization;
- package/module ecosystem.

This reduces the need for WitOS to invent a second administration language.

---

## 3.4 It is cross-platform and open source

The target is **PowerShell 7+**, not Windows PowerShell 5.1.

Windows PowerShell contains legacy assumptions that are not the architectural target.

---

# 4. PowerShell as an M6 Acceptance Workload

PowerShell should be treated as a major acceptance test for the standard .NET milestone.

A working modern PowerShell exercises:

```text
CoreCLR startup
JIT
GC
reflection
dynamic code
runtime generics
assembly loading
AssemblyLoadContext
threads
ThreadPool
Task/async
filesystem
network
console
process launch
serialization
formatting
module loading
native process interop
```

Therefore:

```text
CoreCLR boots
    ↓
simple app runs
    ↓
dynamic .NET features work
    ↓
PowerShell starts
    ↓
modules load
    ↓
scripts run
    ↓
external commands work
```

is a meaningful progression for WitOS.

A useful statement is:

> **PowerShell is not merely a user-facing shell; it is one of the most demanding early integration tests of the standard .NET platform contract.**

---

# 5. Terminal as a Presentation Resource

WitOS should not hard-code "console mode" as a special global operating-system state.

The presentation architecture should expose a text-terminal capability.

Possible contract:

```csharp
public interface ITextTerminal
{
    TextReader Input { get; }
    TextWriter Output { get; }
    TextWriter Error { get; }

    TerminalCapabilities Capabilities { get; }

    ValueTask SetTitleAsync(string? title);
    ValueTask SetCursorAsync(TerminalCursor cursor);
    ValueTask<TerminalSize> GetSizeAsync();
}
```

This is illustrative only.

The key point is semantic:

```text
Application
    |
    v
Text Terminal capability
    |
    +--> graphical terminal window
    +--> serial console
    +--> remote terminal
    +--> SSH PTY-like endpoint
    +--> test harness
```

---

# 6. System.Console Compatibility

Standard .NET applications must continue to use:

```csharp
Console.WriteLine("Hello");
var input = Console.ReadLine();
```

They should not need to know about `OutWit.OS.Presentation`.

Therefore:

```text
System.Console
      |
      v
.NET PAL / runtime integration
      |
      v
current WitOS text-terminal endpoint
```

WitOS-specific terminal APIs are additive.

They exist for richer behavior, not as a prerequisite for ordinary .NET console software.

This is part of the broader compatibility rule:

> **WitOS-specific APIs must not be required merely to run portable .NET software.**

---

# 7. Local GUI Terminal

The graphical terminal application should be an ordinary replaceable application.

Conceptually:

```text
Terminal Application
    |
    +--> window/presentation capability
    +--> text rendering
    +--> keyboard/input
    +--> session creation
    +--> pseudo-terminal/session endpoint
              |
              v
          PowerShell
```

The terminal application should not contain PowerShell-specific logic beyond optional integration conveniences.

It should be possible to run:

```text
PowerShell
cmd-like shell
JavaScript REPL
WASM CLI
debug monitor
custom shell
```

inside the same terminal host.

---

# 8. Headless WitOS

Headless operation is first-class.

A headless installation may have:

```text
CoreCLR
PowerShell
network services
storage
SSH/remoting
serial console
```

and no graphical compositor or desktop shell.

Example:

```text
Boot
  ↓
system services
  ↓
serial / network terminal
  ↓
PowerShell
```

This is important for:

- servers;
- appliances;
- VMs;
- cloud nodes;
- embedded devices;
- recovery environments;
- OmnibusCloud/WitCloud worker nodes.

No GUI dependency should exist in PowerShell, console I/O or basic administration.

---

# 9. PowerShell Port Strategy

The port should target upstream PowerShell with minimal downstream modifications.

Desired architecture:

```text
Upstream PowerShell
        |
        v
standard .NET
        |
        +--> portable PowerShell managed code
        |
        +--> small platform adaptation layer
                  |
                  v
                WitOS
```

The goal is not a forked language dialect.

---

## 9.1 `PowerShell-Native` / native shim

PowerShell currently contains a native portability layer for operating-system-specific functionality.

WitOS should replace or implement the required platform-specific functionality directly rather than constructing a fake POSIX environment.

Conceptually:

```text
PowerShell managed code
        |
        v
PowerShell native/platform abstraction
        |
        v
WitOS implementation
```

Where possible, functionality should move toward standard .NET APIs.

Where PowerShell truly requires platform-specific behavior, provide a narrow WitOS implementation.

---

# 10. No POSIX Compatibility Fiction

WitOS may support POSIX compatibility later as an optional subsystem, but PowerShell integration should not depend on it.

Wrong:

```text
PowerShell
   ↓
pretend WitOS is Linux
   ↓
fake POSIX layer
   ↓
WitOS
```

Preferred:

```text
PowerShell
   ↓
.NET + PowerShell platform abstraction
   ↓
native WitOS APIs
```

This avoids fossilizing Linux assumptions into the primary user interface.

---

# 11. Native WitOS PowerShell Providers

PowerShell's Provider/PSDrive architecture is unusually well suited to WitOS.

Possible native drives:

```text
Resource:
Device:
Application:
Capability:
Service:
```

Potential examples:

```powershell
PS> Get-ChildItem Resource:\

PS> Get-ChildItem Device:\Display

PS> Get-ChildItem Application:\

PS> Get-ChildItem Service:\

PS> Get-ChildItem Capability:\Current
```

These should expose real typed objects.

---

## 11.1 Resource: provider

Potential hierarchy:

```text
Resource:\
    Compute\
    Storage\
    Network\
    Presentation\
    Camera\
    Microphone\
    GPU\
```

Commands can operate on actual resources rather than textual pseudo-files.

Example:

```powershell
Get-ChildItem Resource:\Compute |
    Where-Object Availability -eq Available
```

---

## 11.2 Device: provider

Represents discoverable hardware/logical devices.

Example:

```powershell
Get-ChildItem Device:\ |
    Format-Table Id, Type, Status, Provider
```

Presence in `Device:` is not authority to use the device.

This preserves the distinction:

```text
discovery != acquisition
```

---

## 11.3 Application: provider

Represents logical applications and possibly active instances.

Possible structure:

```text
Application:\
    Installed\
    Running\
    Suspended\
```

Example:

```powershell
Get-ChildItem Application:\Running
```

This maps naturally to the WitOS application model rather than pretending every application is only a process.

---

## 11.4 Capability: provider

This area requires particular care.

The provider should not turn live authority into trivially copyable strings.

A path such as:

```text
Capability:\Current\...
```

may display the capabilities held by the current session/application, but:

```text
ResourceId != authority
path != authority
name != authority
```

PowerShell objects representing live capabilities must obey normal lifetime/delegation rules.

---

## 11.5 Service: provider

Represents discoverable system services/providers.

Possible uses:

```powershell
Get-ChildItem Service:\

Get-Item Service:\Printing

Get-Item Service:\Runtime\WebAssembly
```

Again, discovery does not imply arbitrary administrative authority.

---

# 12. Cmdlets vs Providers

Not every WitOS concept should be forced into a filesystem-like provider hierarchy.

Providers are good when hierarchical navigation is natural.

Cmdlets are better for operations.

Examples:

```powershell
Get-WitResource
Request-WitCapability
Get-WitApplication
Start-WitApplication
Suspend-WitApplication
Resume-WitApplication
Stop-WitApplication
Get-WitExecutionContext
Get-WitDevice
Get-WitService
```

The design rule should be:

> **Use providers for discoverable navigable structure; use cmdlets for semantic operations.**

---

# 13. Standard PowerShell Semantics Must Survive

WitOS integration must not break normal PowerShell usage.

Users should still be able to write:

```powershell
Get-ChildItem .
Get-Content file.txt
Copy-Item
Move-Item
Get-Process
Start-Process
Invoke-WebRequest
Invoke-RestMethod
ForEach-Object
Where-Object
Import-Module
```

where functionality is supported by the underlying standard .NET/platform contract.

WitOS-native commands are additive.

---

# 14. Filesystem Experience

WitOS intentionally does not require a Unix directory hierarchy.

PowerShell's filesystem provider should expose the actual mounted/storage namespace presented by WitOS.

The user experience may still permit familiar path syntax where appropriate.

However, the architecture should avoid making concepts such as:

```text
/bin
/usr
/etc
/dev
/proc
```

fundamental merely because other shells expect them.

---

# 15. Drive Letters and Familiar Paths

There is no architectural requirement to reproduce Windows drive letters.

However, compatibility/familiarity views may be useful.

Possible coexistence:

```text
C:\              compatibility/convenience namespace
Resource:\       native resource provider
Device:\         native device provider
Application:\    native application provider
```

A PowerShell provider already makes multiple namespaces natural.

The key principle is:

> **Compatibility views may exist without becoming the underlying operating-system model.**

---

# 16. External Command Execution

PowerShell must launch ordinary applications using the WitOS application/execution model.

Conceptually:

```text
PowerShell
    |
    v
command resolution
    |
    v
Application Launcher
    |
    v
ApplicationInstance / ExecutionContext
```

For portable .NET:

```powershell
dotnet MyApp.dll
```

must work normally.

Eventually:

```powershell
./tool.wasm
```

may invoke the standard WebAssembly runtime provider.

Native applications may also be supported according to WitOS application policy.

---

# 17. Process Compatibility

PowerShell itself and many modules expect process-like concepts.

WitOS must support standard .NET `System.Diagnostics.Process` semantics sufficiently for normal portable software.

However, the native WitOS application model remains richer:

```text
Application
  └── ApplicationInstance
         ├── zero/one/multiple execution contexts
         └── possibly multiple processes
```

Therefore:

```text
Get-Process
```

can remain a compatibility view of execution processes.

A separate:

```text
Get-WitApplication
```

exposes the logical application model.

Do not overload one concept to mean both.

---

# 18. Jobs and Background Execution

PowerShell Jobs should map onto normal PowerShell semantics first.

WitOS may additionally expose native background execution.

Possible distinction:

```text
PowerShell Job
    PowerShell execution abstraction

WitOS Background Task
    operating-system application/execution abstraction
```

Adapters may connect them, but they should not be silently conflated.

---

# 19. Remoting

Remote command execution is especially important for WitOS because servers, clusters and distributed resources are first-class scenarios.

Several levels may coexist:

```text
PowerShell remoting
SSH
WitRPC-based management
WitOS resource/application APIs
```

The preferred architecture should not require a special “remote shell operating mode”.

A remote terminal is another presentation/session endpoint.

A remote management connection can expose the same typed administration objects.

---

## 19.1 PowerShell remoting compatibility

If upstream PowerShell remoting can run over supported standard transports without invasive changes, preserve it.

SSH-based remoting is particularly attractive because it is broadly interoperable.

But WitOS may later provide a richer native management transport using WitRPC.

---

## 19.2 Native distributed resource management

Do not use PowerShell remoting as the implementation substrate for the WitOS distributed resource model.

Wrong:

```text
distributed system = remote PowerShell sessions
```

Correct:

```text
WitOS distributed resources
        |
        v
native resource APIs
        |
        +--> PowerShell cmdlets as one client
```

PowerShell is an administration surface, not the distributed runtime.

---

# 20. Security and Authority

PowerShell is powerful and therefore security-sensitive.

The command shell must not automatically become omnipotent.

A PowerShell session holds whatever capabilities were delegated to its application/session.

```text
User identity
    |
policy / login / session creation
    |
    v
PowerShell application/session
    |
    v
delegated capabilities
```

The shell does not receive universal authority merely because it is interactive.

---

## 20.1 No implicit root shell

WitOS should avoid a permanent Unix-style universal root identity as the normal administration model.

Instead, privileged operations require appropriate capabilities.

Possible flow:

```text
Request-WitCapability
       |
       v
policy / authentication / trusted consent
       |
       v
temporary scoped capability
       |
       v
administrative operation
```

A recovery/provisioning environment may possess stronger bootstrap authority, but that is distinct from ordinary interactive PowerShell.

---

## 20.2 Scripts and capability delegation

A script should not automatically inherit more authority than the hosting PowerShell session.

For especially sensitive automation, future APIs may permit creating a restricted child execution context:

```powershell
Invoke-WitRestrictedScript `
    -Script ./cleanup.ps1 `
    -Capabilities $selectedCapabilities
```

This is a potential future feature, not an initial requirement.

---

# 21. Trusted Security Prompts

PowerShell must not draw fake trusted consent dialogs inside terminal output.

If an operation requires system consent:

```text
PowerShell command
      |
      v
capability request
      |
      v
trusted WitOS consent path
      |
      v
grant/deny
```

In a GUI session, this may be trusted system presentation.

In a headless session, an appropriate authenticated text/remote interaction path is required.

---

# 22. Scripting and Modules

PowerShell modules should remain ordinary upstream modules wherever possible.

WitOS-specific functionality should be delivered as modules:

```text
OutWit.WitOS.Resources
OutWit.WitOS.Applications
OutWit.WitOS.Capabilities
OutWit.WitOS.Execution
OutWit.WitOS.Devices
```

Exact naming remains open.

This avoids modifying the PowerShell language for WitOS-specific functionality.

---

# 23. Package Management

PowerShell package/module management should not define the WitOS application package model.

These are different layers:

```text
PowerShell module/package
    PowerShell ecosystem artifact

WitOS application
    operating-system application model

NuGet package
    .NET library/tool artifact
```

They may integrate, but should remain distinct.

---

# 24. Terminal Rendering Stack

The terminal should use the same shared text and graphics services as other applications where possible.

Possible pipeline:

```text
Terminal emulator
      |
      v
terminal cell/model
      |
      v
WitOS Text / Fonts
      |
      v
GlyphRuns
      |
      v
WitOS Graphics / Presentation
```

This allows:

- Unicode;
- BiDi policy where appropriate;
- ligatures if desired;
- color fonts/emoji;
- high-DPI;
- accessibility;
- GPU acceleration.

The terminal parser/state machine itself remains a terminal concern.

---

# 25. Terminal Protocol Compatibility

A terminal application may support common terminal control sequences for compatibility with portable CLI applications.

Likely baseline:

```text
ANSI/VT-style control sequences
```

This should be treated as a compatibility protocol at the terminal boundary.

It should not define the internal WitOS presentation architecture.

Conceptually:

```text
CLI application
    |
ANSI/terminal protocol
    |
    v
Terminal Emulator
    |
    v
WitOS Presentation
```

---

# 26. XtermSharp and Similar Libraries

A managed terminal-emulator engine such as XtermSharp is useful as a bootstrap/reference candidate.

Its terminal parsing/screen model is much more relevant than its platform-specific process-launch conveniences.

Potential strategy:

```text
managed terminal emulator core
      |
      v
WitOS terminal/session adapter
```

Before adoption, verify current activity, license, Unicode behavior, performance and platform assumptions.

The public `ITextTerminal`/terminal session contract must not depend on a specific emulator library.

---

# 27. Pseudo-Terminal / Session Abstraction

Traditional platforms often expose PTYs.

WitOS should provide equivalent semantics where needed but need not inherit Unix PTY implementation details.

Required behavior includes:

- bidirectional byte/text stream;
- terminal size;
- resize notification;
- control signals/events;
- session lifetime;
- foreground interactive endpoint;
- detach/reattach if supported.

Possible architecture:

```text
Terminal App
    |
    v
InteractiveSession
    |
    +--> input stream/events
    +--> output/error
    +--> terminal characteristics
    |
    v
PowerShell / CLI application
```

This can be mapped to PTY-compatible semantics where required.

---

# 28. Serial and Recovery Console

A minimal text console should exist before the graphical desktop is available.

Early boot/recovery may use:

```text
serial
UEFI console during bootstrap
simple framebuffer text
hypervisor console
```

PowerShell itself may be too heavy for the earliest recovery stage.

Therefore distinguish:

```text
Boot/Recovery Monitor
    minimal diagnostic shell

PowerShell
    full managed command shell after CoreCLR/system services
```

Do not force PowerShell into the boot-critical path.

---

# 29. Recovery Environment

A recovery environment may contain:

```text
minimal kernel
storage
network if available
CoreCLR if healthy
PowerShell
diagnostics
update/rollback tools
```

If CoreCLR is unavailable, a smaller recovery monitor must still be able to:

- inspect boot state;
- select A/B image;
- access logs;
- repair/update system components.

Thus:

> **PowerShell is the default operational shell, but not the last-resort bootloader console.**

---

# 30. PowerShell and the Application Model

PowerShell itself is an ordinary WitOS application.

It has:

```text
ApplicationId
ApplicationInstance
ExecutionContext
Presentation endpoint(s)
Capabilities
State
```

A PowerShell session may be:

```text
local graphical
serial
SSH
remote management
embedded
```

without changing the shell language.

---

# 31. Multiple Presentation Endpoints

A session may potentially have:

```text
one terminal endpoint
multiple mirrored endpoints
a remote endpoint
no endpoint temporarily while suspended
```

The shell should not assume that its physical keyboard/display is permanently attached.

This aligns with the broader WitOS presentation model.

---

# 32. Session Persistence

Long-lived shell sessions are useful.

Potential behavior:

```text
PowerShell session
      |
terminal disconnects
      |
session remains alive
      |
new terminal attaches
```

This resembles `tmux`/remote-session behavior but can be modeled natively as application/session lifecycle rather than as a workaround.

Not required for the first implementation, but the architecture should not prevent it.

---

# 33. Terminal and Distributed Presentation

Because a terminal is a presentation resource, remote presentation is natural:

```text
PowerShell execution
    machine A
       |
       v
text-terminal stream/resource
       |
       v
presentation endpoint
    machine B
```

This is different from remote execution.

The execution can remain local while presentation moves.

That distinction is useful in WitOS.

---

# 34. Developer Experience

The default developer flow should remain ordinary .NET development.

Example:

```text
Windows + VS/Rider/VS Code
        |
        v
Microsoft.NET.Sdk
        |
        v
dotnet build
        |
        v
copy to WitOS
        |
        v
PowerShell
        |
        v
dotnet MyApp.dll
```

No custom shell syntax or SDK is required to launch ordinary portable .NET applications.

---

# 35. PowerShell as a Developer Surface

PowerShell can expose WitOS development tooling naturally.

Potential commands:

```powershell
Get-WitApplication
Get-WitResource
Get-WitCapability
Get-WitExecutionContext
Get-WitDevice
Get-WitService

Install-WitApplication
Start-WitApplication
Stop-WitApplication

Trace-WitApplication
Debug-WitApplication
```

Names are provisional.

The point is that the same typed management model works interactively and in automation scripts.

---

# 36. Developer Tooling and IDE Integration

Visual Studio/Rider/VS Code integration should eventually use the same management APIs as PowerShell.

Architecture:

```text
WitOS management APIs
      |
      +--> PowerShell module
      +--> VS Code integration
      +--> Rider integration
      +--> Visual Studio integration
      +--> GUI management tools
```

PowerShell is therefore a client of the management API, not the management API itself.

---

# 37. Logging and Diagnostics

PowerShell commands should consume standard system diagnostics rather than special shell-only logs.

Example:

```powershell
Get-WitEvent
Get-WitTrace
```

may wrap:

```text
System.Diagnostics
EventPipe
WitOS resource/application metadata
```

The underlying telemetry infrastructure remains standard .NET/WitOS diagnostics.

---

# 38. Error Model

Cmdlets should expose structured errors.

Avoid transforming system failures into ad-hoc text messages too early.

Possible error object data:

```text
error category
resource/application id
operation
capability failure
policy decision
provider
inner .NET exception
diagnostic correlation id
```

This allows scripts to reason about errors reliably.

---

# 39. PowerShell Formatting

PowerShell's formatting system is a strong fit for resource-oriented objects.

Example:

```powershell
Get-WitResource
```

can return rich objects while the default formatter displays:

```text
Name       Kind       Locality   State      Capacity
----       ----       --------   -----      --------
GPU0       GPU        Local      Available  ...
Node42     Compute    Remote     Busy       ...
```

The object itself remains richer than the table.

This is preferable to CLI tools whose text output later becomes an accidental API.

---

# 40. Browser / Web Terminal Relationship

The managed browser may eventually host web terminal applications, but it must not become the required terminal implementation.

Possible future web app:

```text
browser
  |
web terminal UI
  |
remote/local session capability
  |
PowerShell
```

This is merely another presentation frontend.

The native terminal remains independent.

---

# 41. JavaScript and WASM Shell Integration

RFC0018 proposes system JavaScript and WebAssembly runtimes.

PowerShell can expose these naturally.

Examples:

```powershell
js script.js
wasm run tool.wasm
./tool.wasm
```

or PowerShell modules/cmdlets:

```powershell
Invoke-JavaScript
Invoke-WebAssembly
```

The exact UX should preserve normal application launching where possible.

The shell should not contain runtime implementations itself.

---

# 42. Security of Native/External Commands

Launching external binaries/scripts must respect:

- application identity;
- signature/trust state;
- requested capabilities;
- execution policy;
- resource limits.

PowerShell execution policy should not be confused with WitOS system trust policy.

PowerShell's script-execution policies may remain useful as user/admin policy, but they are not a security boundary by themselves.

---

# 43. Script Signing

WitOS software trust/signature infrastructure can potentially integrate with PowerShell script/module signing.

The architecture should distinguish:

```text
cryptographic identity/signature
trust decision
execution policy
capability grant
```

A signed script is not automatically authorized to access arbitrary resources.

---

# 44. Environment Variables

Portable software expects environment variables.

WitOS should support standard .NET environment semantics sufficiently for compatibility.

However, environment variables should not become the primary system configuration database.

Prefer typed configuration/resources for native WitOS services.

PowerShell can expose both.

---

# 45. Current Directory

PowerShell and ordinary console applications expect a current directory.

Support this as a compatibility/application execution concept.

It must not imply that every WitOS resource can or should be represented as a filesystem path.

---

# 46. Standard Streams

The following semantics should be preserved:

```text
stdin
stdout
stderr
```

Redirection and pipelines must work for normal console applications.

PowerShell object pipelines remain a higher-level mechanism.

Examples:

```powershell
native-app > output.txt
Get-WitResource | Where-Object ...
```

Both are required.

---

# 47. ANSI/VT and Rich Console APIs

Portable CLI applications commonly emit ANSI/VT escape sequences.

The terminal host should support a useful modern subset.

For richer native applications, WitOS-specific terminal capabilities may expose:

- structured style changes;
- hyperlinks;
- images where appropriate;
- resize notifications;
- clipboard interaction;
- terminal metadata.

These must be optional enhancements.

Portable CLI software should remain functional through conventional terminal protocols.

---

# 48. Accessibility

The graphical terminal should participate in the shared WitOS accessibility model.

Potential structure:

```text
Terminal screen model
      |
      v
Accessibility adapter
      |
      v
WitOS accessibility tree
      |
      v
screen reader / assistive technology
```

Do not require terminal applications to implement OS-specific accessibility stacks independently.

---

# 49. Terminal Input Model

The terminal should receive typed input events from WitOS Presentation/Input services and translate them into terminal/application semantics.

Potential inputs include:

```text
keyboard
text composition / IME
paste
mouse
touch
accessibility input
remote input
```

PowerShell itself should receive the expected console/terminal events rather than raw hardware input.

---

# 50. Line Editing

PowerShell already provides a mature interactive line-editing experience through its ecosystem, particularly PSReadLine.

WitOS should support the console/terminal capabilities required by upstream PowerShell/PSReadLine rather than creating a competing shell line editor.

This is another reason to prioritize correct terminal semantics early.

---

# 51. Command Discovery

PowerShell command resolution should work normally:

```text
aliases
functions
cmdlets
scripts
applications
```

WitOS may additionally expose application registrations/associations to command discovery, but should not break upstream behavior.

A command should be able to launch an application independent of its installation path where the application model provides a registered executable command.

---

# 52. Application Registration vs PATH

Traditional systems rely heavily on `PATH`.

WitOS should support `PATH` for compatibility, but may also offer registered command/application discovery.

Potential model:

```text
command name
    |
    +--> PowerShell cmdlet/function
    +--> PATH executable
    +--> registered WitOS application command
```

Exact precedence needs a dedicated design decision.

---

# 53. Modules and System APIs

WitOS PowerShell modules should be thin wrappers over stable managed APIs.

Bad:

```text
PowerShell cmdlet contains all resource-management business logic
```

Good:

```text
OutWit.OS.* managed API
      |
      +--> PowerShell module
      +--> GUI tool
      +--> IDE integration
```

This keeps automation and GUI administration behavior consistent.

---

# 54. Proposed Package Layout

Illustrative only:

```text
OutWit.OS.Terminal.Abstractions
OutWit.OS.Terminal
OutWit.OS.PowerShell
OutWit.OS.PowerShell.Resources
OutWit.OS.PowerShell.Applications
OutWit.OS.PowerShell.Capabilities
OutWit.OS.PowerShell.Execution
```

Avoid package fragmentation unless dependency boundaries justify it.

The PowerShell integration packages should remain outside the minimum kernel/system core.

---

# 55. Hosted Development

Terminal and PowerShell integration should be testable on Windows/Linux during early development.

Possible structure:

```text
portable terminal/session abstractions
        |
        +--> hosted Windows backend
        +--> hosted Linux backend
        +--> WitOS backend
```

This enables:

- unit tests;
- integration tests;
- debugger;
- profiler;
- rapid shell/module development;
- testing before graphical WitOS exists.

---

# 56. Implementation Milestones

## T0 — Basic console compatibility

- `System.Console`;
- stdin/stdout/stderr;
- serial/QEMU console;
- simple console apps;
- redirection basics.

## T1 — Interactive terminal session

- terminal session abstraction;
- resize;
- input events;
- ANSI/VT subset;
- hosted test backend.

## T2 — CoreCLR + PowerShell startup

- upstream PowerShell starts;
- interactive commands work;
- basic filesystem;
- modules load;
- PSReadLine works sufficiently.

## T3 — External application execution

- `dotnet app.dll`;
- ordinary executables;
- exit codes;
- environment;
- working directory;
- redirection;
- process compatibility.

## T4 — Native WitOS PowerShell module

- resource discovery;
- application management;
- execution contexts;
- services/devices.

## T5 — Capability-aware administration

- request/acquire capability;
- trusted consent;
- revocation;
- structured permission errors.

## T6 — GUI terminal

- graphical terminal app;
- shared text/font stack;
- clipboard;
- accessibility;
- multiple tabs/windows optional.

## T7 — Remote sessions

- SSH/remoting;
- detach/reattach;
- remote terminal endpoints;
- management APIs.

## T8 — Runtime integration

- JavaScript runtime commands;
- WebAssembly execution;
- first-class `.wasm` application launch.

---

# 57. Acceptance Tests

## 57.1 Portable .NET console app

Build on Windows:

```csharp
Console.WriteLine("Hello");
```

Copy assemblies to WitOS.

Run without recompilation.

---

## 57.2 PowerShell startup

Run upstream PowerShell and verify:

```powershell
$PSVersionTable
1 + 2
Get-ChildItem
Get-Date
```

---

## 57.3 Dynamic .NET

Verify PowerShell exercises:

- reflection;
- dynamic invocation;
- runtime generic construction;
- module loading;
- script compilation paths.

---

## 57.4 Pipelines

Verify object pipelines:

```powershell
1..10 | Where-Object { $_ % 2 -eq 0 } | Measure-Object
```

---

## 57.5 External .NET process

```powershell
dotnet MyPortableApp.dll
```

with correct stdout/stderr/exit code.

---

## 57.6 Native resource objects

```powershell
Get-WitResource
```

returns typed objects, not preformatted text.

---

## 57.7 Discovery vs authority

A resource visible in:

```powershell
Resource:\
```

must not automatically be usable without the appropriate capability.

---

## 57.8 Browser independence

Remove the browser.

PowerShell and terminal continue to function fully.

---

## 57.9 GUI independence

Boot a headless image.

PowerShell works over serial/remote terminal without GUI components.

---

## 57.10 Alternative shell

Run another command shell in the same terminal infrastructure.

This verifies that terminal != PowerShell.

---

# 58. Open Questions

1. What exact upstream PowerShell native/platform shims require WitOS-specific implementation?
2. Should WitOS expose a PTY-compatible API, a new session API, or both?
3. How should registered application commands participate in PowerShell command resolution?
4. Should `Resource:`, `Device:`, `Application:`, `Capability:` and `Service:` all exist as PSDrives, or should some concepts be cmdlet-only?
5. How should live capability objects be represented safely in PowerShell?
6. How should administrative capability elevation work in a headless session?
7. Should PowerShell sessions survive terminal disconnection by default?
8. Which SSH implementation should be used initially?
9. Should native WitOS management remoting use WitRPC in addition to PowerShell remoting?
10. How should PowerShell serialization preserve WitOS resource identity without accidentally serializing authority?
11. How should file/resource capabilities appear in scripts without encouraging path-based ambient authority?
12. What terminal protocol subset is required for PSReadLine and common .NET CLI tools?
13. Should the default GUI terminal support terminal images/hyperlinks from the beginning?
14. How should terminal sessions integrate with the application suspend/resume model?
15. What is the smallest recovery monitor required if CoreCLR/PowerShell cannot start?
16. Should `Get-Process` expose only actual processes while `Get-WitApplication` exposes logical applications?
17. How should jobs map to WitOS background execution and scheduling?
18. How should trusted prompts work over SSH/remote sessions?
19. Should system JavaScript/WASM runtimes expose PowerShell cmdlets or only normal executable/application launch semantics?
20. Which WitOS PowerShell modules should be part of the default installation versus optional administration modules?

---

# 59. Architectural Invariants

> **PowerShell 7+ is the default WitOS command shell.**

> **PowerShell runs on the standard upstream .NET runtime; WitOS does not create a custom PowerShell language dialect.**

> **PowerShell is an ordinary replaceable application, not a kernel or privileged system component.**

> **The terminal is a presentation resource and is independent of PowerShell.**

> **System.Console compatibility is mandatory for portable .NET console software.**

> **WitOS-specific shell integration is additive; portable PowerShell and .NET behavior must remain intact.**

> **PowerShell integrates directly with WitOS resources and capabilities rather than through a mandatory POSIX compatibility layer.**

> **Discovery is not authority. A PowerShell-visible resource does not grant permission to use it.**

> **Resource identifiers, provider paths and serialized objects are not capabilities.**

> **The default command shell receives no universal root-like authority merely because it is interactive.**

> **The GUI shell, browser and boot process do not depend on PowerShell.**

> **Headless WitOS is a first-class deployment profile.**

> **PowerShell modules are clients of stable WitOS APIs; system behavior does not live only inside cmdlets.**

> **Compatibility views such as processes, paths or drive letters do not replace the native WitOS application/resource model.**

> **PowerShell is a major .NET compatibility workload and should be used as an acceptance test for CoreCLR/JIT integration.**

---

# 60. Long-Term User Experience

A desktop user may see:

```text
WitOS Desktop
    |
    +--> graphical terminal
           |
           v
        PowerShell
           |
           +--> ordinary .NET commands/apps
           +--> filesystem
           +--> Resource:
           +--> Device:
           +--> Application:
           +--> Service:
           +--> capability-aware administration
```

A server user may see:

```text
SSH / serial / remote terminal
           |
           v
        PowerShell
           |
           v
      same system APIs
```

A developer may use:

```text
VS / Rider / VS Code on Windows
        |
        v
build ordinary .NET software
        |
        v
deploy to WitOS
        |
        v
PowerShell
        |
        v
dotnet MyApp.dll
```

The same command environment therefore spans desktop, server, embedded and development scenarios without making any one presentation mode fundamental.

---

# 61. Strategic Consequence

A strong PowerShell/terminal architecture provides more than a convenient command prompt.

It gives WitOS:

- a mature administration language;
- a standard .NET stress test;
- a first-class headless interface;
- a scripting platform;
- a developer shell;
- typed access to resources and applications;
- a path to local and remote management;
- an interface that naturally matches the capability/resource architecture.

The desired relationship is:

```text
PowerShell does not define WitOS.

WitOS exposes a coherent public platform.

PowerShell becomes the most powerful default interactive client of that platform.
```

This preserves portability and replaceability while still allowing unusually deep integration.
