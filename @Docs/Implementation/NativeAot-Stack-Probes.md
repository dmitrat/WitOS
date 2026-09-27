# Native x64 compiler stack probes

**Status:** WitOS 0.0.40; experimental ABI v17 unchanged.

The real __chkstk dependency is implemented in Kernel.Arch.X64 and included only in the WitOS native runtime archive. Windows-reference sources and CRT remain unchanged.

## Contract

The helper follows the [Microsoft x64 prolog contract](https://learn.microsoft.com/en-us/cpp/build/prolog-and-epilog?view=msvc-170): allocation size arrives in RAX, the caller adjusts RSP after probing, and only R10, R11 and condition codes are scratch. RAX, argument and nonvolatile registers remain intact.

The implementation is a leaf and does not push registers, allocate a frame, access FS/GS/TEB data, perform syscalls or alter SIMD/x87 state. It derives the caller's pre-call stack pointer from RSP plus the return-address size. Zero-size requests return without probing. Other requests touch one byte at each descending 4 KiB step, then the final partial-span byte. It never computes and trusts only an allocation endpoint, so large sizes cannot jump over the low guard.

WitOS currently has fixed committed stacks. Crossing their first inaccessible guard page raises a user-mode page fault and terminates only the component through the existing kernel fault path. The helper does not commit new pages or implement a Windows stack-growth/TEB service. It also does not provide GC stack walking, context restoration, exception unwinding or managed StackOverflowException behavior.

The kernel's accepted unwind profile is unchanged. The helper needs no non-leaf unwind frame; the test wrappers carry ordinary structural frame metadata.

## Integration and provenance

The source overlay compiles chkstk.asm into Runtime.WorkstationGC. The source verifier requires __chkstk to resolve, verifies the exact archive object and passes that object to the guest probe. Its hash is checked before linking. No test substitute defines __chkstk.

The runtime_stack.cpp test is compiled with /Gs4096 and an actual volatile 8 KiB local array. The tool checks the compile command and verifies an undefined __chkstk reference in that C++ object, proving that the compiler emitted the helper call. The explicit ABI-test assembly is separate and lives in Kernel.Arch.X64 with all x64 instructions.

The linker map supplies the actual __chkstk code interval for kernel fault assertions at both image bases. Test code and reports remain separate from the helper ABI.

## Guest tests

Three groups extend runtime-config:

- CompilerStackProbe checks zero, partial-page, exact-page and multi-page sizes. The assembly caller verifies RAX/RSP, argument registers, every nonvolatile GPR and all sixteen XMM registers using distinct sentinels. A real compiler-generated 8 KiB frame retains all byte values across a yield; two batches of three native workers repeat it through thread reuse. Native last-error and resource accounting are preserved.
- CompilerStackProbeWithoutTls executes the register/frame checks in the otherwise identical image with no compiler-TLS directory, before native TLS/PAL initialization.
- CompilerStackProbeGuard requests an allocation whose final address would land in the mapped user report page below the stack, then separately requests UINT64_MAX. Both must fault on the first unmapped low guard, with read/nonpresent/user error bits and fault RIP inside the helper. This detects skipped guard pages and a wrapped endpoint. A fresh successful component runs after these failures.

Normal cases check exact thread creations/joins/reaps, unchanged owned pages, no handles/events left and complete physical-page recovery. Fault cases preserve the owning live stack until kernel fault capture and also require full teardown recovery.

Only runtime-config adds these two deliberate hardware faults: it now requires 202 user groups and 53 contained faults. Ordinary boots retain 178 groups and 51 faults. The runner selects the expected count explicitly; individual guard cases additionally validate the address, access flags, selectors and faulting function.

## Validation

Both 128/512 MiB runtime-config boots passed. The import-free fixture is 48,128 bytes with 118 plain unwind entries on the local compiler. Its archive has fifteen source objects plus verified runtime/CRT/minipal objects and the ABI fixture. The full WitOS archive now has 85 members; minipal retains 11. The 64-file source audit, hosted NativeAOT probe, Windows native/source references and minimal startup diagnostic passed. All 19 ordinary QEMU scenarios passed. Release solution builds completed without warnings or errors.

The broad strict-link inventory drops from 79 to 78 symbols and the minimal executable startup inventory from 73 to 72. That remaining link is still deliberately incomplete; no managed guest runtime execution is claimed.
