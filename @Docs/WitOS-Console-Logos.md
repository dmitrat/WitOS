# WitOS console logos — usage guide

Status: proposal for phase C (console and shell, RFC-0020). Applies to the serial terminal of C1 and the framebuffer text
console of C2. Graphical branding (M9) is out of scope.

This guide defines four text-mode marks for WitOS. It says when to show each one, how to colour it, where the logo
lives in the system, and how to keep it compatible with the deterministic test judges of C1.2 and C2.3.

## 1. Principles

1. **The logo is data and policy, not mechanism.** The kernel, the boot loader and the root task never print it. A
   session prints it. This follows ADR 0024: the kernel knows mechanisms only.
2. **Every variant degrades gracefully.** It must stay correct with no colour, no Unicode, a narrow terminal or no
   terminal at all.
3. **Colour carries meaning only as a bonus.** Shape alone must read as WitOS. Shading density already separates the
   layers in monochrome.
4. **Facts shown next to the logo are true.** The info panel reads values from the running system. It never hard-codes
   them.
5. **The logo never breaks automation.** It does not appear when output is redirected, in non-interactive sessions or
   in scripted test runs, unless a test asks for it.

## 2. The marks

### 2.1 Emblem: the layered W (primary)

The W is cut into three tiers that mirror the architecture of ADR 0024. The top tier is upstream .NET (layer 3). The
middle tier is the system layer (layer 2). The lower tier is the nano-kernel (layer 1). The solid base is the UHI: the
hardware personality that may one day move into firmware or silicon.

```
▓▓▓▓          ▓▓▓▓          ▓▓▓▓
 ▓▓▓▓        ▓▓▓▓▓▓        ▓▓▓▓
  ▒▒▒▒      ▒▒▒▒▒▒▒▒      ▒▒▒▒
   ▒▒▒▒    ▒▒▒▒  ▒▒▒▒    ▒▒▒▒
    ░░░░  ░░░░    ░░░░  ░░░░
     ░░░░░░░░      ░░░░░░░░
      ██████        ██████
```

| Property | Value |
| --- | --- |
| Size | 32 columns × 7 rows |
| Characters | U+2588 `█`, U+2593 `▓`, U+2592 `▒`, U+2591 `░`, space |
| Row → layer | rows 1–2: layer 3 (.NET); rows 3–4: layer 2; rows 5–6: layer 1; row 7: UHI |
| Use | session banner with the info panel (§5), `witfetch`, about screens |

### 2.2 Wordmark (secondary)

Use the wordmark when the name matters more than the symbol, for example in a welcome banner or a recovery-console
header on a wide terminal. The lowercase `i` and `t` keep the reading "WitOS", not "WITOS".

```
██╗    ██╗ ██╗   ██╗      ██████╗ ███████╗
██║    ██║ ╚═╝ ██████╗   ██╔═══██╗██╔════╝
██║ █╗ ██║ ██╗ ╚═██╔═╝   ██║   ██║███████╗
██║███╗██║ ██║   ██║     ██║   ██║╚════██║
╚███╔███╔╝ ██║   █████╗  ╚██████╔╝███████║
 ╚══╝╚══╝  ╚═╝   ╚════╝   ╚═════╝ ╚══════╝
```

| Property | Value |
| --- | --- |
| Size | 42 columns × 6 rows |
| Characters | U+2588 `█` and box-drawing U+2550–U+255D (`═ ║ ╔ ╗ ╚ ╝`) |
| Use | welcome banner; never together with the emblem in the same banner |

### 2.3 ASCII fallback

Pure 7-bit ASCII for `TERM=dumb`, terminals without UTF-8, logs and any place where encoding is uncertain.

```
 __      __ _  _      ___   ____
 \ \    / /(_)| |_   / _ \ / ___|
  \ \/\/ / | ||  _| | (_) |\___ \
   \_/\_/  |_| \__|  \___/ |____/
```

| Property | Value |
| --- | --- |
| Size | 33 columns × 4 rows |
| Characters | ASCII 0x20–0x7E only |

### 2.4 One-line mark

For terminals narrower than the emblem or the wordmark, status lines and compact headers.

```
▓▒░ WitOS ░▒▓  nano · x86_64 · .NET 10.0.12
```

The part after the name is optional and follows the info-panel rules (§5). The ASCII form is `== WitOS ==`.

## 3. Colour

Colour is optional. Use it only when the terminal supports it and the user has not opted out (§4).

### 3.1 Palette

