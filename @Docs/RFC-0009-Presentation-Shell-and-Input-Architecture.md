# WitOS
## RFC 0009 — Presentation, Shell & Input Architecture
### Draft v0.1

## 1. Status

Draft.

This document defines the presentation, display, windowing, compositor, shell, input, trusted UI, notification, clipboard, and adaptive user-interface model of WitOS.

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
```

The central principles are:

> **WitOS does not have a mandatory GUI. It has presentation resources.**

> **The shell owns presentation policy, not presentation hardware.**

and:

> **Applications target capabilities and presentation contracts, not desktop, mobile, tablet, or server operating-system variants.**

---

## 2. Motivation

Traditional operating systems often merge several independent concepts:

```text
display driver
window server
compositor
desktop shell
application launcher
task switcher
notification system
input stack
file manager
security UI
```

into what users perceive as "the GUI".

This produces architectural assumptions such as:

```text
graphical shell always running
one desktop model
one window manager
one input model
one application presentation
GUI required for normal OS operation
```

These assumptions do not fit:

```text
servers
embedded systems
phones
tablets
workstations
kiosks
VR environments
remote sessions
multi-device applications
```

WitOS separates these concerns.

---

## 3. Presentation Is a Resource

Presentation is modeled through resources.

Possible presentation resources include:

```text
ITextTerminal
IDisplaySurface
IInteractiveSurface
IWindowingEnvironment
IAudioOutput
INotificationEndpoint
IRemotePresentationEndpoint
```

An application receives only the presentation capabilities it requires.

---

## 4. GUI Is Optional

A valid WitOS installation may have:

```text
kernel
.NET runtime
storage
networking
services
terminal
```

and no graphical subsystem.

This is not a separate server operating system.

It is WitOS with no graphical presentation resources installed or active.

---

## 5. Presentation Is Not Application Identity

An application may exist with zero, one, or multiple presentations without changing application identity.

Example:

```text
Media Player
    ├── Main Window
    ├── Notification Controls
    ├── Lock-Screen Controls
    └── Remote Controller
```

All are presentations of one logical application.

---

## 6. Presentation Environment

A **Presentation Environment** is the set of presentation capabilities available to an application or session.

Examples:

```text
Desktop
Touch Fullscreen
Tablet Multitasking
Terminal
Kiosk
Remote Desktop
VR
Headless
```

These names describe policy/configuration.

They are not separate application platforms.

---

## 7. Presentation Endpoint

A **Presentation Endpoint** is one concrete active representation of an application.

Examples:

```text
window
fullscreen surface
terminal
remote view
notification panel
VR surface
```

An application may dynamically attach and detach endpoints.

---

## 8. Presentation Endpoint Lifetime

Presentation endpoints may disappear independently of the application.

Reasons include:

```text
window closed
display removed
remote client disconnected
shell switched
compositor restarted
device handoff
session ended
```

The application lifecycle remains separate.

---

## 9. No Mandatory Window Model

WitOS does not assume that graphical presentation must use overlapping rectangular windows.

Possible environments include:

```text
traditional windows
fullscreen applications
tiled surfaces
split screen
floating panels
VR spaces
kiosk surfaces
```

Applications target presentation capabilities rather than one window-management policy.

---

## 10. Windowing Environment

A graphical environment may expose `IWindowingEnvironment`.

Conceptually it allows an application to request presentation surfaces.

The exact API is deferred.

---

## 11. Window Is a Presentation Resource

A window is not an application.

A window may be created, destroyed, moved, or transferred while the logical application continues to exist.

---

## 12. Closing the Last Window

Closing the last graphical endpoint does not inherently mean terminate application.

Valid policies include terminate, suspend, continue in background, or remain active without presentation.

The application and session policy determine the result.

---

## 13. Display Resource

Physical or virtual displays are resources.

A display may expose:

```text
dimensions
refresh rates
pixel formats
HDR capability
color characteristics
orientation
scale
physical size
variable refresh
touch association
```

Applications should generally interact with higher-level presentation environments rather than raw display resources.

---

## 14. Display Identity

A display's physical connector or bus address is not its application-level identity.

The system may maintain a logical display identity across reconnects where practical.

---

## 15. Display Topology

Multiple displays form a topology.

Example:

```text
Display A
    4K
    landscape
    primary workspace

Display B
    1440p
    portrait

Display C
    remote
```

The shell or presentation policy decides workspace arrangement.

---

## 16. Hotplug

Displays may appear or disappear dynamically.

Applications must tolerate external monitor connected, dock removed, remote display attached, or VR device activated without restart.

---

## 17. Display Removal

If a display disappears:

```text
presentation endpoint
    ↓
rebind / move / suspend
```

according to policy.

The logical application remains unaffected.

---

## 18. Display Modes

Low-level display resources may expose resolution, refresh rate, color depth, HDR, and variable refresh.

The compositor normally selects modes.

Applications request exact modes only with appropriate capability.

---

## 19. DPI and Scaling

Presentation APIs must expose logical dimensions separately from physical pixels.

Applications should not assume:

```text
1 logical unit = 1 pixel
```

---

## 20. Adaptive Layout

Applications should adapt to available logical area, orientation, input methods, density, window size, and presentation policy rather than OS identity.

---

## 21. Device Class Must Not Be Required

Avoid:

```csharp
if (IsPhone)
{
    ...
}
```

Prefer capability and presentation property checks such as available width or touch support.

---

## 22. Adaptive UI Frameworks

Frameworks may provide adaptive layout abstractions above WitOS.

Avalonia is a strong candidate for the first high-level graphical framework.

It is not part of the WitOS specification.

---

## 23. Avalonia Integration

A future backend might look like:

```text
Avalonia Application
        ↓
