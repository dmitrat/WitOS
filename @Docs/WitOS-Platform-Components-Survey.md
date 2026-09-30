# WitOS Platform Components Survey

**Subtitle:** Reuse Candidates, Bootstrap Implementations and Reference Architectures  
**Status:** Research Draft v0.1  
**Date:** 2026-09-29  
**Related:** RFC0018 — System Runtime Platform and WebAssembly Application Model; RFC0019 — Shared Platform Services for Browser, GUI and Applications

---

## 1. Purpose

This document surveys existing open-source projects, standard .NET facilities and external reference architectures that can reduce the amount of code WitOS must implement from scratch.

The scope is deliberately broader than the browser itself.

The target architecture from RFC0018/RFC0019 assumes that a modern managed browser should consume reusable platform services:

```text
Applications
    |
    +-- Managed Browser
    +-- Shell
    +-- IDE / Editors
    +-- Media Applications
    +-- Scientific / Visualization Applications
    +-- .NET Applications
    +-- WebAssembly Applications
             |
             v
       WitOS Platform Services
             |
    +--------+--------------------------------------------------+
    v        v         v        v        v         v            v
   JS       WASM      Text    Graphics  Images    Media     Credentials
             |
             v
        WitOS Resources,
        Capabilities,
        Execution and Devices
```

The goal of this survey is to answer, for each subsystem:

1. Can we use an existing managed implementation directly?
2. Can we use it as a bootstrap implementation behind a WitOS abstraction?
3. Should we only use it as a behavioral/architectural reference?
4. What remains WitOS-specific?
5. What licensing, portability or maintenance risks exist?

This is a research snapshot, not a dependency lock file. Project activity, API shape and license terms must be re-verified before adoption.

---

## 2. Classification

### TAKE

A strong candidate for direct reuse, subject to API fit, conformance testing and license review.

```text
WitOS abstraction
       |
       v
existing managed implementation
```

The external implementation should remain replaceable.

### BOOTSTRAP

Useful as the first implementation, but WitOS should not make its public API or architecture depend on that project.

```text
WitOS abstraction
       |
       v
bootstrap provider
       |
       v
possible replacement later
```

Typical reasons include a performance ceiling, incomplete standards coverage, license concerns, project-specific object model or unsuitable long-term architecture.

### REFERENCE

Use source code, behavior, test suites and architecture for guidance, but do not make it a runtime dependency.

Typical cases are native C/C++/Rust implementations, inactive projects, unsuitable dependency shape, or cases where a clean managed implementation is preferred.

### STANDARD .NET

Do not create a WitOS-specific replacement unless standard .NET proves insufficient.

Examples include:

```text
HttpClient
SslStream
Stream
Task
Thread
System.Security.Cryptography
System.Diagnostics
compression streams
```

This category is strategically important because WitOS intends to be a standard .NET platform.

---

## 3. Executive Summary

The ecosystem is much stronger than the initial architecture discussion suggested.

The strongest immediate candidates are:

```text
WebAssembly        -> WACS
JavaScript         -> Jint for bootstrap/correctness
JS JIT reference   -> YantraJS
Text shaping       -> SixLabors.Fonts
Images             -> ImageSharp or smaller managed codecs
2D rendering       -> ImageSharp.Drawing as bootstrap/reference
Audio core         -> NAudio.Core
Opus               -> Concentus
MP3                -> NLayer
Vorbis             -> NVorbis
Printing protocol  -> SharpIppNext
PDF object model   -> PdfPig
Crypto fallback    -> BouncyCastle.NET
Zstd               -> ZstdSharp
Diagnostics        -> standard System.Diagnostics/EventPipe
Accessibility      -> AccessKit architecture/schema as reference
Permissions        -> XDG Portals + Fuchsia capability model as references
GPU abstraction    -> WebGPU/webgpu.h + Dawn as reference
```

The largest areas that still appear to require substantial WitOS-specific engineering are:

```text
GPU / compositor integration
permission broker and trusted consent UI
credential/authenticator service
modern video stack
system accessibility service/adapters
resource quotas/accounting
browser web-platform implementation
```

The practical consequence is significant:

> The managed browser does not need to recreate an entire private operating-system substrate if WitOS adopts and integrates existing managed components behind stable platform contracts.

