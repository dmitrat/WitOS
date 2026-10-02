# P6.4 upstream host/startup and binding

Status: in progress. Source-built Windows hosting reference and guest namespace primitives pass. The WitOS hosting PAL, actual runtime handoff and guest hosting acceptance remain open. This does not close P6.4 or prove guest CoreCLR/JIT.

## Actual upstream reference

The clean pinned checkout b82454cad0aaaae3db2cf18fbf2cccc36e201ccc builds the real src/native/corehost CMake project through upstream gen-buildsys.cmd. The coreclr-host command uses separate build/install directories and the upstream MSBuild version-generation targets. Sparse checkout includes the required LibraryImportGenerator props; no source bodies are replaced.

VersionPrefix is explicitly set to the package pin 10.0.8 because this source snapshot's repository default is 10.0.7. The isolated CMake include hook selects only the newly generated metadata headers; it does not modify the host implementation or overwrite shared CoreCLR/NativeAOT version files. Arcade's local-build file-version sentinel remains explicit: these are source-built reference binaries, not Microsoft release binaries.

The reference kit contains source-built dotnet.exe, hostfxr.dll and hostpolicy.dll together with hash-verified CoreCLR/JIT snapshots from P6.1 and the pinned standard framework. An ordinary net10.0 application plus a project DLL dependency executes via dotnet Application.dll. It proves actual DynamicMethod JIT and verifies the full loaded paths of dotnet, hostfxr, hostpolicy, coreclr and clrjit. Framework files, IL, configurations, traces, native imports and generated build metadata are hash-bound in a schema-3 atomic acceptance record.

Eight real cases pass:

| Case | Required result |
| --- | --- |
| Default patch roll-forward | Resolves 10.0.0 request to 10.0.8 and executes unchanged IL; exit 42 |
| Exact 10.0.8 / Disable | Executes unchanged IL; exit 42 |
| Disable on 10.0.0 | FrameworkMissingFailure 80008096 |
| Missing 99.0.0 framework | FrameworkMissingFailure 80008096 |
| Malformed runtimeconfig | InvalidConfigFile 80008093 |
| Malformed deps | ResolverInitFailure 8000808B |
| Missing project DLL | Windows managed exception E0434352 with the exact FileNotFoundException assembly identity |
| Missing project DLL with additional probing | ResolverResolveFailure 8000808C before managed assertions |

The missing-project case initially exposed an incorrect test expectation. Upstream deps_entry deliberately skips file-existence checks without additional probing; CoreCLR discovers the missing DLL later. The corrected test requires that trace behavior and the precise managed diagnostic, not just any nonzero exit. The separate probing case retains an early host-policy failure. The failed initial run remains preserved.

Current authoritative record: artifacts/coreclr-host-reference/current-run.json. The reference import inventory has 303 image/import entries and 143 distinct names. Name-based categories are navigation, not a guest-support claim. A separate Windows-only CI lane rebuilds coreclr-source before coreclr-host; it has not been published or executed remotely in this work.

## Guest namespace backend

User ABI v40 adds STORAGE_QUERY call 67. The common immutable package layer implements stat and immediate-child enumeration, including implicit directories, Unicode names and bounded next-entry cursors. File/directory collisions are rejected even when a sibling sorts between the file and descendant (a, a.b, a/child). EOF, missing directory and non-directory are distinct. Output fields are transactional.

The syscall copies the full request and counted path, validates the entire fixed output range, then copies one bounded result with interrupts disabled. Metadata queries allocate no handles/pages. The native System.Native adapter invokes these real syscalls. Guest tests at 128/512 MiB cover root enumeration, all 176 file records, invalid paths/cursors, missing directories, partial/readonly buffers, input/output aliasing and file-open rejection for directories. Existing streaming, handle lifecycle and supervisor read/write/execute denial checks remain passing. Native parser/namespace corpus: 9750 guard-boundary cases plus 14 actual firmware transport/rollback cases.

## Remaining work

Preserve upstream fx_muxer/runtime_config/fx_resolver/deps_resolver policy; do not replace it with a smaller custom resolver. Required platform contracts include actual file views (mmap_read and mmap_copy_on_write), normalized paths, directory probing, environment and native library lifetime. The existing native heap supports nothrow C++ allocations; it does not provide the full throwing C++/standard-library contract needed by the host. Keep those dependencies real and unsupported methods unresolved.