Avalonia.WitOS
        ↓
OutWit.OS.Presentation
        ↓
WitOS compositor/windowing services
```

The application remains ordinary Avalonia code.

---

## 24. UI Framework Independence

WitOS presentation APIs must not depend on Avalonia internals.

Other frameworks may implement WitOS backends.

---

## 25. Standard .NET Applications

Console applications continue to use:

```csharp
Console.WriteLine(...)
Console.ReadLine(...)
```

normally.

No WitOS-specific API is required.

---

## 26. Text Terminal

`System.Console` maps onto an available `ITextTerminal`.

Possible implementations:

```text
serial console
graphical terminal
SSH session
remote console
debug console
```

---

## 27. No Terminal Requirement

Embedded applications may run without any terminal capability.

Console availability must not define application identity.

---

## 28. Compositor

The **Compositor** combines graphical surfaces into display output.

Responsibilities may include:

```text
surface composition
visibility
occlusion
transforms
scaling
animation
color management
presentation timing
```

---

## 29. Compositor Is Not the Shell

Conceptually:

```text
Applications
     ↓
Presentation API
     ↓
Compositor
     ↓
Display Driver
     ↓
Display Hardware
```

Separately:

```text
Shell
     ↓
presentation policy
window placement
workspace policy
launcher
task switching
```

---

## 30. Replaceable Compositor

The compositor may be replaceable through a stable system contract.

Possible implementations:

```text
StandardCompositor
MinimalCompositor
RemoteCompositor
LowPowerCompositor
ExperimentalCompositor
```

Replacing the compositor is more privileged than replacing a shell.

---

## 31. Compositor Failure

A compositor failure should ideally not terminate graphical applications.

Conceptually:

```text
Compositor crash
    ↓
presentation temporarily unavailable
    ↓
compositor restart
    ↓
surfaces reacquired/recreated
```

---

## 32. Application Rendering State

Applications should not assume that all compositor/GPU resources survive presentation restart.

Logical UI state must remain distinct from transient rendering state.

---

## 33. Rendering Surface

A graphical application may receive a rendering surface capability.

The surface may be backed by GPU texture, shared memory, framebuffer, or remote stream.

The application/framework should not normally care.

---

## 34. GPU Rendering

A compositor may use GPU resources exposed by RFC 0007.

The compositor should not own the entire GPU.

---

## 35. GPU Resource Partitioning

GPU resources may simultaneously support desktop composition, application rendering, compute, video encoding, and video decoding.

The resource scheduler coordinates usage.

---

## 36. Direct Presentation

Performance-sensitive applications may request a more direct presentation path.

Examples include games, VR, video playback, and low-latency visualization.

---

## 37. Exclusive Presentation

An application may request exclusive display, exclusive scanout, or reduced compositor path subject to policy.

This is a capability request, not an application type.

---

## 38. Compositor Bypass

If hardware and policy allow, an application surface may be scanned out directly.

The shell and compositor need not consume substantial GPU resources during this mode.

---

## 39. Exclusive Does Not Mean Uncontrolled

Even in exclusive mode, WitOS must retain secure attention, critical system UI, session recovery, and thermal safety.

---

## 40. Shell

A **Shell** is a replaceable application implementing session-level presentation policy.

The shell is not the operating system.

---

## 41. Shell Capabilities

A shell may receive:

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

Exact APIs are deferred.

---

## 42. Shell Is an Application

A shell is installed, updated, started, stopped, and potentially restarted using normal application mechanisms.

Its special role comes from granted capabilities.

---

## 43. Shell Identity Is Not Magic

WitOS must not recognize a shell because its executable is named `explorer.exe`, `shell.exe`, or `desktop`.

It is a shell because session policy grants it shell capabilities.

---

## 44. Replaceable Shells

Possible shells include:

```text
DesktopShell
TouchShell
WorkstationShell
GamingShell
KioskShell
MediaShell
AccessibilityShell
RemoteShell
CommunityShell
```

---

## 45. Shell Independence

Normal applications must never require a particular shell.

Bad:

```csharp
if (Shell.Name == "DesktopShell")
```

Good:

```csharp
if (presentation.SupportsWindows)
```

---

## 46. Shell Switching

Users may change shells without rebooting.

Where feasible:

```text
Shell A
   ↓ stop
Shell B
   ↓ start