---

## 4. Component Matrix

| Area | Candidate | Role | Managed | Initial judgment |
|---|---|---:|---:|---|
| WebAssembly | WACS | TAKE | Yes | Excellent architectural match |
| JavaScript | Jint | TAKE / BOOTSTRAP | Yes | Best correctness/bootstrap candidate |
| JavaScript JIT | YantraJS | REFERENCE / experiment | Yes | Valuable JS-to-IL design reference |
| Text / Fonts | SixLabors.Fonts | TAKE if license fits | Yes | Strongest managed shaping candidate |
| Text alternative | LayoutFarm/Typography | BOOTSTRAP / REFERENCE | Yes | Useful fallback/reference |
| Globalization | ICU4N | REFERENCE / partial TAKE | Yes | Useful but incomplete ICU port |
| Images | ImageSharp | TAKE if license fits | Yes | Mature managed image stack |
| 2D Drawing | ImageSharp.Drawing | BOOTSTRAP / REFERENCE | Yes | Strong CPU + retained + WebGPU concepts |
| GPU contract | WebGPU / webgpu.h | REFERENCE CONTRACT | N/A | Strong candidate model for WitOS GPU API |
| GPU architecture | Dawn | REFERENCE | No | Validation/backend/sandbox-wire reference |
| .NET GPU library | Veldrid | REFERENCE | Managed frontend + native backends | Good API study, inactive publicly |
| Audio core | NAudio.Core | TAKE / BOOTSTRAP | Mostly managed core | Good portable audio pipeline |
| Opus | Concentus | TAKE | Yes | Strong pure managed codec |
| MP3 | NLayer | TAKE | Yes | Mature managed decoder |
| Vorbis | NVorbis | TAKE | Yes | Mature managed decoder |
| Modern video | dav1d/OpenH264/libvpx | REFERENCE | No | No equivalent mature managed stack found |
| WebRTC | SIPSorcery / Pion | REFERENCE | Mixed | Protocol architecture reference |
| WebAuthn | fido2-net-lib | TAKE partial | Yes | Good WebAuthn/RP semantic layer |
| Permissions | XDG Portals | REFERENCE | No | Excellent broker/consent interaction model |
| Capabilities | Fuchsia | REFERENCE | No | Excellent capability routing/no ambient authority model |
| Accessibility | AccessKit | REFERENCE | No, Rust | Excellent schema and update model |
| Printing | SharpIppNext | TAKE | Yes | Very strong protocol candidate |
| PDF | PdfPig | TAKE partial | Yes | Good object model/parser/creation base |
| PDF renderer | PDF.js | REFERENCE / bootstrap experiment | JS | Mature rendering oracle |
| Crypto | System.Security.Cryptography | STANDARD .NET | Yes/runtime | First choice |
| Crypto extended | BouncyCastle.NET | TAKE fallback | Yes | Broad protocol/algorithm coverage |
| Compression | BCL | STANDARD .NET | Yes | First choice |
| Zstd | ZstdSharp | TAKE | Yes | Managed zstd port |
| MIME | MimeKit | TAKE algorithms / REFERENCE | Yes | Mature MIME parser |
| Diagnostics | System.Diagnostics/EventPipe | STANDARD .NET | Yes/runtime | Do not reinvent |
| Storage | WitDatabase | EXISTING OUTWIT | Yes | Strong candidate substrate |

---

# 5. WebAssembly — WACS

**Project:** WACS — WebAssembly CSharp Toolchain  
**Source:** https://github.com/kelnishi/WACS  
**License:** Apache-2.0

WACS is the closest match found to the execution model proposed in RFC0018.

It is a pure C# WebAssembly toolchain with an interpreter runtime, WASM-to-.NET-IL transpilation, NativeAOT path, WASI Preview 1/2 support, current Preview 3 work, Component Model support, canonical ABI lift/lower, WIT-to-C# binding generation and spec-test infrastructure.

Its architecture already demonstrates the central WitOS idea:

```text
.wasm
  |
  v
managed decoder / validator
  |
  +--> interpreter
  |
  +--> generated .NET IL
          |
          v
       CoreCLR JIT
```

### Proposed use

