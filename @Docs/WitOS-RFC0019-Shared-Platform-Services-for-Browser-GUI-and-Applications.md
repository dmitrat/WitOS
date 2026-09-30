# RFC0019 — Shared Platform Services for Browser, GUI and Applications

**Project:** WitOS  
**Status:** Draft v0.1  
**Related:** RFC0018 — System Runtime Platform and WebAssembly Application Model  
**Scope:** Reusable platform services that should be available to the browser and ordinary applications without becoming browser-owned infrastructure  
**Out of scope:** HTML/DOM/CSS semantics, browser navigation architecture, JavaScript/WebAssembly runtime internals, kernel ABI details, a specific GUI toolkit

---

## 1. Summary

A modern browser contains a large amount of functionality that is not inherently browser-specific.

Chromium, Firefox and WebKit must carry much of this infrastructure because they run on top of several existing operating systems with different APIs and capabilities. WitOS does not need to repeat that architecture.

WitOS should provide a set of reusable platform services for functionality that:

1. is useful to applications other than a browser;
2. naturally belongs to the operating-system/platform layer;
3. benefits from common policy, hardware access, caching, security isolation or implementation reuse;
4. can be defined without requiring HTML, DOM, CSS or browser-specific semantics.

The intended architecture is:

```text
Applications
    │
    ├── Browser
    ├── Shell
    ├── File Manager
    ├── Editors / IDEs
    ├── Media Applications
    ├── Scientific / Visualization Applications
    └── Other .NET / WASM Applications
             │
             ▼
       Platform Services
             │
    ┌────────┼─────────────────────────────────────────┐
    │        │          │        │        │            │
    ▼        ▼          ▼        ▼        ▼            ▼
   Text   Graphics    Media    Image   Identity     Runtime
  Fonts   Compositor  Codecs   Codecs  Credentials   JS/WASM
    │        │          │        │        │            │
    └────────┴──────────┴────────┴────────┴────────────┘
             │
       WitOS resources,
       capabilities and
       execution services
             │
             ▼
          Kernel
```

The browser remains a removable and portable application.

The operating system does **not** become a browser.

The central principle is:

> **If a subsystem is independently useful outside the browser, it should be considered for platform-level ownership. Browser-specific semantics remain in the browser.**

A second principle is equally important:

> **Deep integration must use public platform services, not browser-only privilege.**

---

# 2. Motivation

A modern browser behaves in many respects like a second operating system.

It contains or coordinates:

- language runtimes;
- process and sandbox infrastructure;
- networking;
- TLS and cryptography;
- font discovery and text shaping;
- graphics and composition;
- image decoding;
- video and audio;
- camera and microphone access;
- credentials and authentication;
- notifications;
- location and sensors;
- printing;
- clipboard and drag-and-drop;
- accessibility;
- storage;
- download management;
- GPU compute;
- device permissions;
- diagnostics and profiling.

This duplication is largely necessary on existing operating systems because browsers must hide platform differences behind their own abstractions.

WitOS can invert this relationship.

Instead of:

```text
Browser
    ↓
large private cross-platform subsystem
    ↓
thin OS adaptation layer
```

WitOS should prefer:

```text
Browser
    ↓
browser-specific semantics
    ↓
rich shared platform services
    ↓
WitOS
```

This has several potential benefits:

- smaller browser-specific codebase;
- less duplication across applications;
- one place to integrate new hardware capabilities;
- shared security hardening;
- shared diagnostics;
- centralized but replaceable codecs and providers;
- better use of system-wide caches;
- cleaner capability enforcement;
- better energy/resource management;
- better portability of the browser itself;
- deeper integration on WitOS without making the browser part of the OS.

---

# 3. Lessons from Internet Explorer

WitOS should explicitly avoid the historical failure mode in which the browser becomes structurally inseparable from the operating system.

The wrong architecture is:

```text
WitOS Shell
    ↓
Browser Engine
    ↓
HTML/DOM/CSS
```

or:

```text
System component requires browser implementation to function.
```

The browser must remain:

- removable;
- replaceable;
- independently updateable;
- independently versioned;
- non-essential for boot;
- non-essential for PowerShell;
- non-essential for the GUI shell;
- non-essential for file management;
- non-essential for system settings;
- unable to access privileged APIs unavailable to other authorized applications.

The correct relationship is:

```text
                  WitOS
                    │
             Platform Services
                    │
          ┌─────────┴─────────┐
          ▼                   ▼
   Default Browser      Alternative Browser
```

Both browsers should be able to use the same public services.

A default browser may have optimized integration, but never magical authority.

---

# 4. Selection criteria

A subsystem should be promoted from browser implementation detail to platform service only if it satisfies most of the following criteria.

## 4.1 Independent usefulness

The subsystem should provide clear value to non-browser software.

Examples:

```text
Text shaping     → editors, shell, IDEs, PDF, office apps
Image codecs     → file manager, image viewer, shell
Video codecs     → media player, conferencing, browser
Credentials      → browser, SSH, VPN, applications
Notifications    → browser, mail, calendar, native apps
```

HTML DOM does not pass this test strongly enough.

---

## 4.2 Natural authority boundary

The subsystem should own access to resources or policy that are naturally system-wide:

- camera;
- microphone;
- screen capture;
- credentials;
- notifications;
- location;
- GPU;
- printing;
- secure keys.

The browser should not independently reinvent authority over these resources.

