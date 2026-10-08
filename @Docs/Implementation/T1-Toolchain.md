# T1 — clang and lld for layer 2

Plan step T1 gives the system layer its toolchain, as [ADR 0024](ADR-0024-Three-Layers-and-Unix-Form-Runtime.md) §5
decided: clang and lld producing ELF for the SysV calling convention and the Itanium C++ ABI, pinned like every other
tool, with WitOS's own sysroot. The first stage uses the Linux musl triples, `x86_64-unknown-linux-musl` and
`aarch64-unknown-linux-musl`; the `*-unknown-witos` triple arrives with step T4 once the sysroot has settled. The
first program built this way is the root task: one C source for both ISAs, compiled without a libc or a runtime and
started by the kernel from the boot disk on x64 and ARM64. The kernel's MSVC build is unchanged; step T3 moves it.

## What changed

**The pinned toolchain.** The LLVM 20.1.8 Windows installer was already pinned by SHA-256 for clang-format
(`Toolchain.LLVM_VERSION`, `LLVM_INSTALLER_SHA256`). `setup` now also extracts `clang.exe`, `ld.lld.exe`,
`llvm-objcopy.exe`, `llvm-readobj.exe` and the compiler's own headers (`lib/clang/20/include`: `stdint.h` and the
like, which freestanding code includes) into `.tools/clang-20.1.8/` with 7-Zip, never executing the installer, and
checks `clang --version`; `doctor` reports the compiler and the two triples; a build that needs the compiler requires
it (`Toolchain.RequireClang`) and names `setup` when it is missing. CI caches the installer in both kernel jobs.
`KernelArchitecture` carries the triple, the ELF machine (62, 183) and the architecture's clang options: ARM64 code is
compiled with `-ffixed-x18`, because x18 is the kernel's compiler TLS register, written on every return to EL0.

**The sysroot.** `src/Sysroot/include` holds what layer 2 adds to the kernel's ABI-1 headers, which stay the one
source of the ABI and are reached through the include path (`src/Kernel/include`). The first header is
`witos/syscall.h`: the transport of RFC 0011 §6.1 as an inline function — on x64 `SYSCALL` with the number in RAX,
the arguments in RDI, RSI and RDX, the status in RAX and the result in RDX, RCX and R11 clobbered; on ARM64 `SVC #0`
with x8 and x0–x2, the status in x0 and the result in x1, the flags clobbered. The ABI-1 headers now compile under
clang as well as MSVC: `WIT_NORETURN` chooses `__declspec(noreturn)` or `__attribute__((noreturn))` by `_MSC_VER`.
There is no libc (S1), no C++ runtime (S4) and no ELF loader (S5) yet: a layer-2 program of T1 is freestanding.

**The root task in C.** `tests/User/root.c` replaces `tests/User.X64/root.asm` and `tests/User.A64/root.asm`: the
same checks (the startup descriptor, the log, the boot package's first page and the refused writable view, the
device table, UTC's frequency, reading, setting through the clock capability and the two refusals), the same
diagnostics in the fixed data page for the kernel self-test (the failed status at 1304, the checks passed at 1312),
the same exit codes. Its first log line is `[ROOT] started by clang 20.1.8 for x86_64` or `for aarch64`, built from
the compiler's predefined macros, and the primary boot scenario of each suite requires it: the line proves which
compiler built the component that runs. The source compiles with `-std=c11 -O2 -ffreestanding -fno-builtin -nostdlib
-nostdlibinc -fPIE -fno-plt -fno-stack-protector -fno-asynchronous-unwind-tables -fno-unwind-tables -Wall -Wextra
-Werror`; on x64 the entry realigns the stack (`force_align_arg_pointer`), since the kernel enters it with no return
address pushed. `tests/User/root.ld` links it as a static `ET_EXEC` at the component's image window
(`WIT_USER_IMAGE_BASE`) with code, read-only data and writable data each starting a page, with `--build-id=none`,
`-z max-page-size=4096` and `--gc-sections`.

**ELF to flat.** `FlatImage.FromElfAsync` reads a 64-bit little-endian `ET_EXEC` of the architecture's machine and
turns its loadable segments into the flat segments the kernel validates (`witos/flat.h`): one to four `PT_LOAD`
entries at page-aligned addresses, each readable and never both writable and executable, memory sizes rounded to
pages, the entry inside an executable segment; a `PT_INTERP`, `PT_DYNAMIC` or `PT_TLS` entry is refused, as is a
file range beyond the image. The flat writer is shared with the PE path, which stays for the fixtures of the frozen
line. The kernel still knows no ELF: the flat format remains the boot format of the root task, and the ELF loader is
step S5's.

## Tools

- `tools/WitOS.Dev/Host/Toolchain.cs`: `ClangDirectory`, `Clang`, `Lld`, `RequireClang`, `PrepareClangAsync`, the
  extraction in `SetupAsync`; `Commands/CommandDoctor.cs`; `Kernel/KernelArchitecture.cs`: `Triple`, `ElfMachine`,
  `ClangOptions`, `RootStartedLine`; `Images/UserImage.cs`: `BuildRootAsync` through clang and lld;
  `Images/FlatImage.cs`: `FromElfAsync`, `ParseElf`, `Build`; `Kernel/KernelTestSuite.cs`: the required line.
- `.github/workflows/kernel.yml`: the LLVM installer cache in the ARM64 job; `setup` extracts clang in both.

## Tests

- `tests/WitOS.Dev.Tests/Images/FlatImageTests.cs`: a hand-built ELF with a code and a data segment becomes a two-segment
  flat image with the right offsets, sizes, protections and bytes; a position-independent executable, a foreign
  machine, an interpreter, a TLS segment, a writable executable segment, an unreadable segment, an unaligned segment,
  an entry outside executable code, a file size beyond the memory size, five segments, no loadable segment and a
  truncated header are refused.
- Both guest suites: `Root.Started` and `Root.ImageValidation` pass with the C root task, the release kernel starts
  it from the boot disk, and `boot-128` requires `[ROOT] started by clang 20.1.8 for x86_64` (x64) and
  `[ROOT] started by clang 20.1.8 for aarch64` (ARM64).

## Limits kept explicit

- Freestanding C only: no libc, no C++ runtime, no exceptions, no TLS, no dynamic linking; those are steps S1–S5. The
  triples are Linux's until T4; nothing of Linux is used beyond the register convention the ABI shares with it.
- The kernel, the UEFI loader and the fixtures of the frozen line keep their MSVC, MASM and armasm64 builds until T3
  and K8; the Windows host remains the build host until T2.
- The root task is linked at a fixed address and converted to the flat format; it is not an ELF the kernel loads.