```

Applications continue running.

---

## 47. Shell Crash

If the shell fails:

```text
applications continue
compositor may continue
session controller survives
trusted recovery UI appears
```

The shell may restart.

---

## 48. Minimal Recovery Shell

WitOS should include or provide access to a trusted minimal recovery presentation.

This should remain available even if a third-party shell fails.

---

## 49. Shell Does Not Own Presentation Hardware

The shell receives capabilities to influence presentation.

It does not own display, GPU, keyboard, mouse, touchscreen, or audio hardware.

---

## 50. Shell Presentation Policy

The shell decides policy such as window arrangement, workspace organization, application launcher, task switching, desktop widgets, and notification placement.

---

## 51. Window Placement

Window placement belongs to shell/window-management policy.

Applications may provide hints or preferences.

They should not require absolute placement unless explicitly allowed.

---

## 52. Application Size Requirements

Applications may declare minimum size, preferred size, aspect preference, or fullscreen preference.

The presentation environment negotiates.

---

## 53. Tiling

A shell may implement tiling.

Applications do not need separate APIs.

---

## 54. Traditional Desktop

Another shell may implement free-floating overlapping windows.

The application contract remains the same.

---

## 55. Phone Fullscreen Model

A touch shell may normally present one primary application fullscreen.

The same application package can participate without becoming a mobile-specific build.

---

## 56. Tablet Multitasking

A tablet shell may expose split view, floating windows, fullscreen, and external display workspace using the same presentation contracts.

---

## 57. Kiosk

Kiosk operation should not require launching a hidden desktop shell.

Conceptually:

```text
Session
    ↓
Kiosk Application
    ↓
Display/Input capabilities
```

No shell is required.

---

## 58. Embedded Presentation

An embedded device may expose only a small display surface, few buttons, or touch panel.

The same resource model applies.

---

## 59. Headless Operation

A headless machine exposes no graphical presentation capabilities.

Applications requiring graphical presentation simply cannot acquire them.

---

## 60. Remote Presentation

Presentation and execution may reside on different devices.

Example:

```text
Application execution
        Workstation
            │
            │ presentation channel
            ▼
          Tablet
```

---

## 61. Remote Presentation Is Not Remote Execution

An application may execute locally and present remotely, or execute remotely and present locally.

These concepts are separate.

---

## 62. Remote Presentation Endpoint

A remote presentation endpoint may expose display characteristics, input characteristics, latency, bandwidth, and codec capabilities.

---

## 63. Remote Adaptation

An application may adapt to the destination presentation environment without migrating execution.

---

## 64. Remote Compositor

A compositor may encode final output for a remote client.

Alternatively, higher-level surfaces may be transferred.

The architecture does not mandate one protocol.

---

## 65. Pixel Streaming

One possible remote presentation model:

```text
server compositor
    ↓
encoded frames
    ↓
client
```

This provides maximal application transparency but uses more bandwidth.

---

## 66. Semantic Remote UI

Another model may transmit surface operations, UI tree, or framework-level representation when appropriate.

This is framework-specific.

---

## 67. Hybrid Remote Presentation

WitOS should permit hybrid approaches.

Different applications may use different remote presentation strategies.

---

## 68. Remote Input

Remote presentation may return input events to the execution environment.

Latency and trust remain explicit characteristics.

---

## 69. Presentation Handoff

A presentation may move from desktop monitor to tablet without moving application computation.

---

## 70. Multiple Simultaneous Presentations

An application may simultaneously present desktop window, tablet controller, and remote monitoring view.

Each endpoint may have different capabilities.

---

## 71. Presentation State

The application may maintain presentation-specific state separately for each endpoint.

Example:

```text
Desktop:
    full editor

Tablet:
    compact controls