---

## 4.3 Hardware or platform specialization

If implementation quality depends significantly on the machine or device, the platform should usually own the abstraction.

Examples:

```text
GPU rendering
video decode
audio output
camera capture
font access
HDR/color pipeline
hardware cryptography
```

---

## 4.4 Common security surface

Security-sensitive parsers and codecs are attractive candidates when they can be isolated once and shared.

Examples:

- image decoders;
- media codecs;
- certificate handling;
- credential storage;
- document/PDF decoding.

---

## 4.5 Stable non-browser contract

A candidate should be expressible without exposing browser concepts.

Good:

```text
DecodeImage(stream)
ShapeText(text, font, options)
RequestCamera(criteria)
ShowNotification(...)
CreateRenderSurface(...)
```

Bad:

```text
RenderHTMLElement(...)
CreateDOMWindow(...)
ApplyCSSStyle(...)
```

---

# 5. Three implementation forms

"Platform service" does not always mean "global daemon".

WitOS should distinguish at least three forms.

## 5.1 Shared managed library/provider

Used when the code is safe and efficient in-process.

Examples may include:

- Unicode algorithms;
- text shaping;
- some image decoders;
- MIME parsing;
- compression;
- URL parsing;
- portions of PDF generation.

```text
Application
    ↓
shared managed assembly
    ↓
platform/resource API
```

---

## 5.2 Brokered system service

Used when authority, secrets or system policy should remain outside the application.

Examples:

- credentials/passkeys;
- notifications;
- permission mediation;
- printing;
- location;
- background transfers;
- some media/device operations.

```text
Application
    ↓ IPC / typed capability
System Service
    ↓
protected resource
```

---

## 5.3 Resource/provider abstraction

Used for hardware-facing or performance-critical functionality.

Examples:

- graphics device;
- compositor;
- camera;
- microphone;
- audio endpoint;
- GPU compute;
- display surface.

```text
Application
    ↓
Capability<T>
    ↓
Provider
    ↓
Device / service / remote implementation
```

The application should depend on capabilities and semantics rather than on whether the implementation is local, remote, in-process or brokered.

---

# 6. Proposed service families

The following service families are strong candidates for WitOS platform ownership.

---

# 7. Text, Fonts and Internationalization Platform

## 7.1 Motivation

Text rendering is far more complex than "draw glyphs".

A modern browser requires:

- font discovery;
- font matching;
- fallback;
- OpenType shaping;
- ligatures;
- kerning;
- script shaping;
- Unicode normalization;
- grapheme segmentation;
- word segmentation;
- bidirectional text;
- line breaking;
- hyphenation;
- variable fonts;
- color fonts;
- emoji;
- locale-sensitive behavior;
- language/script fallback.

The same functionality is needed by:

- shell;
- terminal;
- editors;
- IDEs;
- document applications;
- PDF;
- office-like software;
- accessibility tools;
- GUI frameworks.

This should therefore not belong exclusively to the browser.

---

## 7.2 Proposed architecture

```text
Application / Browser / GUI Toolkit
              ↓
        OutWit.OS.Text
              │
      ┌───────┼──────────┐
      ▼       ▼          ▼
 Font DB   Shaping    Unicode/Locale
      │       │          │
      └───────┴──────────┘
              ↓
          Glyph Runs
              ↓
        Graphics Platform
```

Illustrative concepts:

```csharp
FontFamily
FontFace
FontInstance
FontCollection
FontQuery
TextRun
GlyphRun
TextShapingOptions
LineBreakOpportunity
TextDirection
Script
LanguageTag
```

---

## 7.3 Browser responsibility

The browser still owns CSS semantics such as:

```text
font-family
font-weight
font-stretch
font-style
font-feature-settings
font-variation-settings
@font-face
CSS fallback rules
```

The browser translates those semantics into platform font/shaping requests.

The system does not understand CSS.

---

## 7.4 Web fonts

Downloaded fonts should not automatically become globally installed fonts.

A browser may create an application/origin-scoped font collection:

```text
System Fonts
      +
Browser Origin Fonts
      ↓
FontCollection
```

This preserves isolation while using the same shaping engine.

---

## 7.5 Acceptance goal

Given the same font assets and shaping inputs, browser, editor and PDF renderer should receive compatible glyph runs from the same platform service.

---

# 8. Graphics, Rendering and Compositor Platform

## 8.1 Motivation

A browser requires a sophisticated rendering pipeline, but much of that pipeline is generic graphics infrastructure.

The system should provide reusable concepts for:

- surfaces;
- textures;
- paths;
- brushes;
- gradients;
- clips;
- transforms;
- layers;
- display lists;
- command buffers;
- filters;
- GPU resources;
- synchronization;
- color spaces;
- HDR;
- presentation timing;
- composition.

---

## 8.2 Layering

```text
Browser
    HTML/CSS layout
        ↓
    browser paint model
        ↓
WitOS Graphics
        ↓
WitOS Compositor
        ↓
GPU / Display
```

The browser should not hand raw DOM/CSS to the compositor.

Instead it should emit generic graphical operations.

---

## 8.3 Possible API layers

```text
OutWit.OS.Graphics.Abstractions
OutWit.OS.Graphics
OutWit.OS.Presentation
OutWit.OS.Composition
```

Illustrative objects:

```csharp
IRenderSurface
IGraphicsDevice
ICommandQueue
ITexture
IBuffer
IPath
IPaint
IDisplayList
ICompositorLayer
IPresentationTarget
```