After the PAL and native dependency closure, connect real coreclr_initialize/coreclr_execute_assembly handoff and prove guest configuration/binding behavior. Windows reference execution, a static archive or successful placeholder callbacks cannot satisfy that criterion. All mandatory gates passed for this ABI v40 namespace slice: Release without warnings/errors, 37 host groups / 555 PE inputs, 9750 parser cases / 14 firmware cases, all 20 kernel scenarios, hosting/storage/memory reference matrices and audit/probe/target/config/source/boot. artifacts/p6-host-checkpoint.json binds 11 gate logs, the atomic host acceptance and source hashes. P6.4 remains unchecked in PLAN.

### File-view backend checkpoint

The pinned json_parser.cpp uses pal::mmap_read for the selected Windows-shaped UTF-16 host ABI; its Unix path uses copy-on-write for in-situ parsing. The actual Windows map_file opens a read handle, obtains file size, creates a section/view, closes both handles and retains the view until pal::munmap. The new System.Native/file_view.c backend now preserves view lifetime independently of the open file, whole-file bytes, exact logical length, readonly protection and complete rollback. Private-copy support must not mutate the kernel package or silently enable unsupported VirtualAlloc copy-on-write modes. Bundle/native-image requirements remain separate from ordinary JSON reads.


The native backend materializes immutable file bytes in separate owned reservations. Readonly views become R/NX after population; private-copy views stay RW/NX and cannot alter the kernel package. This is an eager private snapshot, not a new shared/write-through file mapping implementation. Four component-private records carry non-repeating tokens, so a stale descriptor cannot release a reused address. The path helper opens/maps/closes its temporary file before publishing the view. Consumers must quiesce readers before unmapping; views are process-scoped and can outlive the creating worker.

Guest acceptance covers full-byte comparison, unchanged file cursor, file-close and worker-exit lifetime, independent private writes, forged/stale descriptors, registry quota, OOM recovery, backing/table allocation boundaries, and exact accounting. A real code alias forces BUSY on unmap; removal of that alias permits a successful retry without losing the registry record. Another case places the descriptor inside the private view being unmapped. Actual CPL3 write/NX faults verify protection and teardown.

Hosted fault injection executes the actual adapter through controlled syscall callbacks. It checks reserve, partial commit, first/later read and protect failures; temporary file handles close on every recoverable failure. Failed ordinary release retains a retryable view, while failed rollback release is fail-fast. It is test-only fault injection, not a guest OS substitute.

The optimized native adapter can emit chained unwind metadata, so the storage component now uses the existing WIT_PE_UNWIND_RUNTIME validator. The plain PE profile is unchanged; no general exception support is claimed from structural admission. The kernel runner requires five post-isolation contained faults (three supervisor storage probes and two file-view probes). Initial capability logs: artifacts/p6-file-view-guest4.log and artifacts/p6-file-view-faults2.log. Final common gates passed: Release, 38 host groups / 555 PE inputs, all 20 kernel scenarios, storage/memory at 128/512 MiB and audit/probe/target/config/source/boot. artifacts/p6-file-view-checkpoint.json records the evidence. Actual pal::mmap_* integration, complete native dependencies and the CoreCLR handoff remain open.


The next integration must compile against the pinned corehost/hostmisc/pal.h, whose selected Windows branch uses std::wstring and an inline munmap forwarding to UnmapViewOfFile. json_parser.cpp uses readonly views in this branch; UTF-8-to-wide conversion precludes its Unix in-situ path. The private C backend is now exercised, but these actual C++ entry points are not yet bound to it. Preserve the real standard-library/throwing-allocation dependencies and record any platform-header changes explicitly; do not substitute object-layout tricks or successful runtime stubs to claim integration.


A preliminary static-archive audit of the actual source-built libhostfxr.lib retains 124 potential external names after subtracting archive definitions. This is navigation, not a strict final-link result. It includes throwing operator new, _CxxThrowException, __CxxFrameHandler4, __std_exception_copy/destroy, and real file-mapping APIs. Evidence: artifacts/p6-hostfxr-static-boundary.json and its dumpbin log. The existing nothrow adapter cannot be treated as closure of these requirements.


### Actual PAL file leaves

`src/Runtime.CoreClr/host_files.witos.cpp` now implements the real pal::file_exists, mmap_read, mmap_copy_on_write and munmap signatures. It consumes genuine std::wstring objects, converts rooted virtual UTF-16 paths to bounded UTF-8 keys, invokes the production native file/view backend and preserves native last-error on success. Invalid encoding, missing files, empty-file mapping and invalid unmap are explicit failures. Relative/fullpath policy is still pending; this leaf requires canonical rooted paths.