```text
OutWit.OS.Runtime.WebAssembly
              |
              v
         IWasmRuntime
              |
              v
        WacsRuntimeProvider
              |
              v
             WACS
```

WitOS APIs must not expose WACS-specific types.

The provider should adapt WitOS capabilities into WASI/Component imports.

### Immediate technical spike

Test ordinary core modules, a WASI CLI application, Component Model application, WIT-generated typed bindings, WASM-to-IL execution, NativeAOT compatibility, memory limits, traps, threading, SIMD, authority interception, startup overhead and code-cache feasibility.

### Risk

The implementation should be replaceable. Adopt the semantics and provider boundary, not the implementation identity.

---

# 6. JavaScript — Jint

**Project:** Jint  
**Source:** https://github.com/sebastienros/jint  
**License:** BSD-2-Clause

Jint is a mature managed JavaScript interpreter for .NET with modern ECMAScript coverage, CLR interoperability and host-enforced execution limits.

It is a strong candidate for:

```text
1. correctness/bootstrap runtime
2. reference implementation for host API design
```

### Proposed use

```text
OutWit.OS.Runtime.JavaScript
             |
             v
      IJavaScriptRuntime
             |
             v
      JintRuntimeProvider
```

Browser DOM/Web API bindings must live above this boundary.

A plain Jint realm should receive only ECMAScript semantics plus explicitly injected host objects.

### Long-term limitation

A Chromium-class browser will eventually need much higher JavaScript throughput.

Jint is still valuable if replaced later because it can remain a semantic baseline, differential oracle, fallback engine or general scripting runtime.

---

# 7. JavaScript JIT Reference — YantraJS

**Project:** YantraJS  
**Source:** https://github.com/yantrajs/yantra  
**License:** Apache-2.0

YantraJS is especially valuable because it explores a managed JavaScript compiler and IL compiler.

The architectural direction is directly relevant:

```text
JavaScript
    |
    v
managed compiler
    |
    v
.NET assembly / IL
    |
    v
CoreCLR
```

The initial strategy should not be “Jint or Yantra”.

Use:

```text
Jint       -> correctness/bootstrap
YantraJS   -> JS-to-IL/compiler architecture reference
Test262    -> conformance authority
```

---

# 8. Text and Font Shaping — SixLabors.Fonts

**Project:** SixLabors.Fonts  
**Source:** https://github.com/SixLabors/Fonts  
**Shaping docs:** https://github.com/SixLabors/docs/blob/main/articles/fonts/shaping.md/

SixLabors.Fonts is one of the strongest findings.

Its managed shaping stack covers OpenType layout and many complex script requirements. The project documentation explicitly states that shaping is implemented directly in managed code rather than through HarfBuzz or platform text APIs.

### Proposed use

```text
OutWit.OS.Text
      |
      v
ITextShaper / IFontProvider
      |
      v
SixLabors.Fonts provider
```

The public WitOS API should expose platform-neutral concepts such as `FontFace`, `FontInstance`, `TextRun`, `GlyphRun`, direction, language and shaping features.

### Browser boundary

Browser keeps CSS font semantics, `@font-face` lifecycle, web-font origin/cache rules and CSS feature selection.

Platform provides font loading, metadata, shaping, metrics, outlines and low-level line-break support.

### License warning

SixLabors uses the Six Labors Split License. Technical fit is excellent, but the current exact license and OutWit distribution model must be reviewed before making it a direct dependency.

Do not expose SixLabors types in WitOS public APIs.

---

# 9. Alternative Text References

## LayoutFarm/Typography

**Source:** https://github.com/LayoutFarm/Typography

Useful as an alternative managed OpenType implementation, font-parsing reference and fallback if SixLabors licensing is unsuitable.

A file-level license review is required before source reuse.

## ICU4N

**Source:** https://github.com/NightOwl888/ICU4N

Useful for Unicode/globalization algorithms and behavior, but should not be assumed to be a complete current ICU replacement without feature verification.

---

# 10. Image Stack — ImageSharp

**Project:** SixLabors.ImageSharp  
**Source:** https://github.com/SixLabors/ImageSharp

ImageSharp provides a mature managed image processing and codec ecosystem.

It is an obvious candidate for an initial `OutWit.OS.Images` provider.