```

---

## 72. Input Is a Resource

Input devices and semantic input streams participate in the resource/capability model.

---

## 73. Raw Hardware vs Semantic Input

RFC 0007 may expose USB HID, touch controller, or keyboard device.

A higher-level input service produces keyboard events, pointer events, touch contacts, pen input, and game controller input.

---

## 74. Unified Pointer Model

A common pointer model may represent mouse, trackpad, pen, touch-generated pointer, or remote pointer where semantics overlap.

---

## 75. Pointer Properties

Pointer events may expose position, movement, buttons, pressure, tilt, rotation, hover, contact area, precision, and device type.

Only supported properties are present.

---

## 76. Touch

Touch input should support multiple contacts, pressure where available, contact geometry, and gesture-independent raw contacts.

---

## 77. Pen

Pen capabilities may include pressure, tilt, eraser, barrel buttons, hover, and rotation.

Applications test capabilities rather than platform type.

---

## 78. Mouse

Mouse input remains fully supported.

A mouse is one pointer-capable input source.

---

## 79. Trackpad

Trackpads may expose pointer movement, multitouch contacts, and gesture information depending on policy and framework.

---

## 80. Keyboard

Keyboard input should distinguish physical key, logical key, text input, and modifier state where appropriate.

---

## 81. Text Input Is Not Raw Keyboard Input

Text input may come from physical keyboard, virtual keyboard, IME, speech recognition, handwriting, remote input, or accessibility device.

Applications receiving text should not require a physical keyboard.

---

## 82. Input Method Editors

IME/text composition should be system/framework services.

Applications receive composition events and final text.

---

## 83. Virtual Keyboard

A touch shell or input service may provide an on-screen keyboard.

This is a presentation/input provider, not a special mobile-only mechanism.

---

## 84. Game Controllers

Gamepads and similar devices may expose typed controller resources.

Applications request these capabilities explicitly.

---

## 85. Specialized Input

Other possible inputs include:

```text
3D controller
MIDI device
eye tracking
gesture sensor
VR controllers
switch accessibility devices
```

The input model must remain extensible.

---

## 86. Focus

The presentation environment controls which application receives normal focused input.

Focus is presentation policy.

---

## 87. Focus Is Not Authority to Observe Everything

An application receiving keyboard input while focused does not gain global key logging, other application input, or secure credential input.

---

## 88. Global Input Capability

Global input observation is a separate privileged capability.

Examples include accessibility tools, system hotkeys, debug tools, or specialized automation.

---

## 89. System Shortcuts

The session controller may reserve input gestures for secure attention, session switching, recovery, and accessibility.

Applications cannot consume them exclusively without appropriate policy.

---

## 90. Secure Attention

WitOS requires a trusted path to system security UI.

Possible triggers include hardware key, reserved key combination, dedicated button, or firmware-assisted gesture.

---

## 91. Secure Attention Cannot Be Spoofed

Ordinary applications and shells must not be able to produce UI indistinguishable from the trusted secure-attention environment.

---

## 92. Trusted Presentation

Certain operations use trusted presentation controlled by system security services.

Examples:

```text
login
permission consent
credential entry
passkey confirmation
system recovery
security warning
```

---

## 93. Trusted Presentation Is Not Shell UI

The shell may visually integrate trusted prompts.

It does not render or control their security-sensitive content.

---

## 94. Secure Input

Trusted presentation may request secure input.

During secure input, ordinary applications, shell plugins, and global hooks must not observe protected input where enforceable.

---

## 95. Hardware Secure Input

Some devices may provide stronger hardware-backed secure paths.

WitOS reports the actual guarantee.

---

## 96. Permission UI

Capability requests requiring user consent invoke trusted system presentation.

Applications cannot implement their own authoritative permission dialogs.

---

## 97. Consent Context

Trusted consent UI should identify requesting application, requested capability, target resource, scope, and duration where practical.

---

## 98. Notifications

Notifications are presentation resources.

Applications may request access to `INotificationEndpoint`.

---

## 99. Notification Host

The shell may host notification presentation.

The underlying notification service remains independent of the shell.

---

## 100. Shell Replacement and Notifications

Switching shells should not destroy notification state.

A new notification host may present existing active notifications.

---

## 101. Notification Types

Possible semantic classes:

```text
informational
progress
actionable
warning
critical
```

System policy controls presentation.

---

## 102. Notification Authority

Applications should not automatically have unlimited ability to create disruptive notifications.

Notification capabilities may be scoped.

---

## 103. Critical Notifications

Critical system notifications may bypass ordinary shell policy using trusted presentation.

---

## 104. Background Applications

Applications without active windows may still publish notifications if granted authority.

---

## 105. Status Presentation

An application may expose status information usable by multiple shells.

Examples include download progress, music playback, and background job status.

This should not require shell-specific integration APIs.

---

## 106. Application Commands

Applications may expose semantic commands such as Pause, Resume, Next, Cancel, or Open.

A notification host, lock screen, remote shell, or accessibility tool may present them.

---

## 107. Clipboard

Clipboard is a session-level resource.

It may contain:

```text
text
binary data
structured data
resource references
delegated capabilities
```

---

## 108. Clipboard Is Not Just Bytes

Copying a document may place display name, resource reference, and temporary read capability rather than serializing the entire document.

---

## 109. Clipboard Authority

Applications require appropriate capability to read clipboard, write clipboard, or observe clipboard changes.

These are distinct operations.

---

## 110. Clipboard Privacy

Applications should not automatically receive notifications of every clipboard change unless authorized.

This reduces passive data harvesting.

---

## 111. Clipboard Lifetime

Clipboard capability transfers may be one-shot, session-scoped, time-limited, or persistent until replaced depending on semantics.

---

## 112. Copy vs Move

A clipboard operation may express Copy, Move, or Reference rather than relying only on byte payload.

---

## 113. Drag and Drop

Drag-and-drop is another capability-transfer mechanism.

Example:

```text
File Manager
    │
    │ document capability
    ▼
