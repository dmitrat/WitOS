# V3 — three ISAs

Plan step V3 (phase V, Goal 3 of the Implementation Strategy, N4.4): the same applications, published once by the stock
SDK on Windows without a RID, run unchanged on WitOS on the third ISA as on the other two, and from this step a step
closes only when its guest acceptance passes on x64, ARM64 and riscv64. Made on 2026-10-18.

## 1. What already ran on riscv64

V2.4 brought the whole of `runtime-witos` to riscv64, and C2.7 gave CI's `linux-host` job its riscv64 leg, so every
scenario of the runtime runs there on every change: CoreCLR and NativeAOT under the M3 acceptance, the PAL suite, the
CoreCLR and library tests, the services of ADR 0025, the terminal, PowerShell on the screen, the .NET compatibility
suite (N1) and the representative NuGet libraries (N2.1), and since C4.2 and C4.3 the file manager and the installer.
N1's application is published there by the stock SDK on the Linux host and its record,
`artifacts/runtime-witos/compatibility-riscv64.json`, says how each of its checks ended.

## 2. The applications published on Windows

V3 adds what N4.4 proved on x64 and ARM64: the bytes another host published run unchanged. `publish-apps` published the
compatibility suite and R6's portable application on Windows (Windows 11, SDK 10.0.300: 11 files, 494,483 bytes, with
`published.json`'s SHA-256 of each), and `runtime-published --arch riscv64 --published <directory>` ran exactly those
bytes in the guest on the device services, the .NET install and the terminal:

- the suite: `[N1] .NET compatibility on witos (RiscV64): 89 checks, 2 recorded differences, 0 unexpected, 0 not
  applicable`, and every check ended as the record of the suite published on Linux says
  (`compatibility-published-riscv64.json`: 89 checks, no difference);
- the portable application: `[R6] A portable application on WitOS 0.1 ABI-1 (witos-riscv64, .NET 10.0.12-dev): 12 of
  12 checks passed`.

The two recorded differences are the platform's identity (`Platform.KnownOperatingSystem`) and the file watcher
(`FileIO.Watcher`), the same as on x64 and ARM64; `expectations.json` holds one record for WitOS on every ISA.

CI's `published-on-windows` job runs the upload of `windows-publish` on the three ISAs, each on the install and the
record its `linux-host` leg uploads.

## 3. The matrix and the rule

- The compatibility matrix names `QEMU RISC-V (virt)` Primary and its WitOS column `WitOS (x64, ARM64, riscv64)`; a fact
  beyond the suite holds on the three ISAs unless it names fewer.
- AGENTS.md's rule: a step is complete only when its guest acceptance passes on x64, ARM64 and riscv64. A rule written
  as "both ISAs" before V3 holds on riscv64 as well.

## 4. What stays on two ISAs

- **The NuGet libraries' own suites** (N2.2): they run for hours a group, and CI's `nuget-suites` job runs them on x64 and
  ARM64; on riscv64 they are not run yet.
- **.NET 8 beside .NET 10** (N3): `release/8.0` is not built for riscv64, whose upstream port came after it; the
  side-by-side job stays on x64 and ARM64.

The matrix and AGENTS.md name both exceptions.

## 5. What ran

In the Linux host's image: `runtime-published --arch riscv64` on the bytes `publish-apps` published on this Windows host,
PASS in 198 s. Host tests: `RuntimePublishedTests` with the job's three ISAs, the matrix current.