### Proposed API boundary

```text
IImageDecoderProvider
IImageEncoderProvider
IImageResource
ImageMetadata
PixelFormat
```

Then `ImageSharpImageProvider` is only one implementation.

### Security

Image decoding processes attacker-controlled binary data. Even managed decoders should be isolatable.

The same API should permit:

```text
in-process decoder
isolated decoder process/service
WASM-sandboxed decoder
```

### License

Same SixLabors Split License issue as SixLabors.Fonts.

---

# 11. 2D Drawing — ImageSharp.Drawing

**Project:** SixLabors.ImageSharp.Drawing  
**Source:** https://github.com/SixLabors/ImageSharp.Drawing

The current design records drawing operations through `DrawingCanvas` and can replay them into CPU images, retained scenes and WebGPU-backed targets.

That makes it highly relevant to the proposed WitOS rendering direction:

```text
drawing commands
      |
      v
display/scene representation
      |
      +--> CPU renderer
      +--> GPU renderer
      +--> remote/offscreen target
```

### Proposed role

Use as:

```text
BOOTSTRAP software renderer
+
reference for retained drawing API
+
reference for CPU/WebGPU backend convergence
```

Do not make `DrawingCanvas` itself the WitOS public graphics contract without a separate API design review.

---

# 12. GPU API — WebGPU / webgpu.h

**Project:** webgpu.h  
**Source:** https://github.com/webgpu-native/webgpu-headers  
**License:** BSD-3-Clause

The project explicitly describes WebGPU as an ergonomic, efficient, portable GPU API whose concepts are mostly not web-specific.

A WitOS high-level GPU API should therefore seriously consider semantic alignment with:

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
RenderPass
ComputePass
```

If WitOS uses similar semantics, browser WebGPU can become a relatively thin binding instead of another large translation layer.

---

# 13. GPU Architecture Reference — Dawn

**Project:** Dawn  
**Source:** https://dawn.googlesource.com/dawn/

Dawn is Chromium's WebGPU implementation and provides several valuable architectural patterns:

- validation layer;
- multiple hardware backends;
- frontend/backend separation;
- a client/server “wire” implementation for sandboxed clients without direct driver access;
- generated API machinery;
- WGSL tooling through Tint.

Possible WitOS structure:

```text
Application
    |
    v
WebGPU-like API
    |
    v
validation
    |
    v
optional IPC/wire
    |
    v
GPU service
    |
    v
driver
```

Dawn is a reference implementation, not a managed dependency candidate.

---

# 14. .NET GPU Reference — Veldrid

**Project:** Veldrid  
**Source:** https://github.com/veldrid/veldrid

Veldrid provides a .NET cross-platform graphics abstraction over D3D11, Vulkan, Metal, OpenGL and OpenGL ES.

It is valuable for studying C# API ergonomics, resource lifetime, command submission and backend separation.

The public project states that updates have not been publicly shared since 2023.

Therefore:

```text
REFERENCE
not foundation
```

---

# 15. Audio — NAudio.Core

**Project:** NAudio  
**Source:** https://github.com/naudio/NAudio

NAudio 3 separates a cross-platform core from platform-specific output/capture backends.

The cross-platform parts include wave/sample providers, mixing, resampling, DSP, effects, sequencing and file I/O.

### Proposed architecture

```text
OutWit.OS.Audio
      |
      +--> sample/DSP pipeline
      |        |
      |        v
      |    NAudio.Core initially
      |
      +--> device provider
               |
               v
          WitOS audio hardware/service
```

The device layer remains WitOS-specific.

---

# 16. Managed Audio Codecs

## Concentus — Opus

**Source:** https://github.com/lostromb/concentus

Strong candidate for managed Opus encode/decode. Benchmark against modern native/hardware implementations.

## NLayer — MP3

**Source:** https://github.com/naudio/NLayer  
**License:** MIT

Fully managed MPEG Layer 1/2/3 decoder.

## NVorbis — Vorbis

**Source:** https://github.com/NVorbis/NVorbis

Managed Vorbis decoder suitable for reuse or bootstrap.

---

# 17. Video — Major Remaining Gap

No equally mature pure-managed ecosystem was found for browser-critical modern video.

Important native references include:

```text
dav1d       -> AV1
OpenH264    -> H.264
libvpx      -> VP8/VP9
```

These are useful algorithmic and conformance references but do not satisfy the pure-managed browser target directly.

### WitOS strategy

Define the provider API before choosing implementations:

```text
IVideoDecoder
IVideoEncoder
VideoCodecDescriptor
DecodedVideoFrame
EncodedPacket
VideoDecoderRequirements
```

Possible provider types:

```text
Managed decoder
Hardware decoder
WASM decoder
Remote decoder
```

### Important experiment: codec through WASM

```text
existing C/Rust codec
        |
        v
      WASM
        |
        v
