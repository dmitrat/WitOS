# P1.6 named embedded images and module lookup

User ABI v24 and image descriptor v2 add a kernel-owned module resource name. The descriptor is 568 bytes and still fits inside the existing readonly information page. No extra physical page, filesystem, DLL loader or managed module registry is introduced.

## Identity and lifetime

The private named PE loader accepts an optional trusted kernel label. The initial namespace is `boot:/` followed by an ASCII resource filename, at most 127 characters total. It identifies bytes embedded by the boot/test runner; it is not a claim that a filesystem file exists. Labels are transported as UTF-16 because that is the upstream PAL interface. The namespace is deliberately independent of host paths and PDB information.

The kernel bounds and validates the entire label before allocating or publishing a process, takes an owned copy and places it in the readonly handoff. Existing anonymous loaders call the same path with no label. Anonymous images have a valid module base but no fabricated name. Native image publication validates the descriptor version, size, reserved fields, bounded name, terminator and namespace before publishing it to TLS constructors or workers. The borrowed name remains valid for the component lifetime, including native thread cleanup.

## Native contracts

- PalGetModuleFileName returns the actual borrowed UTF-16 name and character length excluding NUL. Null module selects the current executable. Failure clears the borrowed pointer; an anonymous image reports ERROR_PATH_NOT_FOUND.
- GetModuleFileNameW accepts null/current module, copies the name including termination and preserves last-error on success. A short buffer contains a terminated prefix, returns its capacity and reports ERROR_INSUFFICIENT_BUFFER. Capacity zero returns zero with that error. Unknown module bases fail without modifying the destination.
- GetModuleHandleW returns the published image base for null, the full boot resource label or its filename. The ASCII resource namespace is compared case-insensitively; slash/backslash separators are accepted for lookup. Missing libraries, empty/oversize names and unknown paths fail with ERROR_MOD_NOT_FOUND. This is discovery, not a closeable kernel capability or reference-counted DLL load.
- GetProcAddress validates the actual current image and reports ERROR_PROC_NOT_FOUND for names/ordinals. This follows a checked fact: the current kernel loader rejects every nonempty PE export directory before allocation. No export addresses or DLL handles are invented. Unknown module bases report ERROR_MOD_NOT_FOUND. Export loading requires a deliberate future loader/adapter extension.

These routines operate on caller-owned native buffers; invalid pointer access is still subject to contained user faults, as for CRT routines. They do not grant mapping rights or write through supervisor mappings. Direct entrypoints and readonly import slots have the same implementation. No compiler TLS, heap or locale state is needed, and native errno remains independent.

## Optional upstream shutdown lookup

The remaining GetModuleHandleW/GetProcAddress roots were in pinned `startup.cpp::RhInitialize`, which probes ntdll!RtlDllShutdownInProgress. That symbol is optional in upstream: a null lookup retains RtlDllShutdownInProgressFallback. The fallback consults the real current runtime thread and g_threadPerformingShutdown set by OnProcessExit. The source remains unchanged. Guest tests exercise the failed lookup pattern; execution of the actual attached-runtime shutdown path remains P1.8/P1.9/P3 evidence, not a claim of this module slice.

## Validation

The actual source-built module objects are copied and hash-verified into RuntimeThreadFixture. Guest modes 71/72/73 cover named, no-compiler-TLS and anonymous images. Tests include both load bases, before-publication failure, borrowed pointer identity, direct/import equivalence, readonly imports, full names and basenames, every output capacity through the exact boundary, guard-page input/output tails, invalid handles, missing DLLs/exports, malformed descriptor rejection, worker slot reuse and discovery after native TLS cleanup.

Kernel checks independently verify readonly handoff mappings, name length/content, copied rather than borrowed label storage, rejection of invalid/oversize labels before allocation, zero unintended console writes and full handle/page recovery. The older CPU image excludes unused UTF/module transport objects and retains 122 plain unwind records; all module tests execute in the separate 31232-byte fixture with 79 records. The loader limit remains 128.

Passed: Release build, runtime-audit/probe/target/source/readiness, four QEMU runtime profiles with 235 user groups and 54 expected contained faults each. Full native archive: 115 members; configuration archive: 33 exact objects. Minimal/broad strict link: 33/39 unresolved dependencies. All 20 ordinary boot scenarios also passed. No guest managed execution or full P1.6 completion is claimed.

Logs: artifacts/p1-module-config.log, artifacts/p1-module-test.log, artifacts/p1-module-audit.log and artifacts/p1-module-probe.log. Exact input hashes and PE sizes are in runtime-source/runtime-config reports.

References: [GetModuleFileNameW](https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-getmodulefilenamew), [GetModuleHandleW](https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-getmodulehandlew), [GetProcAddress](https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-getprocaddress), and pinned upstream PalCommon.cpp/startup.cpp at b82454cad0aaaae3db2cf18fbf2cccc36e201ccc. Capacity-zero and absent-export errors were also checked directly on the Windows host.