---

## 8.4 Display lists

A shared display-list representation is worth considering.

Potential advantages:

- retained rendering;
- GPU upload optimization;
- remote presentation;
- serialization for debugging;
- cached layers;
- compositor reuse;
- browser and GUI toolkit convergence at a low level without sharing widget semantics.

However, the display-list contract must remain generic.

It must not contain:

```text
HTML element
CSS selector
DOM node
browser frame
```

---

## 8.5 Browser advantage

A WitOS-native browser could potentially avoid redundant composition layers.

Instead of:

```text
Browser compositor
      ↓
OS window surface
      ↓
OS compositor
      ↓
GPU
```

WitOS may support:

```text
Browser layer tree
      ↓
WitOS compositor
      ↓
GPU
```

This can reduce copies and synchronization boundaries while preserving application isolation.

---

# 9. WebGPU and System GPU API

WebGPU deserves special consideration because its conceptual model is already a modern cross-platform GPU abstraction.

Typical concepts include:

```text
Adapter
Device
Queue
Buffer
Texture
Sampler
ShaderModule
Pipeline
CommandEncoder
CommandBuffer
```

These concepts are useful outside the browser for:

- games;
- GUI rendering;
- scientific visualization;
- compute;
- machine learning;
- image processing;
- video processing.

WitOS should investigate whether its native high-level graphics/compute API can be intentionally close to WebGPU semantics.

Possible relationship:

```text
WebGPU API
    ↓
Browser WebGPU binding
    ↓
WitOS GPU capability/API
    ↓
GPU provider
```

rather than:

```text
Browser-specific GPU subsystem
    ↓
translation layer
    ↓
WitOS-specific GPU subsystem
```

This does not mean the WitOS API must literally be WebGPU.

It means unnecessary conceptual divergence should be avoided.

---

# 10. Image Codec Platform

## 10.1 Formats

A common image service may support providers for:

```text
PNG
JPEG
GIF
WebP
AVIF
BMP
ICO
potential future formats
```

SVG remains primarily a structured document/graphics format and should not simply be treated as an ordinary raster codec.

---

## 10.2 Architecture

```text
Application
    ↓
Image Codec API
    ↓
format provider
    ↓
decoded pixel/surface resource
```

Illustrative API:

```csharp
ImageInfo Probe(Stream source);
ValueTask<IDecodedImage> DecodeAsync(...);
ValueTask EncodeAsync(...);
```

---

## 10.3 Security isolation

Image parsing processes attacker-controlled input.

A provider may therefore run:

- in-process for trusted/simple formats;
- in an isolated execution context;
- in WASM;
- in a sandboxed codec service.

The caller should not need to know which.

```text
Browser
   ↓
ImageDecode capability
   ↓
sandboxed codec provider
   ↓
shared/read-only decoded surface
```

---

# 11. Media Platform

## 11.1 Scope

The platform should provide reusable low-level media capabilities:

- demux;
- audio decode;
- video decode;
- audio encode;
- video encode;
- hardware acceleration;
- audio playback;
- video surfaces;
- camera capture;
- microphone capture;
- A/V synchronization;
- codec enumeration;
- media device discovery.

Potential containers/codecs include:

```text
MP4
WebM
AV1
VP9
H.264 where legally/distribution-wise appropriate
Opus
AAC where appropriate
PCM
```

Licensing/patent constraints must be handled separately from API architecture.

---

## 11.2 Browser boundary

The browser owns:

```text
HTMLMediaElement
MediaSource
MediaStream web semantics
WebCodecs web bindings
WebAudio semantics
autoplay policy
origin policy
```

The platform owns:

```text
decode this stream
encode this stream
play these samples
capture from this device
present this decoded frame
```

---

## 11.3 Hardware acceleration

The caller should request capability/requirements rather than specific vendor APIs.

```csharp
VideoDecoderRequirements
{
    Codec = "av1",
    PreferHardware = true,
    LowLatency = false
}
```

The provider may resolve to:

- CPU decoder;
- GPU decoder;
- dedicated media block;
- remote resource.

---

# 12. Camera, Microphone and Screen Capture

Capture should be a normal WitOS resource model.

```text
Camera
Microphone
Screen / Window / Surface Capture
```

Access is capability-based.

The browser maps:

```text
getUserMedia()
getDisplayMedia()
```

to explicit system capability acquisition.

The browser does not own the underlying device permission database.

---

## 12.1 Example flow

```text
Web origin asks for microphone
        ↓
Browser validates web policy
        ↓
Browser requests Microphone capability
        ↓
Trusted WitOS consent UI
        ↓
Scoped capability returned
        ↓
Browser exposes MediaStream
```

Revocation can revoke the actual capability.

---

# 13. Credential, Authentication and Secret Platform

## 13.1 Scope

The system should own secure storage and use of:

- passkeys;
- private keys;
- certificates;
- hardware-backed keys;
- application credentials;
- potentially passwords;
- authentication tokens where appropriate.

Possible services:

```text
OutWit.OS.Identity
OutWit.OS.Credentials
OutWit.OS.Authentication
OutWit.OS.Trust
```

---

## 13.2 Browser integration

```text
WebAuthn
    ↓
Browser validation/origin semantics
    ↓
WitOS Credential API
    ↓
platform authenticator / TPM / phone / security key
```