WitOS WASM Runtime
        |
        v
IVideoDecoder provider
```

Measure SIMD, WASM threads, frame-copy overhead, 1080p/4K throughput, compilation/startup and interoperability with video/GPU surfaces.

This is architecturally important even if it does not become the final path for every codec.

---

# 18. WebRTC

## SIPSorcery

**Source:** https://github.com/sipsorcery-org/sipsorcery

Technically valuable for SIP/RTP/ICE/STUN/SDP/WebRTC architecture. Current license terms must be reviewed carefully before code reuse.

## Pion

**Source:** https://github.com/pion/webrtc

Go rather than .NET, but an excellent independent protocol reference.

Likely split:

```text
Browser owns:
    RTCPeerConnection semantics
    JS bindings
    origin permission semantics

Reusable lower layer:
    ICE
    STUN/TURN
    DTLS/SRTP
    RTP/RTCP
    jitter buffers
    capture integration
```

---

# 19. WebAuthn / FIDO2 — fido2-net-lib

**Project:** fido2-net-lib  
**Source:** https://github.com/passwordless-lib/fido2-net-lib

The project provides mature .NET FIDO2/WebAuthn relying-party semantics including attestation and assertion validation and broad authenticator/extension support.

### What it solves

```text
WebAuthn protocol semantics
credential validation
attestation/assertion processing
```

### What WitOS still needs

```text
Authenticator Service
    +-- TPM / secure hardware
    +-- security key / CTAP
    +-- phone-based authenticator
    +-- secure software vault
```

The project is a strong upper-layer candidate, not the entire credential subsystem.

---

# 20. Permission Broker Reference — XDG Desktop Portals

**Documentation:** https://flatpak.github.io/xdg-desktop-portal/

The portal model is highly relevant for sandboxed applications requesting user-mediated access.

Useful interaction pattern:

```text
sandboxed application
       |
       v
asynchronous request
       |
       v
trusted broker
       |
       v
trusted UI
       |
       v
selected resource
       |
       v
scoped access
```

The FileChooser portal is particularly relevant to capability-backed file access.

Reuse the interaction model, not D-Bus itself.

---

# 21. Capability Architecture Reference — Fuchsia

**Documentation:** https://fuchsia.dev/

Fuchsia is one of the strongest external references for WitOS security.

Its component framework explicitly follows a no-ambient-authority model and routes capabilities to components.

This aligns strongly with WitOS principles:

> Identity is not authority.

> Resource identity is not authority.

> Possession of a valid capability is authority.

Study capability routing, delegation, component namespaces, environments, optional availability, least privilege and handle-based access.

Fuchsia is a reference, not an API template.

---

# 22. Accessibility — AccessKit

**Project:** AccessKit  
**Source:** https://github.com/AccessKit/accesskit  
**License:** MIT / Apache-2.0

AccessKit is an excellent reference for the exact problem WitOS must solve.

Its model contains:

```text
Tree
Node
Role
Properties
Action
TreeUpdate
Subtrees
```

The project states that its schema is based largely on Chromium's cross-platform accessibility abstraction and uses pushed incremental tree updates.

### Proposed WitOS direction

Create a small managed schema inspired by AccessKit, Chromium AX and relevant ARIA semantics:

```text
AccessibilityTree
AccessibilityNode
AccessibilityRole
AccessibilityAction
AccessibilityTreeUpdate
AccessibilitySubtreeId
```

Then:

```text
Browser ----+
Avalonia ---+--> WitOS Accessibility Tree --> assistive service
Terminal ---+
```

Do not port AccessKit blindly. The primary reusable asset is its architecture.

---

# 23. Printing — SharpIppNext

**Project:** SharpIppNext  
**Source:** https://github.com/danielklecha/SharpIppNext  
**License:** MIT

SharpIppNext provides IPP client/server functionality, IPP/1.1 operations, CUPS printer discovery and a NativeAOT sample.

It is a strong direct reuse candidate.

### Proposed boundary

```text
OutWit.OS.Printing
      |
      +-- printer discovery
      +-- jobs
      +-- policy
      +-- capability model
      +-- user interaction
              |
              v
       SharpIppNext provider
              |
              v
          IPP printer
