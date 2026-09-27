# Minimal executable startup readiness

**Status:** WitOS 0.0.37. This is hosted execution and a strict link diagnostic, not guest managed execution.

**Update (0.0.38):** Porting [minipal time](NativeAot-Minipal-Time.md) removes four native Windows time imports, leaving 79 minimal-startup dependencies (85 in the broad inventory). Historical measurements below describe the initial 0.0.37 assessment.

## Purpose

The existing NativeAotTarget workload exercises a shared library with allocation, exceptions and native threads. Its broad source-link inventory deliberately omits existing WitOS transport/TLS definitions. This assessment adds an ordinary executable whose normal startup roots come from the real upstream bootstrapper.obj and wmain, instead of additional /include roots for individual runtime functions.

NativeAotBoot uses the standard net10.0 SDK, unchanged upstream CoreLib and locked .NET 10.0.8 compiler/runtime packages. Main checks a static object, a local object and an array after GC.Collect. It returns 42 on success, and uses no application console/files/network/tasks or explicit worker threads. The runtime may still need its own threads and platform services.

## Reproducible path

Run `dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-readiness`. This performs the existing full-source reference/overlay verification and then the new assessment. runtime-source and runtime-config also include it; CI captures artifacts/runtime-readiness.

The tool:

1. Publishes the locked normal executable in its own native intermediate/output directories and checks the actual ILC arguments for standard CoreLib, x64 baseline and executable mode.
2. Executes the Windows binary and requires exit 42, then inspects its PE, TLS, unwind and import metadata. This is explicitly hosted evidence.
3. Captures the actual NativeLibrary inputs selected by the SDK and substitutes the corresponding files from the just-built WitOS source SDK. It requires executable bootstrapper.obj, not bootstrapperdll.obj, and the workstation GC.
4. Compiles the existing x64 syscall/lock/fail-fast assembly in transport-only mode, excluding the ordinary WitOS fixture entrypoint. Default guest builds retain that entrypoint. The exit-call number is generated from the shared ABI header.
5. Compiles real single-module TLS metadata and attempts a native-subsystem link rooted at wmain with /nodefaultlib. No Windows import libraries, forced linking, substitute CoreLib or fake runtime implementations are supplied.
6. Requires the expected unresolved-symbol failure, no produced executable, and no unexpected linker diagnostics. The known transport, TLS index, wmain and actual runtime/module startup symbols must resolve; PalAttachThread must still be reported until genuinely implemented. An unexpectedly successful link stops for review rather than claiming a runnable guest.

The selected wmain is a link-dependency root, **not a valid WitOS kernel handoff thunk**. A future guest driver still must publish the immutable image/environment, initialize native process/compiler TLS state, run necessary C++ initializers and perform orderly shutdown around actual upstream bootstrap. No attempt is made to boot this incomplete link.

Reports include exact input hashes, compiler arguments, package provenance, captured libraries, all unresolved symbols and the actual kernel-limit constants. Generated executables/objects/logs remain ignored.

## Measured result

On the local compiler, the minimal Windows executable passes static initialization, allocation and collection. Its mapped image is **954,368 bytes (932 KiB / 233 pages)** with **2,593 unwind entries**. WitOS currently allows **262,144 image bytes**, **128 total owned pages per component** and **128 plain unwind entries**. The Windows measurements are comparison evidence, not a requirement to copy its layout/imports into WitOS or a final guest budget. Handler/chained unwind and runtime stack walking remain separate from table capacity.

The real minimal startup link has **83 unresolved symbols**. The difference from the earlier 89-symbol broad source boundary consists exactly of _tls_index and the five implemented wit_native transport functions now supplied. No other unresolved dependency disappears merely by reducing the application to this small Main. The counts are workload/profile-dependent and are not task counts or completion percentages.

This confirms that remaining work is chiefly runtime/platform integration, not application size. See [the revised M3 assessment](M3-Runtime-Integration-Plan.md) for observable gates and the superseded estimate.

## Validation

The new hosted executable and expected strict-link check passed. The source audit verified 61 files, the existing hosted probe passed eight groups, and native-target/source references passed four groups. Both runtime-config VMs passed 194 user groups, including actual upstream Thread record construction. All 19 ordinary QEMU scenarios passed, with 178 user groups and 51 contained user hardware faults in successful boots. Release tool builds passed without warnings or errors.

Managed guest entry, a running guest collector, managed finalization/exceptions and CoreCLR/JIT remain unimplemented milestones. NativeAOT is the initial integration target; unchanged portable IL applications remain the later CoreCLR milestone.