The browser never receives exportable private key material unless the credential type explicitly permits it.

---

## 13.3 Non-browser users

The same credential infrastructure can support:

- SSH;
- VPN;
- enterprise login;
- package signing;
- application authentication;
- database clients;
- Git clients.

This strongly justifies system ownership.

---

# 14. Permission Broker

The capability system provides authority, but a higher-level permission broker may coordinate human consent.

The broker should understand generic resource requests, not browser APIs.

Examples:

```text
Camera
Microphone
Location
Notifications
Bluetooth
USB
Serial
MIDI
Clipboard read
Screen capture
Selected files/directories
```

The browser translates web semantics to generic requests.

Native applications use the same broker.

---

## 14.1 Trusted consent UI

Consent must not be rendered by arbitrary untrusted application content.

The system should have a trusted presentation path for security-sensitive prompts.

The prompt identifies:

- requesting application;
- delegated web origin if relevant;
- requested resource;
- requested scope;
- duration;
- persistence options.

Example:

```text
Browser: OutWit Browser
Origin: https://meet.example
Requests: Microphone
Scope: Built-in microphone
Duration: While application is active
```

---

# 15. Notification Service

Notifications are naturally system-level.

```text
Application / Browser Origin
        ↓
Notification Service
        ↓
Shell / device / remote endpoint
```

Capabilities may control:

- posting;
- updating;
- grouping;
- actions;
- urgency;
- persistence.

For installed web applications, the system may associate a browser-hosted origin with a WitOS `ApplicationId`.

The browser still owns web notification permission semantics and Service Worker integration.

---

# 16. Clipboard and Data Transfer

WitOS should define a generic typed data transfer model usable by:

- clipboard;
- drag-and-drop;
- browser `DataTransfer`;
- inter-application transfer.

Potential representations:

```text
text/plain
text/html
image/*
URI list
structured application types
resource capability
stream capability
```

Files should preferably be transferred as authorized resource handles/capabilities rather than ambient filesystem paths.

Example:

```text
File Manager
    ↓ drag
ReadCapability<File>
    ↓
Browser
    ↓
<input type=file>
```

The web application receives the browser-level `File` abstraction, not the underlying system capability.

---

# 17. Accessibility Platform

The system should expose an accessibility tree/protocol independent of any GUI technology.

Common concepts:

```text
Role
Name
Description
Value
State
Actions
Bounds
Focus
Selection
Relationships
Events
```

Adapters can be provided for:

```text
Avalonia
Browser DOM
Terminal
custom UI toolkits
```

Browser architecture:

```text
DOM/layout
    ↓
browser accessibility mapping
    ↓
WitOS accessibility nodes
    ↓
screen reader / assistive service
```

WitOS must not require DOM to implement accessibility.

---

# 18. Location and Sensor Platform

Relevant resources include:

- geographic location;
- accelerometer;
- gyroscope;
- magnetometer;
- device orientation;
- ambient light;
- proximity;
- potentially health/environmental sensors on specialized devices.

Applications request typed sensor capabilities.

The provider may be:

- physical sensor;
- fused sensor service;
- remote device;
- unavailable.

The browser maps relevant Web APIs onto these capabilities.

Privacy policy belongs to the system capability/consent layer plus browser origin policy.

---

# 19. Background Transfer Service

Long-running transfers should not necessarily belong to the lifetime of a browser tab or application process.

A reusable transfer service may support:

- download;
- upload;
- pause/resume;
- retry;
- checksums;
- bandwidth policy;
- network transition;
- battery/background policy;
- destination resource capabilities.

```text
Browser Download
Package Manager
Updater
Cloud Sync
Native Application
        ↓
Background Transfer Service
```

A browser download manager becomes UI/policy around a generic transfer primitive.

---

## 19.1 Capability example

```text
Source:
    HTTPS URL

Destination:
    WriteCapability<UserDownloads/SomeFile>

Policy:
    Continue in background
    Wi-Fi preferred
    Verify SHA-256 if supplied
```

The service receives only the destination authority delegated to that transfer.

---

# 20. Printing Platform

Printing is a shared system capability.

The platform should provide:

- printer discovery;
- printer capabilities;
- job creation;
- print queue;
- page representation;
- progress/cancel;
- policy/accounting where relevant.

Browser responsibilities:

- CSS print semantics;
- page layout;
- browser print preview;
- conversion of web content into printable pages.

System responsibilities:

```text
print these pages using these printer settings
```

---

# 21. PDF Services

PDF requires careful separation.

Possible reusable system capabilities:

- PDF generation;
- PDF parsing;
- PDF rendering;
- document metadata;
- printing integration.

However, the browser should not require the system shell to be built on a PDF engine.

PDF should remain a replaceable platform/document provider.

A sandbox boundary may be desirable for parsing untrusted PDF data.

---

# 22. Cryptography, Certificates and Trust

Much of this already belongs naturally in standard .NET, but WitOS should define how BCL functionality maps onto:

- system trust stores;
- hardware keys;
- certificate policy;
- secure entropy;
- revocation information;
- application trust/signatures;
- user-managed roots.

The browser should use standard .NET/platform cryptography where semantics match.

Browser-specific TLS/web PKI policy may still require additional browser logic.

The browser must not invent a separate generic cryptographic subsystem merely because Chromium does.

---

# 23. Compression and Content Encoding

Compression algorithms such as:

```text
gzip
deflate
brotli
zstd where useful
```

should be reusable platform/BCL-level components.

The browser owns HTTP/web content-encoding semantics.

The compressor/decompressor itself is not browser-specific.

---

# 24. MIME and Content Type Registry

A system-wide registry may map:

- file extensions;
- media types;
- application handlers;
- preferred viewers/editors;
- sniffing policy where appropriate.

Browser security-sensitive MIME sniffing semantics remain browser-owned.

The system registry should provide descriptive metadata, not override web-standard security rules.

---

# 25. Storage Primitives

The system should **not** provide IndexedDB as a universal OS API.

Instead it should provide strong reusable storage primitives.

Candidates:

```text
transactional key/value
relational/file database
blob storage
atomic replace
journaling
quota
cache storage primitives
```

WitDatabase is an obvious candidate for some of these roles.

Browser layering:

```text
IndexedDB / CacheStorage / browser profile storage
            ↓
browser semantic implementation
            ↓
WitOS storage/database primitives
            ↓
storage capability
```

The system does not know what an IndexedDB object store is.

---

# 26. WebRTC and Realtime Communications

WebRTC should be split.

Browser-owned layer:

```text
RTCPeerConnection
MediaStream web API
web permission semantics
origin policy
JavaScript bindings
```

Potential shared lower layer:

```text
ICE
STUN
TURN
DTLS
SRTP
RTP/RTCP
jitter buffering
echo cancellation
noise suppression
A/V synchronization
network adaptation
```

This lower realtime communications stack can also support:

- native conferencing;
- VoIP;
- remote desktop;
- game communication;
- telepresence.

It should be investigated as a separate platform service rather than automatically placed in the browser.

---

# 27. Diagnostics, Tracing and Profiling

Platform services should expose common diagnostics infrastructure.

Examples:

- structured logs;
- tracing spans;
- CPU profiling;
- allocation profiling;
- GPU timing;
- media pipeline diagnostics;
- network timing;
- runtime metrics;
- crash diagnostics.

The browser can surface browser-specific DevTools while consuming system diagnostics underneath.

A WebContent process should be observable through normal WitOS diagnostics, not exclusively through browser-private instrumentation.

---

# 28. Resource Quotas and Accounting

Browser processes currently implement many resource limits internally.

WitOS can enforce generic quotas at the system layer:

- CPU;
- memory;
- GPU memory;
- background time;
- network;
- storage;
- thread count;
- executable memory;
- media devices.

The browser adds origin/site-level policy and maps it onto execution/resource limits.

Example:

```text
Browser decides:
    background origin gets reduced CPU budget
          ↓
WitOS ExecutionContext quota
          ↓
scheduler enforcement
```

---

# 29. Application and Web Application Integration

A web application may optionally be represented as a WitOS `Application`.

Example:

```text
Installed PWA
    ApplicationId
    browser engine association
    origin
    icon/name
    lifecycle
    notifications
    presentation endpoint
    persistent grants
```

The browser still provides the web execution environment.

WitOS provides application identity/lifecycle/resource integration.

This allows:

- task switching;
- independent windows;
- notifications;
- file associations;
- launch intents;
- capability grants;
- background policy.

The browser remains replaceable.

The association should be:

```text
Application
    requires WebApplicationHost capability/provider
```

rather than hard-coded to one browser executable.

---

# 30. What must remain browser-owned

The following should **not** become generic WitOS services merely to help the default browser:

- HTML tokenizer/tree builder;
- DOM;
- CSSOM;
- CSS selector matching;
- CSS cascade;
- web layout;
- HTML form semantics;
- browser event loop semantics;
- `Window`;
- `Document`;
- `Navigator`;
- Fetch API semantics;
- same-origin policy;
- CORS;
- CSP;
- browser cookie semantics;
- browser HTTP cache semantics;
- browser history;
- navigation;
- Service Workers;
- IndexedDB semantics;
- CacheStorage semantics;
- web permissions semantics;
- web origin isolation;
- DOM accessibility mapping;
- Web API JavaScript bindings.

These define the web platform.

They belong in the browser engine.

---

# 31. Browser responsibility vs platform responsibility

A useful rule:

```text
Web meaning       → Browser
Generic mechanism → Platform
Hardware access   → Platform
Authority         → Capabilities / Platform
```

Examples:

| Feature | Browser owns | Platform owns |
|---|---|---|
| Fonts | CSS matching semantics, web-font lifecycle | font discovery, shaping, glyphs |
| Camera | `getUserMedia`, origin semantics | device/capture capability |
| Video | HTML/WebCodecs semantics | codec and hardware decode |
| GPU | WebGPU API semantics | GPU resource/device provider |
| Files | File API semantics | storage resource capability |
| Notifications | Web Notifications semantics | actual notification delivery |
| Credentials | WebAuthn/origin semantics | authenticator/keys |
| Clipboard | Clipboard Web API policy | clipboard/data-transfer mechanism |
| Accessibility | DOM→AX mapping | system accessibility protocol |
| Storage | IndexedDB semantics | durable transactional primitives |
| Printing | CSS print layout | printers/jobs |
| WASM | Web API hosting rules | shared WASM runtime |
| JS | DOM/Web APIs/bindings | ECMAScript runtime |

---

# 32. Portability model

The browser must remain usable outside WitOS.

The browser core should target standard .NET:

```text
Browser.Core
Browser.Html
Browser.Dom
Browser.Css
Browser.Layout
Browser.Web
Browser.Rendering
```

Platform dependencies belong behind abstractions/providers.

Example:

```text
Browser.Platform.Abstractions
    │
    ├── Browser.Platform.WitOS
    ├── Browser.Platform.Windows
    ├── Browser.Platform.Linux
    └── Browser.Platform.macOS
```

Where standard .NET already provides an adequate abstraction, the browser should use it directly instead of inventing another provider.

Examples:

```text
HttpClient
SslStream
Stream
Task
Thread
System.Security.Cryptography
```

The platform abstraction layer exists only where genuinely necessary.

---

# 33. WitOS integration advantage

The same managed browser may have different integration depth depending on host capabilities.

On a conventional host:

```text
Managed Browser
      ↓
generic desktop integration
      ↓
Windows/Linux/macOS
```

On WitOS:

```text
Managed Browser
      ↓
native capabilities/resources
      ↓
WitOS platform services
```

Potential advantages include:

- capability-native permissions;
- first-class web applications;
- direct composition;
- zero-copy media surfaces;
- shared JS/WASM runtimes;
- system credential integration;
- isolated codec services;
- native background transfers;
- unified notifications;
- unified accessibility;
- resource-aware scheduling;
- distributed resources where appropriate.

This is deep integration without coupling browser correctness to WitOS.

---

# 34. Provider model

Most platform services should be replaceable providers.

Example:

```csharp
public interface IImageCodecProvider
{
    bool Supports(MediaType type);

    ValueTask<IImageResource> DecodeAsync(
        Stream source,
        ImageDecodeOptions options,
        CancellationToken cancellationToken = default);
}
```

The exact interfaces will be defined separately.

The architectural requirement is:

> Consumers depend on capabilities and stable contracts, not implementation brands.

The platform may choose providers according to:

- hardware;
- format;
- latency;
- power;
- trust;
- locality;
- quality;
- cost;
- availability.

---

# 35. Local and remote services

WitOS resource abstractions allow some services to be remote where semantics permit.

Examples:

```text
printing
background transfer
large media transcode
AI processing
WASM compute
remote presentation
```

Latency-sensitive services such as interactive text shaping or normal browser layout should usually remain local.

Remote placement must be explicit or policy-driven based on requirements.

The API should not assume that "service" means "another local process".

---

# 36. Security model

Shared services increase reuse but also enlarge blast radius if designed poorly.

Required principles:

## 36.1 Least authority

Services receive only capabilities required for a request.

Example:

```text
Image decoder
    gets:
        read-only encoded stream
        output surface capability

    does not get:
        user filesystem
        network
        microphone
```

---

## 36.2 Isolation for hostile parsers

High-risk codecs/parsers may execute in constrained address spaces or WASM sandboxes.

---

## 36.3 No ambient device access

A browser's media subsystem should not enumerate/use arbitrary camera devices merely because it is the default browser.

---

## 36.4 Explicit delegation

If a browser obtains a camera capability for an origin, it delegates a narrower browser-level object to web content.

The web application never receives the raw system capability.

---

# 37. Versioning

Platform services must avoid creating a new "one system DLL version breaks all applications" problem.

Strategies:

- stable small contracts;
- side-by-side provider versions where needed;
- semantic feature discovery;
- compatibility adapters;
- versioned data formats;
- no unnecessary inheritance of provider implementation types into public APIs.

For browser compatibility, reproducibility may sometimes require pinning a provider or runtime version.

That must remain possible.

---

# 38. Deployment profiles

WitOS profiles may install different service sets.

## 38.1 Minimal/server

```text
CoreCLR
PowerShell
network/storage
credentials
basic text terminal
```

No browser, media stack or GUI required.

---

## 38.2 Desktop

```text
CoreCLR
JavaScript Runtime
WebAssembly Runtime
Text/Fonts
Graphics/Compositor
Image Codecs
Media
Credentials
Notifications
Accessibility
Printing
Browser
GUI Shell
```

---

## 38.3 Embedded

Only required providers are installed.

Example:

```text
CoreCLR
Text
small display
specific image codec
device-specific resources
```

---

# 39. Failure isolation

A failure in a replaceable service should not unnecessarily crash the caller.

Examples:

```text
image codec crash
    → image decode fails

printer service crash
    → print job fails

media decoder crash
    → stream playback fails
```

The system shell/browser should survive where practical.

Not every library call requires a process boundary.

The boundary should reflect risk and cost.

---

# 40. Performance principles

Platform services must not become abstraction tax.

Rules:

1. no IPC on hot paths where in-process capability/object calls are safe;
2. zero-copy/shared surfaces where large image/video data is transferred;
3. batch APIs for graphics;
4. immutable shared resources where possible;
5. async for genuinely asynchronous I/O/device work;
6. sync APIs may remain appropriate for CPU-local operations such as shaping;
7. provider indirection must not force allocation-heavy virtual dispatch in inner rendering loops;
8. browser and GUI benchmarking must validate abstractions continuously.

---

# 41. Example end-to-end browser page

Consider a modern web page containing text, images, video, WebAssembly and a camera request.