Editor
```

---

## 114. Drag Payload

A drag payload may include text, structured data, resource descriptors, capabilities, and preview information.

---

## 115. Drop Authority

Receiving a drag payload does not automatically grant broader source-application authority.

Only explicitly delegated capabilities transfer.

---

## 116. Cross-Application Data Transfer

Clipboard and drag-and-drop use the same general security rules as RFC 0004 capability delegation.

---

## 117. Resource Picker

A trusted resource picker brokers access to storage resources.

It is a presentation service above RFC 0008.

---

## 118. File Picker

A file picker is one possible UI for selecting storage resources.

The application receives a capability, not necessarily a global path.

---

## 119. Shell-Independent Pickers

Resource pickers should work regardless of the active shell.

Shells may provide visual hosting/themes, but security authority remains in trusted services.

---

## 120. Application Launcher

Launching applications is a session capability.

The shell usually exposes launcher UI.

Other applications may invoke launcher services if authorized.

---

## 121. Open-With UI

A shell/resource picker may enumerate applications capable of handling a selected resource.

The selected application receives an appropriate capability.

---

## 122. Application Associations

File/content handlers should use semantic contract registration.

Example:

```text
content type
resource type
URI scheme
operation
```

not merely filename extension.

Extensions remain useful compatibility metadata.

---

## 123. Workspace

A shell may organize presentation endpoints into workspaces.

Workspace is presentation policy, not application identity.

---

## 124. Multiple Workspaces

An application may have endpoints in several workspaces simultaneously.

---

## 125. Virtual Desktops

A traditional virtual-desktop shell can implement workspaces using the same mechanism.

---

## 126. Session

Presentation belongs to a session context.

Different sessions may have independent shells, presentation policies, clipboards, focus, and notifications.

---

## 127. Multiple Sessions

A machine may host local graphical session, remote graphical session, terminal session, and kiosk session simultaneously.

---

## 128. User vs Session

A user may have multiple presentation sessions.

Presentation state should therefore not be attached solely to user identity.

---

## 129. Lock Screen

A locked session may suspend or obscure normal presentation while applications continue according to background policy.

---

## 130. Lock-Screen Presentation

Selected applications may expose limited presentation/commands while a session is locked.

Authority is explicitly granted.

---

## 131. Login Presentation

Login UI belongs to trusted system presentation, not the user's normal shell.

---

## 132. Accessibility

Accessibility must be part of the presentation architecture rather than a shell-specific afterthought.

---

## 133. Semantic UI Information

UI frameworks should expose semantic information such as role, name, value, state, actions, and relationships to accessibility services.

---

## 134. Accessibility Capability

Accessibility services may receive controlled cross-application presentation access.

This is privileged because it can reveal sensitive UI information.

---

## 135. Screen Readers

A screen reader should consume semantic presentation trees where available rather than infer everything from pixels.

---

## 136. Alternative Input

Accessibility may provide alternative input mechanisms through the same semantic input services.

---

## 137. High Contrast / Visual Adaptation

Presentation policy may provide high contrast, large text, reduced motion, magnification, or color adjustments.

Frameworks can respond through capabilities/preferences.

---

## 138. Reduced Motion

Applications may observe a presentation preference requesting reduced animation.

This should not require shell identification.

---

## 139. Localization

Presentation APIs should support locale, text direction, font fallback, and input method without coupling these to device class.

---

## 140. Fonts

Font services are system/application resources.

Applications may package fonts or use system-provided fonts according to policy.

---

## 141. Font Rendering

Exact font-rendering implementation belongs above the hardware layer and below application frameworks as appropriate.

---

## 142. Color Management

Advanced displays may expose color characteristics.

A compositor may provide ICC-like profiles, HDR transforms, and color-space conversion.

Applications needing accurate color can request appropriate presentation characteristics.

---

## 143. HDR

HDR capability is discovered.

Applications should not assume all displays support it.

---

## 144. Variable Refresh Rate

Low-latency applications may request variable-refresh presentation.

The compositor/display provider reports whether it can be granted.

---

## 145. Presentation Timing

Applications such as games/video players may require frame timing information.

A presentation API may expose refresh cadence, presentation timestamps, and frame deadlines.

---

## 146. VSync

VSync behavior is presentation policy/capability.

Applications may express preferences.

---

## 147. Frame Scheduling

The compositor may coordinate frame production with RFC 0005 scheduling for latency-sensitive workloads.

---

## 148. Input-to-Photon Latency

VR/gaming environments may request coordinated input scheduling, render scheduling, and display presentation to minimize latency.

This may require stronger resource reservations.

---

## 149. Presentation Profiles

WitOS supports semantic presentation profiles.

Possible examples:

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

---

## 150. Presentation Profile Is Policy

A profile is not a separate operating-system mode.

It configures shell behavior, compositor policy, resource allocation, notifications, animations, and background work.

---

## 151. Workstation Profile

A workstation profile may reduce decorative animations, reduce shell GPU usage, pause thumbnail generation, reduce indexing, reserve presentation bandwidth, and favor engineering application responsiveness.

---

## 152. Gaming Profile

A gaming profile may minimize shell activity, grant low-latency input, favor direct presentation, reserve GPU resources, and reduce noncritical notifications.

---

## 153. Minimal Profile

A minimal profile may keep only application switching, secure controls, and critical notifications.

---

## 154. Kiosk Profile

A kiosk profile may expose only one selected application.

No normal launcher or desktop is required.

---

## 155. Remote Profile

A remote session may optimize bandwidth, compression, frame rate, and latency according to connection characteristics.

---

## 156. Dynamic Profile Switching

Profiles may change without reboot.

Example:

```text
Normal Desktop
    ↓
Workstation
    ↓
Normal Desktop
    ↓
Gaming
```

---

## 157. Application Profile Request

An application may request a profile or set of presentation requirements.

The system may grant, partially grant, or deny the request.

---

## 158. Granted Presentation Characteristics

Applications must inspect actual guarantees.

Example:

```text
Requested:
    exclusive display
    low-latency input
    HDR

Granted:
    low-latency input
    HDR
    compositor retained