```

Browser retains CSS print layout and web print-preview semantics.

---

# 24. PDF — PdfPig

**Project:** PdfPig  
**Source:** https://github.com/UglyToad/PdfPig  
**License:** Apache-2.0

PdfPig is a managed .NET PDF library with a useful object/parser model, text positions, images, annotations, forms, metadata, hyperlinks, encrypted-document support and basic creation.

It is a strong candidate for reusable PDF parsing/object-model functionality.

It should not automatically be treated as a complete modern high-fidelity PDF renderer.

---

# 25. PDF Rendering Reference — PDF.js

**Project:** PDF.js  
**Source:** https://github.com/mozilla/pdf.js

Potential roles:

1. rendering oracle;
2. compatibility/reference implementation;
3. temporary bootstrap viewer through the system JavaScript runtime;
4. source of PDF edge-case behavior.

A possible early path is:

```text
PDF.js
   |
   v
system JavaScript runtime
   |
   v
WitOS Graphics
```

This need not be the final architecture.

---

# 26. Cryptography

## Standard .NET first

Use:

```text
System.Security.Cryptography
SslStream
X509Certificate2
RandomNumberGenerator
```

where appropriate.

WitOS should support these APIs rather than create branded replacements.

## BouncyCastle.NET

**Source:** https://github.com/bcgit/bc-csharp

BouncyCastle provides a large managed cryptographic/protocol surface including CMS, OpenPGP, TLS/DTLS, X.509 and additional algorithms.

Use it as an extended algorithm/protocol provider and reference implementation, not as a reason to bypass BCL crypto.

---

# 27. Compression

## Standard .NET first

Use BCL for Deflate, GZip, Brotli and other supported formats.

## ZstdSharp

**Project:** ZstdSharp  
**Source:** https://github.com/oleg-st/ZstdSharp  
**License:** MIT

Managed zstd port with streaming support and safe wrappers.

Prefer ordinary library use unless a true system-service abstraction is needed.

---

# 28. MIME — MimeKit

**Project:** MimeKit  
**Source:** https://github.com/jstedfast/MimeKit  
**License:** MIT  
**Organization:** .NET Foundation project

MimeKit is a mature managed MIME parser/creator.

It is probably too email-specific to become a foundational WitOS service, but is useful for parsing behavior, multipart handling, content types and related standards.

Use it as an ordinary library or reuse narrow algorithms behind platform-neutral APIs where justified.

---

# 29. Diagnostics — Standard .NET

Do not create a parallel WitOS tracing API.

Use:

```text
System.Diagnostics.Activity
ActivitySource
Meter
EventSource
EventPipe
```

WitOS work should focus on CoreCLR/EventPipe support, collection, visualization and correlation with WitOS process/resource identities.

---

# 30. Storage — WitDatabase

Browser storage semantics should remain in the browser:

```text
IndexedDB
CacheStorage
browser profile state
       |
       v
browser storage implementation
       |
       v
transactional storage primitives
       |
       v