```text
HTML/CSS
    ↓
Browser DOM/CSS/Layout
    │
    ├── text
    │     ↓
    │   Text/Font Service
    │     ↓
    │   GlyphRuns
    │
    ├── image
    │     ↓
    │   Image Codec Service
    │
    ├── video
    │     ↓
    │   Media Service
    │     ↓
    │   decoded video surface
    │
    ├── WebAssembly
    │     ↓
    │   System WASM Runtime
    │
    ├── JavaScript
    │     ↓
    │   System JS Runtime
    │
    └── getUserMedia()
          ↓
        Permission Broker
          ↓
        Camera Capability

Browser paint
    ↓
Graphics/DisplayList
    ↓
WitOS Compositor
    ↓
GPU
```

Browser-specific web semantics remain at the top.

Reusable mechanisms come from the platform.

---

# 42. Example non-browser reuse

The same system may then support:

```text
Photo Viewer
    → Text
    → Image Codecs
    → Graphics

Video Editor
    → Text
    → Media
    → Graphics
    → GPU

IDE
    → Text
    → Graphics
    → Credentials
    → Notifications

PowerShell Tool
    → Credentials
    → Background Transfers
    → WASM Runtime

Scientific Viewer
    → Graphics
    → GPU Compute
    → Text
    → Image/Media Codecs
```

The investment made for the browser improves the entire platform.

---

# 43. Relationship to Avalonia

Avalonia should remain a candidate GUI toolkit, not a system identity.

Where useful, Avalonia may consume the same WitOS platform services:

```text
Avalonia
    ↓
WitOS Text
WitOS Graphics
WitOS Input
WitOS Accessibility
```

The browser should not be forced to use Avalonia internally for web rendering.

Likewise, Avalonia should not be forced to use browser layout/rendering.

They may converge only on common low-level services.

---

# 44. Relationship to standard .NET

WitOS should prefer upstream .NET functionality whenever it is already sufficient.

Do not create:

```text
OutWit.OS.HttpClient
OutWit.OS.Stream
OutWit.OS.Task
OutWit.OS.Cryptography
```

merely to create a branded platform layer.

The goal is:

```text
standard .NET first
WitOS-specific API only where .NET has no suitable portable contract
```

This is critical for browser portability and developer experience.

---

# 45. Relationship to RFC0018

RFC0018 defines runtime platform concepts and establishes:

```text
.NET
JavaScript
WebAssembly
```

as reusable execution runtimes.

This RFC extends the same idea to non-language platform functionality.

Together:

```text
RFC0018
    Runtime Platform

RFC0019
    Shared Platform Services
```

The browser consumes both.

Neither RFC makes the browser mandatory.

---

# 46. Proposed namespace sketch

This is exploratory.

```text
OutWit.OS.Text
OutWit.OS.Graphics
OutWit.OS.Presentation
OutWit.OS.Media
OutWit.OS.Images
OutWit.OS.Credentials
OutWit.OS.Authentication
OutWit.OS.Notifications
OutWit.OS.Accessibility
OutWit.OS.Location
OutWit.OS.Sensors
OutWit.OS.Transfer
OutWit.OS.Printing
OutWit.OS.DataTransfer
OutWit.OS.Runtime
```

Low-level contracts may live in:

```text
*.Abstractions
```

only where a separate package genuinely improves dependency structure.

Avoid package fragmentation for its own sake.

---

# 47. Priority classification

Not all services need to exist before the browser.

## Tier A — foundational

Strongest candidates:

```text
Runtime: JavaScript / WebAssembly
Text / Fonts / Shaping
Graphics / Compositor
Credentials / Authentication
Permission / Capability Broker
```

These affect architecture early.

---

## Tier B — highly reusable desktop/browser infrastructure

```text
Image Codecs
Media
Camera / Microphone / Capture
Notifications
Accessibility
Clipboard / Data Transfer
Printing
Background Transfers
```

---

## Tier C — investigate before committing

```text
Realtime communications / WebRTC lower stack
PDF rendering
Location/Sensors beyond basic resources
system-wide MIME registry
shared browser-oriented caches
high-level GPU API aligned with WebGPU
```

These may be valuable but need dedicated RFCs or prototypes.

---

# 48. Implementation sequencing

The service architecture should evolve through vertical slices rather than attempting to design every subsystem before implementation.

Possible sequence after CoreCLR maturity:

```text
P0 Runtime services       JS/WASM foundations
P1 Text                   system fonts + shaping
P2 Graphics               surfaces + display list + composition
P3 Image                  PNG/JPEG/WebP pipeline
P4 Credentials            secrets/passkeys/authenticator
P5 Permissions            trusted resource consent flow
P6 Notifications          shell delivery
P7 Media                  audio/video/capture foundations
P8 Accessibility          common accessibility tree/protocol
P9 Transfer/Printing      long-running OS-mediated operations
P10 Browser integration   provider-backed browser host
```

Actual ordering should follow the next complete runnable WitOS slice.

---

# 49. Browser milestone implications

A browser milestone should not wait for every system service.

Hosted development on Windows/Linux can initially use ordinary .NET or library providers.

Example:

```text
Browser.Text
    ├── HostedFontProvider
    └── WitOsFontProvider

Browser.Graphics
    ├── HostedGraphicsProvider
    └── WitOsGraphicsProvider
```

Over time the WitOS path becomes the richer native integration path.

The browser must remain runnable during the transition.

---

# 50. Acceptance tests

## 50.1 Browser removability

Remove the browser.

WitOS must still:

- boot;
- present the shell;
- run PowerShell;
- access storage;
- configure the system;
- run .NET applications.

---

## 50.2 Alternative browser