```

---

## 159. Shell Dormancy

During exclusive presentation the shell may enter a Dormant state.

It may release GPU surfaces, thumbnail resources, animations, and nonessential caches.

---

## 160. Minimal Secure Shell Presence

Even when dormant, essential session controls remain reachable through trusted system presentation.

---

## 161. Presentation Resource Reservations

Applications may request display ownership, GPU memory, frame bandwidth, input latency, or audio priority as coordinated resource contracts.

---

## 162. Presentation and Compute Coordination

A game may request CPU partition, GPU allocation, low-latency input, and exclusive presentation through separate but coordinated resource contracts.

---

## 163. Presentation and Storage Coordination

A video editor may simultaneously request high-bandwidth storage, GPU, display HDR, and workstation presentation profile.

The resource manager may coordinate them.

---

## 164. Presentation and Communication

Remote presentation uses RFC 0006 channels.

Possible transport characteristics include latency, bandwidth, encryption, and input return path.

---

## 165. Presentation and Hardware

Display/input drivers consume RFC 0007 hardware capabilities.

Higher layers consume semantic presentation/input resources.

---

## 166. Raw Display Hardware Is Privileged

Applications should not normally map display-controller MMIO or framebuffer hardware directly.

They use presentation resources.

---

## 167. Raw Input Hardware Is Privileged

Applications should not normally access keyboard/touch hardware directly.

They consume semantic input resources.

---

## 168. Presentation Security

A normal application window must not automatically gain screen capture, global input, other window contents, or trusted UI authority.

---

## 169. Screen Capture

Screen capture is a separate capability.

Possible scopes:

```text
own surface
selected window
selected display
entire session
```

---

## 170. Own-Surface Capture

Applications may normally capture their own rendered content without broader screen authority.

---

## 171. Other-Application Capture

Capturing another application's surface requires explicit authority or user mediation.

---

## 172. Remote Sharing

Screen sharing may grant temporary capture capabilities to a communication service.

This should be visible and revocable.

---

## 173. Capture Indicator

Policy may require trusted indication while screen or input capture is active.

---

## 174. Camera and Presentation

Camera preview is not inherently a presentation capability.

It is a camera resource whose output may be rendered through a presentation surface.

---

## 175. Audio and Presentation

Audio output is related to user experience but remains a separate resource.

A presentation profile may influence audio policy.

---

## 176. Media Controls

Media applications may expose semantic controls to shell/lock-screen/remote presentation without sharing full UI.

---

## 177. Presentation Sandboxing

A sandboxed application receives only its surfaces, focused input, authorized clipboard, and authorized notifications by default.

---

## 178. Shell Plugins

Shell plugins must not automatically inherit all shell authority.

They receive delegated presentation capabilities.

---

## 179. Shell Extension Isolation

Widgets, launchers, and extensions may run in process, isolated managed context, or separate process according to trust.

---

## 180. Community Shell Security

Third-party shells may provide full session UI without receiving unrestricted application data.

Capabilities constrain access.

---

## 181. Theme

Theme is presentation policy.

Applications may receive semantic preferences such as light, dark, high contrast, or accent preference rather than hard-code shell-specific styling.

---

## 182. Application-Owned Styling

Applications may choose their own appearance while still respecting system accessibility and presentation constraints.

---

## 183. Shell Styling Is Not API Contract

Applications should not depend on internal shell CSS, XAML, resources, or implementation details.

---

## 184. Decorations

Window decorations may be system-rendered, shell-rendered, application-rendered, or hybrid depending on presentation policy.

---

## 185. Client-Side Decorations

Frameworks may render their own title bars.

System-level move/resize/focus operations remain mediated by presentation services.

---

## 186. Window Commands

Applications may request minimize, maximize, fullscreen, close, move, or resize where supported.

Presentation environments may ignore or reinterpret operations that do not fit their model.

---

## 187. Fullscreen

Fullscreen is a presentation state.

It does not necessarily imply exclusive display ownership.

---

## 188. Minimize

Some presentation environments may not have a visible minimize concept.

Frameworks should handle unsupported window states gracefully.

---

## 189. Always-On-Top

Persistent topmost presentation is a privileged or policy-controlled feature where it may interfere with trusted UI.

---

## 190. Modal UI

Modal dialogs are framework/presentation behavior.

They must not block trusted system presentation.

---

## 191. Dialog Service

Frameworks may implement application dialogs.

System security prompts remain separate.

---

## 192. Application Activation

Activating an application may cause existing endpoint focused, new endpoint created, headless application resumed, or remote endpoint attached depending on application state.

---

## 193. Presentation Restoration

After application restore:

```text
logical UI state
    ↓
current presentation capabilities
    ↓