WitDatabase / storage provider
```

IndexedDB should not become an OS API.

WitDatabase is an obvious candidate substrate for profile databases, metadata, caches and possibly transactional web storage.

---

# 31. Areas Where WitOS Should Mostly Own the Implementation

The survey does not imply “assemble the OS from NuGet packages”.

Some areas are core WitOS differentiators and should remain architecturally ours.

## Permission broker

Reference XDG Portals, Fuchsia, Android/iOS and browser permission models, but implement WitOS-specific identity/capability issuance, persistent grants, trusted UI, revocation and resource discovery.

## GPU/compositor system

Reference WebGPU, Dawn, Veldrid and browser compositors, but implement WitOS-specific integration with GPU hardware, presentation, isolation, surfaces, scheduling, remote presentation and zero-copy.

## Accessibility service

Reference AccessKit, Chromium AX, ARIA, UIA, AX, AT-SPI and Android accessibility. Implement a compact managed WitOS schema and adapters.

## Credentials/authenticator service

Reuse protocol semantics where possible, but the trusted system secret/authenticator broker remains a WitOS service.

## Modern video platform

Provider architecture must be ours even if individual codecs are reused.

---

# 32. Dependency Philosophy

WitOS should not expose third-party types directly through public contracts.

Bad:

```csharp
public SixLabors.Fonts.Font GetSystemFont(...);
```

Good:

```csharp
public IFontFace OpenFont(...);
```

with a `SixLaborsFontProvider` behind the contract.

The same rule applies to WACS, Jint, ImageSharp, NAudio, SharpIppNext and PdfPig.

This preserves the ability to replace, fork, sandbox or specialize providers.

---

# 33. Proposed First Technical Spikes

## Spike A — WebAssembly

Use WACS to run:

1. trivial core module;
2. WASI CLI application;
3. Component Model application;
4. typed WIT host binding;
5. WASM-to-IL execution.

Wrap it behind a prototype runtime interface and measure startup, throughput, allocations, compatibility and authority interception.

## Spike B — Managed Text and 2D Rendering

Use:

```text
SixLabors.Fonts
+
ImageSharp
+
ImageSharp.Drawing
```

Render Latin, Hebrew, Arabic, Indic text, emoji, variable fonts, mixed BiDi, ligatures and fallback fonts.

Output both a CPU bitmap and, if practical, a WebGPU target.

## Spike C — GPU API Mapping

Build a design table mapping:

```text
WebGPU concept
Dawn concept
proposed WitOS concept
```

for Adapter, Device, Queue, Buffer, Texture, ShaderModule, Pipeline, CommandEncoder, CommandBuffer and Surface.

Do not write a unique graphics API until this mapping has been reviewed.

## Spike D — Audio

Combine NAudio.Core, Concentus, NLayer and NVorbis with a minimal hosted audio-device provider.

Test playback, mixing, resampling, streaming and codec behavior.

## Spike E — Video Through WASM

Compile one open codec implementation to WASM and expose it as `IVideoDecoder`.

Measure 1080p/4K throughput, SIMD, threads and copy overhead.

## Spike F — Permission Broker

Prototype:

```text
Application
    |
    v
CapabilityRequest<Camera>
    |
    v
trusted broker
    |
    v
trusted presentation prompt
    |
    v
scoped capability
```

Use XDG Portal request semantics and Fuchsia capability principles as references.

---

# 34. Recommended Priority

### Immediate study

```text
1. WACS
2. SixLabors.Fonts
3. WebGPU / Dawn
4. Jint
5. ImageSharp.Drawing
```

These have the strongest impact on core architecture.

### Early implementation candidates

```text
WACS
Jint
NAudio.Core
Concentus
NLayer
NVorbis
SharpIppNext
PdfPig
ZstdSharp
BouncyCastle where BCL is insufficient
```

### Reference-heavy areas

```text
GPU/compositor     -> WebGPU + Dawn
permissions        -> XDG Portals + Fuchsia
accessibility      -> AccessKit + Chromium AX
video              -> mature native codecs + standards
WebRTC             -> Pion/SIPSorcery/specifications
```

---

# 35. Licensing Checklist

Before adopting any third-party component:

1. confirm repository license at the exact version/commit;
2. confirm NuGet/package license metadata;
3. inspect bundled third-party code and submodules;
4. distinguish direct dependency from source copy/fork;
5. record attribution requirements;
6. verify patent-sensitive codec implications separately;
7. verify commercial-use conditions;
8. verify whether future distribution changes obligations.

Particular attention is required for the SixLabors stack, media codecs, WebRTC code, font data and PDF/font subcomponents.

A permissive source license does not automatically resolve patent or codec distribution questions.

---

# 36. Reference Architecture Map

```text
                              Managed Browser
                                    |
       +----------------------------+----------------------------+
       |                            |                            |
       v                            v                            v
   HTML / DOM                  CSS / Layout                  Web APIs
       |                            |                            |
       +----------------------------+----------------------------+
                                    |
                         WitOS Platform Services
                                    |
       +----------+----------+------+-------+----------+------------+
       v          v          v      v       v          v            v
      JS         WASM       Text  Graphics Images     Media     Credentials
       |          |          |      |       |          |            |
      Jint       WACS     6L.Fonts WebGPU  ImageSharp NAudio      fido2
   Yantra ref.             candidate Dawn   candidate codecs      semantic
                                    |
                                    v
                         Permissions / Capabilities
                                    |
                          XDG/Fuchsia references
                                    |
                                    v
                                WitOS Core