| Role | 24-bit | 256-colour | 16-colour fallback |
| --- | --- | --- | --- |
| Layer 3 — .NET | `#512BD4` (on dark backgrounds `#8B6CEF`) | 56 (dark backgrounds: 98) | magenta (35) |
| Layer 2 — system layer | `#00AFAF` | 37 | cyan (36) |
| Layer 1 — nano-kernel | `#FFAF00` | 214 | yellow (33) |
| UHI base | `#8A8A8A` | 245 | bright black (90) |
| Panel labels | layer 2 colour | 37 | cyan (36) |
| Panel values | default foreground | — | — |

The layer 3 colour follows the .NET brand purple. The dark-background tint keeps it legible on black. Amber on a
white background has weak contrast, so check every colour on both a dark and a light theme.

### 3.2 Coloured emblem

In colour, draw every tier with `█` and let colour separate the layers. The shading characters of §2.1 are the
monochrome form.

- 24-bit: `ESC[38;2;R;G;Bm`
- 256-colour: `ESC[38;5;Nm`
- 16-colour: `ESC[3Nm` / `ESC[9Nm`
- Reset at the end of **every** line with `ESC[0m`, so that a cut-off or wrapped line never leaks colour into the
  prompt.

### 3.3 Detecting the colour depth

| Condition | Depth |
| --- | --- |
| `NO_COLOR` is set (any value) | none |
| `TERM=dumb` or output redirected | none |
| `COLORTERM` is `truecolor` or `24bit` | 24-bit |
| `TERM` ends with `-256color` | 256 |
| otherwise | 16 |

WitOS sets `TERM=xterm-256color` in `/etc/environment` (C1.1), so the serial terminal gets 256 colours by default. The
framebuffer console of C2 decides its own depth and advertises it the same way.

### 3.4 Gradient wordmark

The wordmark may use a vertical gradient: row 1 in the layer 3 colour, row 6 in the layer 1 colour, and the layer 2
colour in between. Do not use horizontal gradients or per-character rainbow effects: they break on 16-colour terminals
and do not echo the layers.

## 4. Choosing a variant

Evaluate the rules top to bottom and take the first match.

| # | Condition | Show |
| --- | --- | --- |
| 1 | Output redirected (`Console.IsOutputRedirected`), non-interactive session, or `WITOS_NO_LOGO=1` | nothing |
| 2 | Encoding is not UTF-8, or `TERM=dumb` | ASCII fallback (§2.3), no colour |
| 3 | Width < 33 columns | one-line mark (§2.4), ASCII form if rule 2 applies |
| 4 | Width < 70 columns | emblem alone, or the one-line mark |
| 5 | Width ≥ 70 columns | emblem with the info panel (§5) |

The wordmark replaces the emblem where a product name is wanted rather than a system summary, for example in a
recovery-console header. Pick one per context and keep it stable; do not alternate at random.

Measure the width with `Console.WindowWidth` once, at the start of rendering. On the serial terminal it comes from the
terminal service's `SIZE` (N4.3); on C2 it comes from the framebuffer geometry.

## 5. The info panel

The panel sits to the right of the emblem, starting at column 36 (four columns of gap), top-aligned.

```
▓▓▓▓          ▓▓▓▓          ▓▓▓▓    witos
 ▓▓▓▓        ▓▓▓▓▓▓        ▓▓▓▓     ────────────────────────────
  ▒▒▒▒      ▒▒▒▒▒▒▒▒      ▒▒▒▒      OS       WitOS · ABI-1 1.2 · ABI-2 1.0
   ▒▒▒▒    ▒▒▒▒  ▒▒▒▒    ▒▒▒▒       Kernel   nano · x86_64 · QEMU q35
    ░░░░  ░░░░    ░░░░  ░░░░        Runtime  .NET 10.0.12, 8.0.31
     ░░░░░░░░      ░░░░░░░░         Shell    PowerShell 7.6
      ██████        ██████          Services 6 · Volumes ext4, fat, tmp
```

The values above are an example. A real panel reads them at run time.

| Field | Source |
| --- | --- |
| Title | host name, or `witos` |
| OS | ABI versions reported by the system layer |
| Kernel | ISA from `RuntimeInformation.OSArchitecture`; board from the device table |
| Runtime | installed frameworks under `/dotnet/shared/Microsoft.NETCore.App` |
| Shell | the running shell's own version (`$PSVersionTable.PSVersion` in pwsh) |
| Services | the process manager's list of running services; mounted volumes from the namespace service |