`experiments/CoreClrHost/host-files.lock.json` pins canonical upstream pal.h and configure.h.in bytes. The only declaration correction makes the Windows inline munmap a declaration under WITOS_HOST_FILES. The generated configuration identifies witos/x64; it does not advertise Windows as the guest OS. The upstream unused-parameter warning is suppressed only around that header. The hosted coreclr-host-files command uses atomic attempt publication and real C++ standard-library objects with a controlled syscall model.

The same PAL leaf object executes in the guest using legal constexpr standard-library string objects. A short package control p contains the unchanged runtimeconfig bytes, permitting successful PAL mapping without inventing a throwing allocator or forging string layout. The storage fixture now carries 177 files / 63,092,722 payload bytes. Both 128/512 MiB profiles pass PAL namespace, readonly/private mapping, unmap and native error checks. This proves the leaf contracts, not the full host's C++ allocation/exception/runtime closure. General/long string objects and Unicode/error boundaries are exercised in the hosted test; broader guest C++ support remains pending.

A strict NODEFAULTLIB link rooted at actual hostfxr_main with WitOS native transport yields 119 unresolved externals and no other linker-error category. artifacts/coreclr-host-link/baseline.json records the exact archive/entry/log hashes. This is a diagnostic Windows-reference archive boundary, not a runnable guest host or a port-completion count.

Final common regressions passed for this integration slice: Release, isolated 38/38 host groups with 555 PE inputs, all 20 kernel scenarios, storage/memory at 128/512 MiB and audit/probe/target/config/source/boot. A prior 36/38 host run failed existing process cleanup/start timing checks and remains preserved; budgets were not changed. artifacts/p6-pal-files-checkpoint.json binds the final evidence. P6.4 remains open.


### Path and current-directory checkpoint

The native System.Native path layer now normalizes counted UTF-8 virtual paths with a process-local current directory. It accepts both separators at the private compiler/PAL boundary, collapses repeated separators and dot components, clamps parent traversal at the virtual root, rejects invalid UTF-8/control/drive syntax and preserves outputs on failure. Input work is bounded to 4096 bytes and the normalized/intermediate path to the package's 1024-byte key quota. Directory-qualified paths require an actual directory; current-directory changes validate through storage metadata before publication. The immutable namespace has no symlinks.

The actual C++ pal::getcwd/fullpath/realpath/is_path_rooted/is_path_fully_qualified entry points are implemented in a separate object. Their std::wstring assignments retain genuine allocation/EH dependencies; they are hosted-tested and are not smuggled into allocation-free guest leaf probes. pal::file_exists and mmap now use the same native resolution rather than requiring pre-normalized absolute input. Fullpath/realpath check actual existence before changing the supplied string.

A 622-case readonly guard-boundary corpus plus oversized-length rejection covers Unicode, malformed input, root clamping and full-buffer boundaries. A capacity subtraction was written to avoid unsigned underflow when a maximum-length cwd is followed by another component. Guest tests cover relative file paths, directory-only suffixes, failure atomicity, process-wide CWD after worker exit and relative actual PAL leaf calls. The real x64 __chkstk object is linked for the bounded conversion buffers. Final hosted and 128/512 MiB guest tests passed, including CWD persistence after a worker changes it and exits. Release, 39 host groups / 555 PE inputs, all 20 kernel scenarios, hosted PAL and storage/memory acceptance, and audit/probe/target/config/source/boot gates passed. artifacts/p6-path-checkpoint.json binds the evidence. The full hosting PAL/native dependency closure and actual CoreCLR handoff remain open.


### Directory enumeration and framework layout

The native immutable-directory iterator captures CWD at open, enumerates immediate children with the kernel's bounded cursor, filters files/directories, and preserves output/cursor/found on failure. EOF is distinct from missing/non-directory. Counted UTF-8 patterns support literal characters, '*' and Unicode-scalar '?', case-sensitively. These are WitOS namespace rules, not Windows DOS/8.3 wildcard emulation. Upstream hosting's '*' and '*.deps.json' patterns are covered.

Actual pal::readdir and readdir_onlydirectories overloads now append real std::wstring entries to a real std::vector in a separate C++ object. Allocation/EH dependencies remain genuine. Hosted tests cover filtering, directory-only selection, Unicode, append semantics and missing/file errors. The native backend passed 84,568 exhaustive small-name/pattern comparisons against an independent recursive oracle, plus Unicode and iterator/CWD/failure cases.

