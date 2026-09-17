# ADR 0006: Windows x64 code generation with a WitOS runtime adapter

**Status:** Direction accepted; first memory-interface slice implemented in WitOS 0.0.10.
**Date:** 2026-09-17.
**Scope:** A native C++ implementation of the pinned upstream GC OS interface runs inside WitOS. The collector and managed runtime do not run there yet.

## Context and decision

Keep the upstream NativeAOT Windows x64 code-generation path, Microsoft x64 calling convention and PE/COFF format. Replace platform behavior explicitly in a user-space WitOS adapter. The initial profile remains baseline x86-64, workstation/non-concurrent GC, invariant globalization and static deployment.

This decision selects the source-port direction, not an existing supported `witos-x64` RID. The published Windows runtime libraries still require Windows. We do not link them into the guest or promise a general Win32 personality. The current source overlay is only `src/Runtime.NativeAot/gcenv.witos.cpp`, implementing selected declarations from the exact upstream headers. It does not patch or compile the entire Windows GC environment, GC collector or NativeAOT runtime. Full CMake/PAL/CoreLib integration remains work to do.

The source basis is .NET 10.0.8, runtime commit `b82454cad0aaaae3db2cf18fbf2cccc36e201ccc`. Compare the pinned [runtime build selection](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/CMakeLists.txt), [Windows PAL](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/windows/PalMinWin.cpp) and [Unix PAL](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/unix/PalUnix.cpp).

| Option | Consequence | Decision |
| --- | --- | --- |
| Windows x64 code generation, explicit WitOS source adapter | Reuses tested ABI/PE tools; still requires replacing Windows TLS, PAL, CoreLib and fault/unwind assumptions | Selected |
| Unix x64 source path | Requires a SysV/ELF path plus pthread, signal/context, TLS and unwind adaptation | Deferred; no demonstrated reduction in port work for this repository |
| Broad Windows compatibility layer | Would expand the kernel/system surface to satisfy OS/CRT APIs used by existing binaries | Rejected for this milestone |
| Substitute managed compiler or collector | Would weaken the upstream .NET objective | Rejected |

Hardware instructions stay in `Kernel.Arch.X64`; the C++ adapter uses the existing language-neutral user ABI. No GC concepts were added to kernel syscalls.

## Implemented interface

The compiler consumes the actual [gcenv.os.h](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/gc/env/gcenv.os.h), its base/structs headers and two minipal headers. All five headers and the upstream MIT license are copied from a SHA-256-verified cache into the build directory. The entire source audit now contains 31 source/license files. The upstream headers are unchanged; Windows SDK types are compile-time declarations only.

| Method | WitOS behavior |
| --- | --- |
| `VirtualReserve` | Round nonzero size upward to 4 KiB, reject overflow; default/minimum alignment 64 KiB, honor larger power-of-two alignment within the kernel arena; no backing RAM consumed |
| `VirtualCommit` | Page-aligned nonzero address, size rounded upward with overflow checks; commit RW/NX pages, preserve existing data/rights, rollback additions on failure |
| `VirtualDecommit` | Same range checks; discard backing pages and empty private tables, retain reservation; recommit zero-fills |
| `VirtualRelease` | Exact reservation base; ignore size as the upstream Windows implementation does; kernel owns the extent |
| `SupportsWriteWatch` | False; reserve rejects write-watch and unknown flags |

NUMA node 0 and `NUMA_NODE_UNDEFINED` use the single-node allocator; other nodes fail. Unaligned commit/decommit addresses are rejected rather than silently extending the affected range. Failure is `nullptr`/`false`. Kernel prototype quotas remain in force.

All other `GCToOSInterface` methods remain undefined. In particular, reset/large-page support, initialization, locks, events and thread creation are not successful placeholders. A negative link rooted at `GCToOSInterface::Initialize` must fail with exactly that missing symbol, without `/FORCE` or runtime/OS/CRT libraries.

## Build and guest evidence

```powershell
dotnet build WitOS.slnx --configuration Release
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-port
dotnet run --project tools/WitOS.Dev --configuration Release -- test
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-audit
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-probe
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-target
```

`runtime-port` builds the normal kernel and fixtures in `artifacts/x64/runtime-port/`, then requires a successful 256 MiB QEMU boot with all expected markers and exit status. Normal `run`, `build` and `test` also build the adapter; successful boots require its checks. The last two runtime commands still execute hosted Windows reference workloads.

The local MSVC 14.51 / SDK 10.0.26100 build produces a 5,120-byte native PE with seven ordinary unwind records, a real relocation and no OS/CRT imports, managed header or compiler TLS directory. The build report records source hashes, compiler/SDK identity and image hash; identical bytes across different compiler versions are not promised. CI builds with its installed MSVC/SDK.

Six new guest groups cover:

- Real upstream method declarations calling reserve/commit/decommit/release, rounding, alignment and rejected options/ranges.
- Sparse 16 MiB reservation, atomic quota failure with existing data retained, zero-fill, reservation exhaustion/reuse and complete backing/table reclamation.
- Execution of the same C++ image relocated to two different guest bases.
- Actual page faults on a reserved page, a decommitted page, a page rolled back after commit exhaustion, and attempted execution from committed data.

The supervisor checks fault vector/error/address/selectors and resource counts. Reservation and decommit fault cases must leave the owned-frame count unchanged. Every component teardown restores the physical-page count. The full suite has 17 VM scenarios, 89 user check groups and 33 contained user faults in each successful boot. Local `runtime-port`, full VM suite, source audit and both hosted probes passed.

Reports: `gc-memory-build.json`, `gc-missing-link.log`, `GcMemoryFixture.pe` and `.map` under the relevant image directory; serial/outcome logs under `artifacts/logs/`. Kernel CI uploads the new evidence alongside its boot artifacts.

## Remaining boundary

This proves five native adapter methods against the upstream declaration, not that upstream collector algorithms execute in the guest. The full runtime source build, native/managed TLS, thread attachment, synchronization, memory discovery, module registration, fault/unwind integration and GC rendezvous remain incomplete. The 128-frame quota, 256 KiB image limit and 128-entry plain-unwind limit cannot accommodate the measured hosted runtime. Preserve strict link failures while extending the source adapter, then raise limits with tests driven by the real runtime profile. M3 acceptance remains unchanged.