Rules:

- Seven lines at most, so the panel never grows taller than the emblem.
- A field that cannot be read is **omitted**, not shown as `unknown` and not filled from a constant.
- No secrets, no user data, no network addresses before M7 defines them.
- Values are truncated with `…` to fit the width; the emblem is never truncated or reflowed.
- Labels are padded to one column (9 characters in the example); separators are U+00B7 `·` (ASCII form: `-`).

## 6. Where the logo lives

The logo must not move policy into the layers that ADR 0024 keeps free of it.

| Layer | Role |
| --- | --- |
| Boot loader, kernel (layer 1) | never prints a logo; boot output stays diagnostic |
| Root task, services (layer 2) | never prints a logo; the serial recovery console stays plain |
| Session (layer 3) | renders the logo for an interactive session |

Proposed layout in the image:

```
/etc/witos/logo/emblem.txt      monochrome emblem (§2.1)
/etc/witos/logo/wordmark.txt    wordmark (§2.2)
/etc/witos/logo/ascii.txt       ASCII fallback (§2.3)
/etc/witos/logo/palette.json    colours by role (§3.1)
```

The art files store plain text only. Colour is applied by the renderer from `palette.json`, by row: the row → layer
mapping of §2.1 is part of the palette file, not hard-coded in the renderer.

Two renderers are expected:

1. **PowerShell profile.** Start pwsh with `-NoLogo` to suppress its own banner. Render the WitOS logo from
   `$PSHOME/profile.ps1` (all users, all hosts). PowerShell stays an ordinary replaceable application (RFC-0020 §59):
   an image without pwsh boots exactly as before, and another shell can render the same files.
2. **`witfetch`.** A small .NET tool that prints the emblem with the info panel on demand. It is the reference renderer;
   the profile may simply call it.

## 7. Testing

The judges of C1.2 (VT emulator state) and C2.3 (frame checksum) compare screens exactly. The logo must not make them
flaky.

- **Deterministic by default in tests.** The runner sets `WITOS_NO_LOGO=1` for scenarios that do not test the logo.
- **One logo scenario.** A dedicated scenario renders the logo with a fixed width and a fixed colour depth and checks
  the screen state. The info panel in that scenario shows only fields that are constant for a given image (ABI
  versions, ISA, runtime versions). Uptime, time of day, memory use and counters are excluded.
- **Both ISAs.** The scenario passes on x64 and ARM64 (and RISC-V from V3). Only the ISA field may differ.
- **Expectations.** Expected markers go to `tests/Expectations/*.json` like any other guest output.
- **Prompt detection.** C1.2 types input once the prompt is visible on screen. The logo must end with a reset
  (`ESC[0m`) and a newline, so it can never be mistaken for the prompt.

## 8. Font requirements for the framebuffer console (C2)

The bitmap font pinned in C2.2 must cover:

| Range | Needed for |
| --- | --- |
| U+0020–U+007E | ASCII fallback, panel text |
| U+00B7 | panel separator |
| U+2500–U+257F (Box Drawing) | wordmark, panel rule |
| U+2580–U+259F (Block Elements) | emblem, one-line mark |
| U+2026 | truncation ellipsis |

Add this coverage to the font selection criteria of C2.2, next to the licence check. All these characters must be
exactly one cell wide. A font that renders block elements with gaps between rows makes the emblem look striped; prefer
a font whose block glyphs fill the full cell height.

## 9. Do and don't

**Do**

- Keep the art files byte-identical across images and ISAs.
- Reset colour at the end of every line.
- Respect `NO_COLOR`, `TERM=dumb` and redirected output.
- Read every panel value from the running system.

**Don't**

- Don't print the logo from the kernel, the boot loader, the root task or a service.
- Don't animate the logo or delay the prompt to show it.
- Don't stretch, mirror, recolour per character or change the proportions of the marks.
- Don't put the emblem and the wordmark in the same banner.
- Don't write the logo to stderr or into logs.
- Don't show the logo on the serial recovery console when a framebuffer console is the primary terminal (C2): recovery
  output stays plain.

## 10. Open questions

1. Should the art files live in `/etc/witos/logo/` (editable by the administrator) or in a read-only system location
   with `/etc` overrides?
2. Does the recovery console get the ASCII header, or nothing at all?
3. Should `witfetch` ship in the base image or as an optional package (RFC-0010)?
4. Does the C2 console advertise `COLORTERM=truecolor`, or stay at 256 colours like the serial terminal?