A second browser must be able to acquire the same public graphics, text, media, credentials and permission capabilities.

---

## 50.3 Text reuse

The same text shaping service must be usable by at least:

- browser;
- native GUI application;
- document or terminal-related application.

---

## 50.4 Codec isolation

Crash/fault a sandboxed image decoder.

The requesting browser/application must receive a decode failure rather than a system crash.

---

## 50.5 Permission revocation

Grant microphone access to a browser-hosted origin.

Revoke it through trusted system policy.

The browser must lose effective microphone authority without requiring access to hidden browser-only state.

---

## 50.6 Credential isolation

A browser may use a passkey through the credential service but must not gain raw private-key material.

---

## 50.7 Browser portability

The browser core must build as ordinary standard .NET code outside WitOS.

---

## 50.8 No browser semantics below boundary

Platform service packages must not expose public types referring to:

```text
DOM
HTMLElement
CSSStyle
Window
Document
Origin
ServiceWorker
```

unless they are explicitly browser-layer packages.

---

# 51. Open questions

1. Should text shaping be in-process by default or brokered only for untrusted font parsing?
2. What is the correct boundary between `Graphics` and `Presentation`?
3. Should the compositor accept a generic retained layer tree, display lists, or both?
4. Should the high-level GPU API deliberately track WebGPU concepts?
5. Which image codecs belong in the default desktop profile?
6. Should media codecs run in separate execution contexts by default?
7. Which credentials are system-managed versus application-managed?
8. How should web origin identity be represented to the generic permission broker?
9. Should installed PWAs receive stable WitOS `ApplicationId`s?
10. How should persistent web permission grants map to persistent capability grants?
11. Should background transfer state survive browser/application upgrades?
12. Which parts of WebRTC are sufficiently generic to become a platform service?
13. Should PDF parsing/rendering become a standard service or remain a normal application/library?
14. How should color management and HDR be represented across browser, media and compositor?
15. Which diagnostics should be standardized across browser, media and graphics?
16. How much provider version pinning should applications be allowed?
17. Which service APIs belong in standard `.NET`-style libraries versus `OutWit.OS.*`?
18. Can font, image and media parsers be safely sandboxed through WASM implementations where practical?
19. How should remote providers expose latency/cost characteristics?
20. Which services are required for the first GUI browser milestone versus later desktop maturity?

---

# 52. Architectural invariants

The following should be treated as hard constraints unless revised explicitly.

> **WitOS does not depend on the browser.**

> **The browser is removable, replaceable and independently updateable.**

> **Deep integration is implemented through public platform capabilities, not browser-specific privilege.**

> **Browser-specific web semantics remain in the browser.**

> **Generic mechanisms, hardware access and authority belong in reusable platform services where appropriate.**

> **A platform service is not necessarily a daemon; use in-process libraries, providers and brokers according to the actual boundary.**

> **Standard .NET APIs are preferred whenever they already provide the required portable contract.**

> **Platform services must not force applications to emulate Linux, Windows or another operating system.**

> **Capabilities define authority. Resource identifiers and API availability do not.**

> **High-risk parsers/codecs should be isolatable without forcing all callers through high-overhead IPC.**

> **The default browser receives no hidden authority unavailable to another authorized browser.**

> **The shell and GUI framework do not depend on HTML/DOM/CSS.**

> **Portable applications should remain portable even when WitOS provides deeper native integration.**

---

# 53. Long-term architecture

The long-term platform can be viewed as four layers:

```text
┌────────────────────────────────────────────────────────────┐
│ Applications                                               │
│ Browser / Shell / IDE / Media / Tools / .NET / WASM Apps │
└────────────────────────────────────────────────────────────┘
                             │
┌────────────────────────────────────────────────────────────┐
│ Application-specific platforms                             │
│ Web Platform / GUI Toolkit / Domain Frameworks            │
└────────────────────────────────────────────────────────────┘
                             │
┌────────────────────────────────────────────────────────────┐
│ Shared WitOS Platform Services                             │
│ .NET / JS / WASM                                          │
│ Text / Graphics / Media / Images                          │
│ Credentials / Permissions / Notifications                 │
│ Accessibility / Transfer / Printing / Sensors             │
└────────────────────────────────────────────────────────────┘
                             │
┌────────────────────────────────────────────────────────────┐
│ WitOS Core                                                 │
│ Resources / Capabilities / Execution / IPC / Storage      │
│ Network / Devices / Memory / Kernel                       │
└────────────────────────────────────────────────────────────┘
```

This gives the browser a much stronger host environment than a conventional cross-platform application while preserving clean architectural boundaries.

---

# 54. Strategic consequence

If WitOS eventually provides:

```text
standard .NET
+
JavaScript runtime
+
WebAssembly runtime
+
text/font shaping
+
graphics/compositor
+
media/codecs
+
credentials
+
capability-native permissions
+
notifications/accessibility/device services
```

then a managed browser no longer has to recreate an entire private operating-system substrate.

Its core responsibility becomes the actual web platform:

```text
HTML
DOM
CSS
layout
web event model
navigation
origin/security semantics
Web APIs
```

That is still a very large engineering task, but it is a much cleaner one.

At the same time, every major investment made to support the browser improves ordinary WitOS applications.

This is the desired direction:

> **Do not embed a browser into the operating system. Build an operating system whose reusable services are good enough that a browser does not need to embed another operating system inside itself.**