new surfaces
```

Physical surfaces are reacquired.

---

## 194. Display Change During Suspension

An application suspended on a desktop may resume on smaller display, touch-only device, or remote endpoint.

The framework should reconstruct suitable presentation.

---

## 195. Snapshotting UI

Rendering surfaces and GPU handles are transient execution state.

They should not be the authoritative persisted UI state.

---

## 196. Persistent UI State

Applications may persist open panels, selected document, workspace arrangement, and navigation state using RFC 0008 storage.

---

## 197. Shell State

The shell may similarly persist workspace arrangement, pinned applications, and presentation preferences without becoming part of application state.

---

## 198. File Manager

A file manager is a normal application using storage and presentation capabilities.

It is not part of the storage subsystem.

---

## 199. Multiple File Managers

Alternative file managers may coexist.

No system API should require one canonical implementation.

---

## 200. Task Manager

A task/application manager is also a normal privileged application.

It may receive application enumeration and lifecycle capabilities.

---

## 201. Settings UI

System settings may be implemented as an application using privileged configuration capabilities.

The kernel does not own a GUI settings panel.

---

## 202. Device Settings

Device-specific configuration UI may be provided by drivers/services through stable settings contracts.

---

## 203. System Tray Equivalent

Status-item presentation may be a shell capability.

Applications publish semantic status endpoints rather than draw directly into a magic global tray.

---

## 204. Launcher Integration

Applications may publish name, icon, commands, recent resources, and activation contracts for shell launchers.

---

## 205. Search Integration

Applications may expose searchable commands/resources under explicit indexing capabilities.

---

## 206. Icons

Icons are presentation metadata.

Applications may provide multiple vector/raster representations.

The shell chooses suitable rendering.

---

## 207. Presentation Metadata

An application may publish display name, icon, commands, content handlers, and status without depending on shell implementation.

---

## 208. Input Capture

Games and drawing applications may request pointer capture, relative mouse movement, or exclusive controller input within their active presentation scope.

---

## 209. Pointer Lock

Pointer lock is a presentation/input capability.

The user must retain a trusted way to escape.

---

## 210. Relative Input

Low-latency applications may receive raw relative movement without global observation authority.

---

## 211. Touch Capture

Applications may capture active touch sequences associated with their surface.

They do not receive unrelated system touch input.

---

## 212. Gesture Recognition

Gesture recognition may occur inside framework, shell, or input service depending on gesture semantics.

---

## 213. System Gestures

Certain gestures may be reserved for home, task switching, secure attention, or accessibility.

---

## 214. App Gestures

Application gestures operate within the application's input region unless exclusive authority is granted.

---

## 215. Input Latency Classes

Input resources may expose or negotiate normal, interactive, low latency, or exclusive modes.

---

## 216. Input Timestamping

Input events should carry reliable monotonic timestamps where available.

This supports gaming, drawing, audio synchronization, and remote input.

---

## 217. Remote Input Timestamps

Remote presentation may distinguish client capture time and server receive time where useful.

---

## 218. Synthetic Input

Automation tools may generate input.

Synthetic input is a privileged capability and should be distinguishable from trusted physical input where relevant.

---

## 219. Automation

UI automation should preferably use semantic accessibility/application contracts instead of only synthetic mouse/keyboard events.

---

## 220. Testing

Presentation frameworks should support virtual presentation providers for automated testing.

Example:

```text
VirtualDisplay
VirtualKeyboard
VirtualPointer
```

---

## 221. Headless UI Testing

Applications should be testable without physical displays.

A virtual compositor may render offscreen.

---

## 222. Input Fault Injection

Tests may simulate touch, mouse, pen, display removal, orientation change, remote disconnect, and DPI change.

---

## 223. Compositor Testing

A reference virtual compositor can validate surface lifecycle, resize, occlusion, and presentation restart.

---

## 224. Shell Testing

Shells can run against virtual application/window descriptors without real user applications.

---

## 225. Presentation Backend Model

`OutWit.OS.Presentation` may use replaceable providers.

Conceptually:

```text
OutWit.OS.Presentation
        │
        ├── WitOS native provider
        ├── Windows provider
        ├── Linux provider
        ├── macOS provider
        └── test/virtual provider