Boot packaging now uses shared/Microsoft.NETCore.App/10.0.8 rather than the earlier private framework prefix, and carries unchanged Microsoft.NETCore.App.deps.json/runtimeconfig.json. Guest tests discover the version directory, enumerate the two framework JSON files and two app JSON files, and prove that changing CWD after opening an iterator does not redirect it. The current fixture has 179 files / 63,122,308 payload bytes. These reference metadata bytes are not a completed WitOS runtime distribution: referenced native runtime assets and the actual handoff are still pending.

Final hosted and 128/512 MiB guest acceptance passed. Release, 40 host groups / 555 PE inputs, all 20 kernel scenarios and the full audit/probe/target/config/source/boot pipeline passed. artifacts/p6-directory-checkpoint.json binds the evidence and byte-exact package proof. P6.4 remains open.


### Environment lookup and block lifetime

The existing immutable native environment adapter now provides real GetEnvironmentStringsW/FreeEnvironmentStringsW direct and readonly-IAT bindings. A block is an independently allocated UTF-16 name=value sequence with a final extra NUL; the empty environment has two NULs. The source table remains immutable. Four component-private block records prevent foreign/interior/immediate-double frees; allocation failure rolls back its claimed record. Allocation and release use the actual native nothrow heap, never writable payload metadata as authority. Blocks are process-scoped and can outlive their creating worker. Raw pointer reuse follows the Windows-shaped API's lifetime rules; pointers are not cross-process capabilities.

Guest tests exercise empty/nonempty tables, maximum existing name/value quotas, Unicode, independent copies, direct/IAT calls, preserved last-error on success, invalid frees, pool/heap exhaustion and recovery, worker concurrency and cross-thread release after worker exit. Whole memory/reservation accounting returns to baseline. The runner requires User.PalEnvironmentBlocks rather than inferring this coverage from older environment markers.

Actual C++ pal::getenv and enumerate_environment_variables signatures are implemented separately with genuine vector/string/callback/EH dependencies. Lookup preserves upstream behavior: output is cleared and missing/empty values return false. Enumeration splits at the first equals sign and owns the native block through callback execution. A scope guard frees it on normal exit and exception unwinding. The hosted test invokes real Windows environment APIs through a test-only counting shim and confirms two allocations/two releases with no live blocks after normal and throwing callbacks. It also verifies the empty variable is present in enumeration and values may contain equals signs.

This is not mutable environment support, guest C++ heap closure or CoreCLR startup. Existing environment quotas and startup publication rules are unchanged. Final Release, 40 host groups / 555 PE inputs, all 20 kernel scenarios, hosted PAL contracts, storage/memory at 128/512 MiB and audit/probe/target/config/source/boot gates passed. artifacts/p6-environment-checkpoint.json binds the evidence. P6.4 remains open.


### Native library PAL foundation

See [native DLL loading](P6-Native-Libraries.md) for the ABI 41 loader/export/reference contract and its validation. Actual pal::load_library/get_symbol/unload_library now execute in the guest and against a real Windows DLL reference. Imports, DLL initialization/TLS, automatic module unwind lifetime, get_loaded_library and the full C++/hostfxr dependency closure remain open. All native/managed guest gates passed; final host suite remains 40/41 due to strict process-cleanup timeout, so this checkpoint is not reported as wholly green.


Module discovery now has native FIND/PATH operations (ABI 42), with retained references, deterministic ambiguity rejection and atomic immutable-path output. Actual C++ get_loaded_library/get_module_path passed hosted success/error/bad_alloc tests; guest native discovery passed both memory profiles. Full checks and the subsequent 42/42 host suite passed, while the earlier intermittent process completion issue remains recorded rather than declared fixed. See artifacts/p6-discovery-checkpoint.json and the native-library document. Import descriptors now have a separately verified common parser; imports are not enabled in the guest loader yet.


ABI 44 adds kernel-owned native module reader leases and automatic module lookup in the checked unwinder. Real DLL-frame, last-external-reference retention, reader quota/type/stale-buffer and exhaustion fail-fast probes passed at 128/512 MiB. General kernel/runtime regressions remain in progress. DLL initialization/TLS, foreign module-frame coverage and full native/C++ hosting startup remain open; see P6-Native-Libraries.md.