```

---

# 37. Strategic Conclusion

The survey changes the implementation picture substantially.

The problem is not:

> Build every subsystem that Chromium contains from zero in C#.

A more realistic decomposition is:

```text
Reuse mature managed components where the semantics already exist.
Use native projects as behavioral and architecture references.
Keep WitOS-specific authority, presentation and resource APIs under our control.
Write the browser-specific Web Platform ourselves.
```

The result can look like:

```text
Existing managed ecosystem
       |
       +-- WACS
       +-- Jint
       +-- managed text shaping
       +-- image codecs
       +-- audio codecs
       +-- PDF parser
       +-- IPP
       +-- crypto/compression
       +-- .NET diagnostics
                |
                v
       stable WitOS abstractions
                |
                v
        shared platform services
                |
                v
          managed browser
```

This keeps the most important engineering effort focused on areas where WitOS can genuinely add value:

```text
capability-native security
application/resource model
portable .NET execution
WASM as a native format
GPU/presentation integration
managed Web Platform implementation
```

---

# 38. Sources and Projects

Primary sources used for this research snapshot:

- WACS — https://github.com/kelnishi/WACS
- WebAssembly Component Model — https://github.com/WebAssembly/component-model
- Jint — https://github.com/sebastienros/jint
- YantraJS — https://github.com/yantrajs/yantra
- SixLabors.Fonts — https://github.com/SixLabors/Fonts
- SixLabors shaping docs — https://github.com/SixLabors/docs/blob/main/articles/fonts/shaping.md
- SixLabors/ImageSharp — https://github.com/SixLabors/ImageSharp
- SixLabors/ImageSharp.Drawing — https://github.com/SixLabors/ImageSharp.Drawing
- Six Labors pricing/license information — https://sixlabors.com/pricing/
- webgpu.h — https://github.com/webgpu-native/webgpu-headers
- Dawn — https://dawn.googlesource.com/dawn/
- Veldrid — https://github.com/veldrid/veldrid
- NAudio — https://github.com/naudio/NAudio
- NLayer — https://github.com/naudio/NLayer
- Concentus — https://github.com/lostromb/concentus
- NVorbis — https://github.com/NVorbis/NVorbis
- fido2-net-lib — https://github.com/passwordless-lib/fido2-net-lib
- XDG Desktop Portals — https://flatpak.github.io/xdg-desktop-portal/
- Fuchsia docs — https://fuchsia.dev/
- AccessKit — https://github.com/AccessKit/accesskit
- SharpIppNext — https://github.com/danielklecha/SharpIppNext
- PdfPig — https://github.com/UglyToad/PdfPig
- PDF.js — https://github.com/mozilla/pdf.js
- BouncyCastle.NET — https://github.com/bcgit/bc-csharp
- ZstdSharp — https://github.com/oleg-st/ZstdSharp
- MimeKit — https://github.com/jstedfast/MimeKit
- .NET EventSource — https://learn.microsoft.com/dotnet/core/diagnostics/eventsource
- .NET EventPipe — https://learn.microsoft.com/dotnet/core/diagnostics/eventpipe

---

# 39. Next Revision

The next revision should add a per-candidate verification sheet with:

```text
exact version/commit
last release
last meaningful commit
NuGet package IDs
license
transitive dependencies
native dependencies
unsafe code
P/Invoke
NativeAOT support
trimming support
platform assumptions
conformance suite
benchmark notes
security history
forkability
estimated adapter effort
```

That revision can become the practical dependency-decision document used before implementation begins.