```

---

## 226. Cross-Platform API

Where practical, presentation extensions may run on existing operating systems.

Unsupported guarantees must be reported honestly.

---

## 227. Windows Backend

A hosted provider may map WitOS presentation contracts onto available Windows windowing/display/input facilities.

---

## 228. Linux Backend

A hosted provider may use an appropriate Linux graphical/input environment.

The public contract remains independent of X11/Wayland specifics.

---

## 229. macOS Backend

A hosted provider may similarly map presentation resources onto native macOS facilities.

---

## 230. Native WitOS Backend

The native provider communicates with compositor, input service, display service, and session service using RFC 0006 channels.

---

## 231. Stable Contract, Replaceable Provider

The application depends on:

```text
OutWit.OS.Presentation
```

not directly on:

```text
Win32
Cocoa
Wayland
X11
Android View
```

---

## 232. WPF Compatibility

WPF is Windows-specific and is not automatically portable to WitOS.

Applications depending on WPF require a port, compatibility layer, or alternative UI framework.

---

## 233. WinForms Compatibility

The same limitation applies to WinForms.

Standard .NET compatibility does not imply compatibility with platform-specific UI stacks.

---

## 234. Avalonia Compatibility Goal

Portable Avalonia applications are strong candidates for early WitOS graphical compatibility.

A WitOS backend should aim to allow them to run with minimal or no application changes.

---

## 235. Browser UI

Browser-based applications may run through web/browser resources.

WitOS does not require all applications to use native graphical presentation.

---

## 236. Blazor

Blazor applications remain standard .NET/web applications.

A future WitOS shell may host browser/WebView-like resources, but this is not required by the presentation architecture.

---

## 237. Native WebView

A WebView is a presentation resource provided by a browser/web engine.

It is not a kernel facility.

---

## 238. Application Rendering Choices

An application may choose:

```text
Avalonia
custom GPU rendering
terminal
web UI
remote UI
no UI
```

The OS architecture should support all without privileging one framework.

---

## 239. Presentation Diagnostics

Trusted diagnostics may inspect surfaces, frame timings, GPU usage, input latency, display topology, and compositor load.

---

## 240. Application Privacy

Diagnostics must not expose arbitrary window content without appropriate capture authority.

---

## 241. Shell Diagnostics

Shell performance should be independently measurable from application rendering.

---

## 242. Compositor Resource Usage

The compositor should expose its GPU memory, CPU usage, frame latency, and dropped frames for authorized diagnostics.

---

## 243. Presentation Resource Pressure

The system may signal GPU memory pressure, surface pressure, display bandwidth pressure, or remote bandwidth pressure.

Applications/frameworks may reduce quality.

---

## 244. Graceful Degradation

Possible reactions include lower resolution, reduced animation, dropped caches, lower remote frame rate, or simpler effects.

---

## 245. Shell Resource Pressure

The shell should also release nonessential resources under pressure.

---

## 246. Presentation Failure Isolation

Failure of shell, compositor, one application surface, remote endpoint, or input provider should be isolated where possible.

---

## 247. Recovery

Presentation services should be restartable without rebooting unrelated system services.

---

## 248. Presentation Conformance Tests

Providers should be tested for surface creation, resize, multiple displays, DPI, input, focus, clipboard, drag/drop, remote disconnect, and trusted UI separation.

---

## 249. Accessibility Conformance

Presentation frameworks should expose sufficient semantic accessibility information to pass accessibility tests.

---

## 250. Security Conformance

Tests should verify that ordinary applications cannot observe global input, capture other surfaces, spoof trusted prompts, or block secure attention without explicit authority.

---

## 251. Performance Conformance

Low-level presentation should be measurable for input latency, frame latency, copy count, GPU synchronization, and remote encoding cost.

---

## 252. Compatibility Invariants

1. Graphical presentation is optional.
2. Application identity is independent of presentation.
3. The shell is a replaceable application with granted capabilities.
4. The shell does not own display, input, or GPU hardware.
5. The compositor and shell are distinct components.
6. Trusted security presentation is independent of ordinary shells.
7. Applications depend on presentation/input capabilities rather than device class or shell identity.
8. Desktop, mobile, tablet, kiosk, server, and embedded environments use the same presentation/resource model.
9. Input authority is capability-based.
10. Focused input does not imply global input authority.
11. Screen capture is a separate capability.
12. Presentation surfaces are transient resources and must be reacquirable.
13. Closing the last presentation endpoint does not inherently terminate the application.
14. Remote presentation must expose relevant latency/bandwidth characteristics.
15. Performance/exclusive presentation modes are explicit negotiated resource contracts.
16. Standard console .NET applications remain fully supported.
17. Platform-specific UI frameworks are not part of the standard .NET portability guarantee.
18. WitOS presentation APIs remain framework-independent.

---

## 253. Deferred Questions

### Windowing API

```text
surface creation
window state
resize
focus
activation
decorations
```

### Compositor Protocol

```text
surface submission
buffer ownership
synchronization
frame timing
direct scanout
```

### Rendering Buffers

```text
CPU memory
GPU memory
shared buffers
zero-copy paths
```

### Input API

```text
event types
pointer model
touch model
keyboard representation
IME
controllers
```

### Remote Presentation

```text
pixel streaming
surface streaming
codecs
semantic UI transport
handoff
```

### Trusted Presentation

```text
secure attention
consent UI
credential entry
hardware-backed secure path
```

### Accessibility

```text
semantic tree
automation protocol
cross-application accessibility capability
```

### Presentation Profiles

```text
formal profile contracts
resource coordination
shell dormancy
exclusive presentation
```

---

## 254. Relationship to Future RFCs

This RFC interacts strongly with:

```text
RFC 0010 — Application Packaging & Distribution
    UI framework dependencies
    shell packages
    compositor packages
    presentation metadata
    application handlers
```

Future dedicated RFCs may define Compositor Protocol, Input Event Model, Remote Presentation Protocol, Trusted System Presentation, Accessibility Architecture, and Shell Contract in greater detail.

---

## 255. Summary

WitOS does not define one mandatory desktop.

Instead it defines a presentation architecture:

```text
Applications
      │
      ├── graphical surfaces
      ├── terminal
      ├── notifications
      └── remote presentation
             │
             ▼
    Presentation Services
             │
     ┌───────┴────────┐
     │                │
 Compositor          Input
     │                │
 Display           Input devices
```

with shell policy alongside it:

```text
Shell
  ├── launcher
  ├── workspace policy
  ├── window policy
  ├── task switching
  └── notification hosting
```

The shell is replaceable.

The compositor is separate.

Trusted security UI is independent of both.

Applications may be graphical, terminal-based, headless, remote, or multi-presentation without becoming different application types.

Desktop, mobile, tablet, kiosk, workstation, gaming, and remote environments are therefore different policies over the same presentation/resource model.

The defining principle is:

> **WitOS does not define what the user interface must look like. It defines secure, capability-based presentation and input contracts from which many user environments can be built.**
