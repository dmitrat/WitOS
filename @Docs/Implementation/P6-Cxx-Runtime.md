# P6.4 guest C++ runtime for the host

Date: 2026-10-05. Status: P6.4.e (Windows), P6.4.f (guest), P6.4.g (vcruntime surface), P6.4.h (UCRT subset),
P6.4.i (microsoft/STL: exceptions, algorithms, threads, locales and streams), P6.4.j1 (the host's guest build and
its inventory), P6.4.j2 (the rest of the PAL), P6.4.j3a (process state in the kernel), P6.4.j3b (module paths)
P6.4.j3c1 (a C++ library's startup; both host libraries link with no unresolved symbol), P6.4.j3c2 (C++
exceptions in a library) and P6.4.j3c3 (the host libraries in the guest) complete, and with them P6.4.j. In P6.4.k,
k1 (`hostfxr_main` up to CoreCLR) and k2 (the inventory of the guest's `coreclr.dll`) are complete; k3 (closing
that inventory) has begun with the C runtime.

## Decision

The real `hostfxr` and `hostpolicy` are C++ programs with exceptions and the standard library. A strict
`NODEFAULTLIB` link rooted at `hostfxr_main` with the WitOS native transport leaves 119 unresolved externals
(`artifacts/coreclr-host-link/baseline.json`):

| Group | Count | Examples |
|---|---|---|
| Win32 imports | 42 | `CreateFileW`, `CreateFileMappingW`, `EnterCriticalSection` |
| C++ EH, RTTI and GS (vcruntime) | 17 | `_CxxThrowException`, `__CxxFrameHandler4`, `type_info` vtable, `__GSHandlerCheck_EH4` |
| Standard library (msvcp) | 11 | `std::_Xlength_error`, `std::_Throw_Cpp_error`, `_Cnd_broadcast` |
| `new` and `delete` | 4 | scalar and array forms |
| Other CRT | 45 | thread-safe statics (`_Init_thread_*`), vector constructor iterators, CRT functions |

The guest gets a C++ runtime built from source, never from a Visual Studio installation (decided 2026-10-04):

- The standard library's separately compiled part comes from microsoft/STL at the pinned tag `vs-2022-17.14`
  (Apache-2.0 with LLVM exception), verified by the hash of the canonical download like the other upstream pins. That
  tag requires MSVC 19.44 or newer, which covers the CI toolset (14.44) and the local one (14.51).
- WitOS writes its own C++ exception runtime. The on-disk format of the compiler's `__CxxFrameHandler4` data is
  described by the public toolset header `ehdata4_export.h`, which serves as documentation; the vcruntime reference
  sources are not used.
- Rejected: linking the toolset's `libvcruntime.lib`/`libcpmt.lib` (provenance tied to an installation, many
  Win32/UCRT imports) and rebuilding the host with clang and libc++ (diverges from upstream's Windows build).

AGENTS.md already forbids fake throwing `new` or CRT implementations that only close the link boundary: every piece
must be real and tested.

## How C++ exceptions work on x64 Windows

`throw` calls `_CxxThrowException(object, throwInfo)`, which raises exception `0xE06D7363` with the object, its
`ThrowInfo` and the image base. The dispatcher calls each frame's language handler; functions with C++ EH name
`__CxxFrameHandler4` and their compressed `FuncInfo4` in the unwind data. In the search phase the handler maps the IP
to an EH state, finds the try blocks around it and matches the thrown type's catchable types against each catch. For a
match it calls `RtlUnwindEx` with `STATUS_UNWIND_CONSOLIDATE`: the unwinder runs the destructors of the frames in
between (the handler's unwind phase walks the unwind map down to the try's state), and then calls a consolidation
callback on the current stack, below the dead frames, so the exception object stays alive. The callback copies the
object into the catch parameter, calls the catch funclet with the establisher frame, destroys the exception object
unless it was rethrown, and returns the continuation address, where execution resumes with the target frame's
context. A `throw;` rethrows the current exception; an exception that leaves a `noexcept` function terminates.

## Slices

- [x] **P6.4.e** Exception runtime on Windows: `_CxxThrowException`, `__CxxFrameHandler4`, catchable-type matching,
  `type_info`, the consolidation callback and the current-exception state, in `src/Runtime.Cxx/`. A test program built
  with `/EHsc` against this runtime instead of vcruntime, on Windows' own dispatcher and unwinder, must print the same
  trace as with vcruntime.
- [x] **P6.4.f** Exception runtime in the guest: `STATUS_UNWIND_CONSOLIDATE` in the WitOS `RtlUnwindEx`, including
  exceptions thrown inside a catch block; the same test passes in the guest.
- [x] **P6.4.g** The rest of the vcruntime surface the host uses: thread-safe statics, GS cookies and handlers,
  throwing `new`/`delete` on the native heap, vector constructor iterators, `std::exception` support.
- [x] **P6.4.h** The host's UCRT subset, WitOS's own and checked differentially against the real UCRT: the wide
  `printf` family with UCRT's options, legacy wide specifiers and buffer contracts (floating-point conversions fail
  fast until something needs them, then through the pinned STL's Ryu), `wcs*`, the C and UTF-8 locales, calendar
  time, error messages, `malloc`/`realloc`/`free` over the native heap, stdio over the WitOS console and files, and
  the UCRT entry points (`__stdio_common_*`, `__acrt_iob_func`). Decided 2026-10-04 after two open candidates
  failed the fit: musl's wide functions assume a 32-bit `wchar_t` and its sources are GNU C; mingw-w64's
  `mingw_pformat.c` formats floating point through the x87 80-bit `long double` and gdtoa, which the host never
  uses. The Windows SDK's UCRT sources are not used. It comes before the STL, whose sources call it.
- [x] **P6.4.i** microsoft/STL `vs-2022-17.14`: pin, audit and build of the separately compiled sources the host
  needs with the UCRT functions those sources call. STL's build includes vcruntime's `internal_shared.h` from the
  toolset's reference sources; a minimal WitOS header replaces it. Their Win32 calls go through WitOS adapters, and
  any change to a pinned file is an upstream patch.
  - [x] **P6.4.i1** Exceptions and algorithms: the pin, `std::_X*` and `_Throw_Cpp_error`, system error messages,
    `std::uncaught_exception` and the vectorized algorithms.
  - [x] **P6.4.i2** Threads and synchronization: `_Mtx_*`, `_Cnd_*`, `_Thrd_*` with `_beginthreadex`.
  - [x] **P6.4.i3** Locales and streams: the support behind `std::wstringstream`.
    - [x] **P6.4.i3a** The UCRT functions the STL's locale sources call: narrow `sprintf_s`, the global locale, time
      names, character classes and string helpers.
    - [x] **P6.4.i3b** The Win32 NLS functions, critical sections and the 28 locale and stream sources, with the
      static initializers they need and a native heap that scales to the full runtime profile.
- [x] **P6.4.j** The host for the guest: upstream `hostfxr` and `hostpolicy` compiled unchanged for the guest, with
  WitOS's own PAL objects in place of the Windows PAL, linked strictly with no unresolved symbol.
  - [x] **P6.4.j1** The guest build and its inventory: the corehost sources from the verified checkout, compiled with
    upstream's options against the pinned STL and linked over the WitOS runtimes; each image's unresolved externals
    are a recorded expectation that the command enforces.
  - [x] **P6.4.j2** The rest of the PAL that needs no new kernel interface: strings, trace output, the timestamp,
    installation locations and the small queries, with the Win32 functions only the host calls.
  - [x] **P6.4.j3** Libraries in a process: a module's own path and the module at an address (a kernel interface),
    process state that every module sees (environment, console, current directory), a library's startup (GS cookies,
    TLS, initializers, `atexit`) and the system calls apart from the process entry; no unresolved symbol.
    - [x] **P6.4.j3a** Process state in the kernel: the environment and the current directory, one for every module
      (user ABI v50).
    - [x] **P6.4.j3b** A module's own path and the module at an address (user ABI v51).
    - [x] **P6.4.j3c** A library's startup, the system calls apart from the process entry and the kernel's admission
      of `hostfxr.dll`/`hostpolicy.dll`.
      - [x] **P6.4.j3c1** A C++ library's startup and atexit, the libraries' link set without the process entry, and
        the full runtime profile's limits for a library; `hostfxr` and `hostpolicy` link with no unresolved symbol.
      - [x] **P6.4.j3c2** C++ exceptions in a library: dispatch and unwinding through the frames of loaded modules.
      - [x] **P6.4.j3c3** The real `hostfxr.dll` and `hostpolicy.dll` load in the guest and run their startup.
- [ ] **P6.4.k** Guest `hostfxr_main` reads a real application's runtimeconfig and deps through `hostpolicy` and
  reaches `coreclr_initialize`/`coreclr_execute_assembly`.
  - [x] **P6.4.k1** `dotnet /app/CoreClrProbe.dll` in the guest: the muxer, the framework from the runtimeconfig,
    hostpolicy, the deps and their assets, up to resolving CoreCLR, which the guest does not have yet.
  - [x] **P6.4.k2** The guest's `coreclr.dll`: upstream CoreCLR built for the guest and its unresolved inventory.
  - [ ] **P6.4.k3** Closing that inventory, then loading the image: the C runtime, Win32, COM/OLE/WinRT, the
    kernel's limits, threads in libraries, the TEB and one exception dispatcher.
    - [x] **P6.4.k3a1** vcruntime's searches and range-check report.
    - [x] **P6.4.k3a2** UCRT's classes, integers, secure strings, environment and sorting.

## P6.4.e: the exception runtime on Windows

`src/Runtime.Cxx/` holds the runtime: `exception_data.h` (the ABI structures and a bounded reader of the compressed
FH4 encoding), `frame_handler.cpp` (`__CxxFrameHandler4` and the catch callback), `throw.cpp` (`_CxxThrowException`,
per-thread state, `__uncaught_exceptions`, `__std_terminate` and, for now, the Windows platform calls) and
`type_info.cpp` (the `type_info` vtable that every type descriptor refers to). It needs `RaiseException`,
`RtlUnwindEx` and `RtlPcToFileHeader`, and no SEH runtime: its own handler unwinds the catch callback.

### Scenarios and Windows evidence

`tests/User.X64/cxx_exceptions.cpp` runs 21 scenarios without any library: catch by value, reference and pointer;
base classes, multiple and virtual inheritance with pointer adjustment through the virtual base table; const pointers;
`catch (...)`; destructors of the frames in between; nested try blocks; rethrow from the catch and from a function
it calls; a new exception thrown from a catch; try blocks inside a catch; an exception that leaves two nested catches;
`uncaught_exceptions` during unwinding and inside the catch; plain structures copied byte by byte. Every observable
step is a token. `CxxExceptionsTests` builds the same source with the CRT and vcruntime and with the WitOS runtime and
no CRT or vcruntime (kernel32 and ntdll only); both print `NativeCxxExceptionImage.WINDOWS_TRACE` in every run, with
the local toolset 14.51 and with 14.44 of the CI runners. The trace fixes vcruntime's observable order: the catch
parameter is built in the search phase, before the frames in between unwind; an exception leaving a catch destroys
the old object while unwinding, before the next catch runs; a rethrow keeps the object; `uncaught_exceptions` is 1 in
a destructor during unwinding and 0 inside the catch.

### What the compiler's data required

- Catch funclets have their own try and unwind maps with local state numbers and only the try blocks inside the
  catch; their `dispFrame` names the slot holding the parent's frame, which addresses the objects and is passed to
  every funclet in `rdx`. A funclet unwinds its own map to -1.
- A catch funclet without objects or try blocks has no handler at all, and the catch callback has only an unwind
  handler, so the dispatcher's search phase sees neither. While a catch runs, the frame holding its try block keeps
  the IP inside the try. A per-thread record of each running catch gives that frame its state before the try. When
  an exception leaves the catch, the record stays, marked with that exception, until the exception's own catch
  begins, because the unwind passes the frame after the callback's cleanup. The records live in thread storage, not
  on the callback's stack.
- Catch funclets return an index into the handler's encoded continuation addresses; a catch that cannot end normally
  (it ends with `throw;`) has none.
- `_CxxThrowException` must match the compiler's predefined declaration (`_ThrowInfo *`, `noexcept(false)`).
- The callback's cleanup is a C++ destructor that this runtime's own handler runs while unwinding; it tells a
  rethrow from a new exception by the object the unwind is delivering.

### Limitations

`std::terminate` is a fail-fast without terminate handlers; a non-C++ unwind (`longjmp`, SEH) through a running catch
is not supported; separated code (`isSeparated`, profile-guided layouts) decodes but is not exercised; exceptions in
destructors during unwinding are not handled. `operator delete` in the Windows test harness is an explicit fail-fast
stub: the scenarios never call it, and real `new`/`delete` belong to P6.4.g.

## P6.4.f: the exception runtime in the guest

The same runtime runs in the guest with `platform_witos.cpp`, whose `Fatal` is the component's fail-fast; the
`RaiseException` and `RtlUnwindEx` it calls are the guest's own bindings (`Runtime.Pal.Win32/X64/native_exception.asm`
and `Runtime.CoreClr/X64/coreclr_unwind_bindings.asm`). The image base of a throw is the throwing module's
`__ImageBase`: the runtime is linked into each module, so a throw always names a `ThrowInfo` of its own module.

### Frame consolidation in the guest dispatcher

The guest `RtlUnwindEx` gained `STATUS_UNWIND_CONSOLIDATE`, in the dispatcher profile with dynamic code
(`WITOS_DYNAMIC_CODE`, the CoreCLR memory image):

- The C++ handler calls `RtlUnwindEx` from the search phase of its own exception. The guest runs the unwind as a
  nested exception, which the kernel refuses to begin while the thread holds the search's stack lease; a
  consolidating unwind abandons that search, so its lease ends before the unwind begins, as a dynamic-code handler's
  lease does around its call.
- When the unwind reaches the target frame and has called its handler, `consolidate()` retires the unwind's
  exception and the abandoned search's together (`EXCEPTION_UNWIND` through the search's token): every catch leaves
  no exception active, and the kernel's limit of four nested exceptions is never reached.
- The kernel then enters `wit_native_consolidate_start` (`Runtime.NativeAot/X64/unwind_consolidation.asm`) below
  the dead frames, with the target's nonvolatile registers. Its prolog copies the target's machine frame and saves
  those registers where its unwind codes say, so an exception leaving the callback unwinds straight to the target
  frame. It calls the record's callback and passes the returned address to `wit_native_consolidate_finish`, which
  restores the target there through `THREAD_CONTEXT_RESTORE`.
- A consolidation that would also have to retire a collided unwind is refused.

### Guest evidence

Mode 21 of the host runtime fixture (P6.4.i; first of the CoreCLR mapper fixture,
`tests/User.X64/cxx_exceptions_guest.cpp`) runs the 21 scenarios on the
runtime and the guest dispatcher and compares the trace with `WINDOWS_TRACE`, which the tool generates into
`cxx_exception_trace.h`; the trace grows in the report page, which the kernel prints when a run fails.
`coreclr-memory` passes `Code.CxxExceptions` in both profiles. The scenarios throw more often than the four nested
exceptions the kernel admits, so every catch retired its exceptions. The scenario classes' deleting destructors
refer to sized `operator delete`, which the guest harness, like the Windows one, defines as a fail-fast that the
scenarios never call.

## P6.4.g: the rest of the vcruntime surface

- **Allocation.** Throwing `operator new` and `new[]` (`new.cpp`) call the platform's nothrow allocator and throw
  `std::bad_alloc` when it fails; there is no new handler. In the guest the allocator is the native heap of
  `Runtime.NativeAot/native_new.witos.cpp`, which also owns every `delete`; on Windows `platform_windows.cpp` provides
  the same operators over the process heap.
- **Arrays.** `vector.cpp` provides `eh vector constructor iterator` and `eh vector destructor iterator` under their
  decorated names through `/alternatename`. A constructor that throws destroys the elements already built, newest
  first; a destructor that throws still destroys the rest; a second exception during that cleanup terminates. Both
  functions are `noexcept(false)`: under `/EHsc` the compiler takes `extern "C"` functions for non-throwing and drops
  their cleanup otherwise, which the Windows comparison caught.
- **Thread-safe statics.** `statics.cpp` implements the contract MSVC compiles against: a guard per static, a global
  epoch and the per-thread `_Init_thread_epoch`; `_Init_thread_header` claims an initialization or waits for another
  thread's, `_Init_thread_footer` publishes it and `_Init_thread_abort` reopens it after a throwing initializer. The
  platform lock is an SRW lock with a condition variable on Windows and the native lock with a yield in the guest.
- **`std::exception`.** `__std_exception_copy` copies an owned message into the nothrow allocator and shares a
  borrowed one; `__std_exception_destroy` frees it.
- **GS.** `__GSHandlerCheck_EH4` (`gs_witos.cpp`) checks the frame's cookie with the guest's `wit_native_gs_check`,
  whose GS data follows the FuncInfo4 RVA, and then runs `__CxxFrameHandler4`. The guest's cookie and
  `__security_check_cookie` need startup initialization (`wit_native_security_initialize_system`): the first run of
  the scenarios without it ended in the GS failure exit, and the host's startup must initialize it too.
- **Control Flow Guard.** `__guard_dispatch_icall_fptr` points to a plain jump (`X64/guard_dispatch.asm`): images
  built with `/guard:cf` run, but the guest does not enforce CFG.

`tests/User.X64/cxx_runtime.cpp` adds ten scenarios built with `/guard:cf`: `new` and `delete`, arrays with
`delete[]`, `std::bad_alloc` from a failed allocation, nothrow `new`, a copied `std::runtime_error`, a static built
once, a static whose first initializer throws and whose second call retries, a GS-protected frame that throws, a
guarded indirect call and an array whose third constructor throws. On Windows both builds print the extended
`WINDOWS_TRACE`; there the WitOS build has no GS, whose handler and cookie check belong to the guest. In the guest the
same mode 21 runs both sets with `/GS` on the runtime scenarios, so the GS handler, native heap, statics and
iterators all run (`Code.CxxExceptions`, both profiles).

Of the host's remaining vcruntime and CRT symbols, `_fltused`, `__chkstk`, the memory routines and `_tls_index`
already exist in the guest, and `atexit` belongs to the C runtime slice. Both harnesses define `atexit` as
registration only: static destructors run after the trace on Windows and not at all in the test.

## P6.4.h: the UCRT subset

`src/Runtime.Crt/` holds the C runtime functions the real `hostfxr` and `hostpolicy` call, under UCRT's names and
contracts. A strict link of `hostpolicy` with `hostcommon` and `hostmisc` (the 119-symbol baseline covers `hostfxr`
only) adds `_beginthreadex`, `_wremove`, `_wrename`, `fclose`, `fwrite`, `terminate`, `_purecall` and the STL's
locale, stream and thread internals to the list.

| File | Functions |
|---|---|
| `format.cpp` | the printf engine; `__stdio_common_vswprintf`, `__stdio_common_vsnwprintf_s`, `__stdio_common_vsprintf_s` (P6.4.i3a) |
| `stdio.cpp` | `__acrt_iob_func`, `__stdio_common_vfwprintf`, `fputwc`, `fputc`, `fputs` (P6.4.i2), `fwrite`, `fflush`, `setvbuf`, `_wfsopen`, `fclose`, `_wremove`, `_wrename` |
| `locale.cpp` | `_create_locale`, `_free_locale`; the C and UTF-8 conversions; `setlocale`, `localeconv`, `___lc_*`, `___mb_cur_max_func`, `__pctype_func`, `_lock_locales` (P6.4.i3a) |
| `string.cpp` | `wcslen`, `wcscmp`, `wcsncmp`, `wcschr`, `_wcsicmp`, `_wcsnicmp`, `tolower`, `toupper`, `wcstoul`, `_wtoi`, `_wcserror_s`; `islower`, `isupper`, `isspace`, `__strncnt`, `wcsnlen`, `strcspn`, `_wcsdup` (P6.4.i3a) |
| `time.cpp` | `_time64`, `_gmtime64_s`, `wcsftime`; `_Strftime`, `_Wcsftime`, `_Gettnames`, `_Getdays`, `_Getmonths` and their wide forms (P6.4.i3a) |
| `heap.cpp`, `runtime.cpp` | `malloc`, `calloc` (P6.4.i2), `realloc`, `free`; `ceilf`, `terminate`, `_fltused`; `abort`, `_invoke_watson` (P6.4.i); `frexp`, `_dclass`, `_ldclass`, failing `strtod`/`strtof` (P6.4.i3a) |
| `thread.cpp`, `errno.cpp` | `_beginthreadex`, `_endthreadex`; the errno of a Windows error (P6.4.i2) |
| `platform_windows.cpp`, `platform_witos.cpp` | locks, fail-fast, standard handles, files, the heap, the UTC clock, threads; `_errno` on Windows |

`_errno`, `strlen`, the memory routines and `atexit` come from the guest's native layer
(`Runtime.NativeAot/crt_config.witos.cpp`, `crt_memory.witos.c`, `crt_exit.witos.cpp`); `__chkstk` and `_tls_index`
were there before. The NativeAOT overlay defines its own `_fltused` in `native_math.witos.cpp`; a module links one.

### Contract

- An invalid parameter ends the process, as UCRT's default invalid-parameter handler does; no handler can be
  installed. So does everything the subset does not implement: floating-point conversions, `%Z`, single-category
  locales, `%z`/`%Z` in `wcsftime` (there is no time zone), reading and update modes, buffering or closing a standard
  stream.
- Locales: `_create_locale(LC_ALL, ...)` accepts `"C"` and the code-page-only UTF-8 names (`.utf8`, `.utf-8` in any
  case); other names return null, where UCRT would create them. The global locale is always C.
- A module's startup calls `wit_crt_initialize_stdio_options`, which sets what a UCRT module's startup sets: legacy
  wide specifiers and standard rounding for printf (`0x24`) and legacy wide specifiers for scanf, in the module's own
  `__local_stdio_*_options` storage. The host's startup (P6.4.k) must call it.
- The standard streams are unbuffered: each call writes its bytes when it ends, so nothing waits for an exit-time
  flush. UCRT buffers standard output to files and pipes; the bytes are the same.
- In the guest, standard output and standard error go to the process console, `malloc` uses a separate C family of
  the native heap (a block is freed only by its own family), there is no UTC clock (`_time64` returns -1, as C
  specifies for an unavailable time) and storage is read-only: opening a file for writing, removing and renaming
  report `EACCES`.

### Evidence

- `CrtTests.WitOsSubsetMatchesUcrtTest` builds `tests/User.X64/crt_scenarios.cpp` twice: with UCRT, and with the
  subset and no C runtime over kernel32. The scenarios call the public functions and the inline functions of UCRT's
  headers, as the host compiles them. Both builds print `NativeCrtImage.WINDOWS_TRACE` and byte-identical standard
  output and standard error; kernel32 is the subset build's only import.
- `CrtTests.WitOsSubsetMatchesUcrtDifferentiallyTest` compiles the subset with `WITCRT_REFERENCE`, which leaves its
  exported names out, and calls it next to UCRT in one process
  (`tests/WitOS.Dev.Tests/Native/CrtDifferential.cpp`): generated printf specifications over every option set,
  buffer size and limit, both locales and strings of both widths; streams into files in every mode and buffering;
  every code unit through `wcstoul` and `_wtoi`; comparisons, messages, `_gmtime64_s` and `wcsftime` over 40,000
  times and every conversion; locale data; two million `ceilf` inputs; the heap. Locally: 8,441,053 comparisons,
  none different; 631,392 cases are not compared because UCRT calls its invalid-parameter handler there. The UCRT
  of the Windows Server 2025 CI runners faults (access violation) or reports `EILSEQ` for a narrow string with a
  precision in the UTF-8 locale, even for `"abc"` or `(null)`, where the local UCRT (Windows 11 build 26200) and the
  subset convert it. The comparison probes for this defect first and, where UCRT has it, does not compare string
  cases with a precision in the UTF-8 locale and counts them; the local run compares them all, the CI run
  7,758,944 cases, none different.
- In the guest, mode 22 of the host runtime fixture (P6.4.i; first of the CoreCLR mapper fixture,
  `tests/User.X64/crt_scenarios_guest.cpp`) runs the same
  scenarios on the subset, the native heap and the process console and compares the trace with `WINDOWS_TRACE`,
  which the tool generates into `crt_trace.h`; it also checks the missing UTC clock and the read-only storage.
  `coreclr-memory` passes `Code.UcrtSubset` and finds the scenarios' console lines in the boot log, in both
  profiles. The mapper fixture then had 398 unwind entries, so the runtime profile's quota grew from 320 to 512
  (`WIT_PE_RUNTIME_UNWIND_ENTRIES`, `WIT_PE_MAX_UNWIND_RANGES`); P6.4.i moved the scenarios into their own fixture
  under the full runtime profile and returned the quota to 320.

### What the comparison established about UCRT

The first versions differed from UCRT in every area; each difference became a rule of the subset:

- **Malformed formats.** The non-secure functions write an unknown conversion character alone, `%` for `%%` with
  modifiers, and nothing for a specification the format ends in. The secure ones call the invalid-parameter handler,
  but only for the part of the format they reach: they may write one character more than allowed before the
  terminator and stop at the next one, so a malformed specification after that is never parsed. `%n` and the `w`,
  `L` and `T` lengths on integers are invalid in both; lengths on `%p` are ignored.
- **Buffers.** With a buffer and a count of 0, nothing is formatted (-1) unless the standard snprintf contract asks
  for the length. A full buffer decides the result before a failure does; the ISO contract reports a short buffer
  as -2, which the headers turn into -1. A failure keeps the characters before it, except under the standard
  snprintf contract and in `_vsnwprintf_s`, where it empties the string; `_vsnwprintf_s` with a limit not shorter
  than the buffer terminates the buffer's last element first.
- **Characters.** A narrow character the locale cannot convert writes nothing, sets `EILSEQ` and the call goes on;
  a narrow string it cannot convert fails the call.
- **Streams.** Text mode writes each LF as CRLF. Formatted output in the C locale writes `?` for characters above
  U+00FF, while `fputwc` fails with `EILSEQ`; the UTF-8 locale drops surrogates, even paired ones; binary mode
  writes UTF-16LE code units. A written U+FFFF is taken for `WEOF` and fails the call, except where the C locale
  wrote `?` for it.
- **Parsing.** White space is what Windows classifies as such (including U+0085, U+00A0, U+180E, U+2000–U+200A and
  U+3000); digits include 17 Unicode decimal ranges besides ASCII, also in a `0x` prefix; a prefix without hex
  digits converts nothing; `_wtoi` reports `ERANGE`.
- **Time.** `_gmtime64_s` accepts -43,200 to 32,536,850,399 and reports Thursday for the last half day of 1969.
  `wcsftime` in the C locale: `#` strips leading zeros except in `%G`/`%g`, has no effect on `%X` and selects the
  long forms of `%c` and `%x`. Literal text and names may fill the last slot of the buffer, a number needs room for
  the terminator, and a number under `#` keeps its last digits when it does not fit.
- **Locales and messages.** The UTF-8 locale has `MB_CUR_MAX` 4 and code page 65001; its ctype table adds the
  defined and alphabetic classes and marks the lead bytes C2–F4. `_wcserror_s` has messages for 0–42 and 100–140.

### Deviations

- A narrow string the locale cannot convert fails with `EILSEQ` as in UCRT, but before its conversion writes
  anything: UCRT writes padding and characters first, and in a short buffer may report its truncation result. The
  comparison checks only the failure for such strings.
- A width, precision or count beyond `INT_MAX` fails with -1; UCRT's count wraps.
- The locales and unimplemented features above.

## P6.4.i: microsoft/STL

The separately compiled STL sources the host needs form a closure of 36 files. They split by what they call:

- **i1, exceptions and algorithms:** `xthrow`, `thread0`, `syserror`, `syserror_import_lib`, `uncaught_exception`,
  `vector_algorithms`. They need `__uncaught_exception`, `__isa_available`/`__isa_enabled`, `_invoke_watson`,
  `abort`, `memchr`, `FormatMessageA`, `LocalFree` and `GetLocaleInfoEx` beyond what the guest had.
- **i2, threads and synchronization:** `cond`, `mutex`, `cthread`, `xnotify`, `xtime`. They need SRW locks and
  condition variables, `_beginthreadex`/`_endthreadex`, thread waits and exit codes, `GetNativeSystemInfo`, QPC/QPF,
  the precise system time (the guest has no UTC clock), `fputc`/`fputs` and `calloc`.
- **i3, locales and streams:** `locale0`, `locale`, `wlocale`, `xlocale`, `ios`, `iosptrs`, `xlock`, `xmtx`, the
  ctype, collation and conversion helpers and the `xsto*` parsers. They need UCRT's locale internals (`setlocale`,
  `localeconv`, `___lc_*`, `__pctype_func`, the time names, `_Strftime`/`_Wcsftime`), floating-point parsing, the NLS
  functions, critical sections and `EncodePointer`/`DecodePointer`.

### Pin and build

`src/Runtime.Cxx/stl.lock.json` names the tag, the commit `1f6e5b16`, the license and the SHA-256 of the canonical
bytes of 182 files: `LICENSE.txt`, `NOTICE.txt`, the 174 headers of `stl/inc` and the six sources of i1; each later
slice adds its sources (189 files with i2). `StlSources` downloads them at the commit into `.tools/stl/<commit>` and verifies every file
on every use; no STL file is in the repository (`src/Runtime.Cxx/THIRD-PARTY-NOTICES.md`). The sources compile
unchanged with the options of the STL's own static-library build (`/std:c++latest /permissive- /Zc:preprocessor
/Zc:threadSafeInit- /Gy /Zp8 /EHsc`, `_CRTBLD`, `_VCRT_ALLOW_INTERNALS`, `_ITERATOR_DEBUG_LEVEL=0`; completed in
P6.4.i2); i1 needed no upstream patch.

- **The host compiles against the pinned headers.** Headers and separately compiled sources must be one version:
  the 14.51 toolset's headers already call `__std_find_first_not_of_trivial_pos_2`, which neither the pinned tag nor
  the 14.44 toolset has. Everything that sees STL headers puts the pinned `stl/inc` ahead of the toolset's include
  directory; vcruntime's and UCRT's headers still come from the toolset and the SDK.
- **`internal_shared.h`.** The STL's sources include vcruntime's closed header of that name. WitOS's own
  `src/Runtime.Cxx/stl/internal_shared.h` gives them what they use: `Windows.h`, `malloc.h` and the CRT's internal
  allocation names, mapped to the UCRT subset's `malloc`, `calloc`, `realloc` and `free`.
- **Processor level.** `__isa_available` and `__isa_enabled` (`Runtime.Cxx/X64/isa.cpp`) choose the vectorized
  algorithms' paths. They hold SSE2 until a module's startup calls `wit_cxx_initialize_isa`, which detects SSE4.2 and,
  only with OSXSAVE and XCR0's YMM state, AVX and AVX2 with BMI1 and BMI2. The kernel keeps OSXSAVE clear, so the
  guest runs SSE4.2 paths at most and never executes `XGETBV`. The host's startup must call it.
- **Runtime pieces.** `__uncaught_exception` (`throw.cpp`) serves `std::uncaught_exception`; `abort` and
  `_invoke_watson` end the process like UCRT's defaults (`Runtime.Crt/runtime.cpp`); the guest's `memchr`
  (`crt_memory.witos.c`) reads byte by byte and stops at the first match.
- **Win32.** `Runtime.NativeAot/native_stl.witos.cpp` with its bindings `Runtime.Pal.Win32/X64/native_stl.asm` holds
  the Win32 functions only the STL's sources call. `FormatMessageA` is built over the wide `FormatMessageW`, as in
  Windows: the wide call validates the request in its own order and formats into a buffer it allocates, which the
  ANSI form narrows (the catalogue is ASCII) into the caller's buffer or a new one that `LocalFree` frees.
  `GetLocaleInfoEx` fails with `ERROR_NOT_SUPPORTED`, since the guest has no locale database: `system_category()`
  asks for en-US messages first, then the system language through `GetLocaleInfoEx`, then the neutral one. Its
  messages are therefore the guest's own texts, and codes outside the catalogue give the STL's `unknown error`. The
  pair stays out of `native_diagnostics.witos.cpp`: that file is part of the NativeAOT runtime archive, whose probe
  images load under the default profile's 128 unwind entries, and the first version there pushed the thread and
  COM probes to 130 and 131 (the COM probe uses 127).

### Evidence

- `StlTests.PinnedStlMatchesMsvcpTest` builds `tests/User.X64/stl_scenarios.cpp` twice: with the toolset's STL,
  msvcp140, vcruntime and UCRT, and with the pinned headers and sources, the WitOS C++ runtime, the UCRT subset and
  the guest's memory routines over kernel32 only (`memcpy` is the guest's `memmove` there, since the guest's own
  `memcpy` shares its source with a second `_errno`). Both print `NativeStlImage.WINDOWS_TRACE`: the exceptions and
  messages of the throw helpers (`_Xlength_error`, `_Xout_of_range`, `_Xinvalid_argument`, `_Xoverflow_error`,
  `_Xruntime_error`, `_Xbad_alloc`, `_Xbad_function_call`, `_Throw_Cpp_error`), `generic_category` messages, the
  mapping of Windows errors to generic conditions, `std::uncaught_exception` during unwinding, and the vectorized
  algorithms (`find`, `count`, `mismatch`, `min_element`/`max_element`, `search`, `find_end`, `find_first_of`,
  `adjacent_find`, `reverse`) and string searches (`find_last_of`, `find_first_not_of`, `find_first_of`, `rfind`,
  `find`) for elements of one, two, four and eight bytes against plain loops over random data: 12,789 and 4,000
  checks, none different, and a hash of the results. On Windows the WitOS build runs the AVX2 paths where the
  processor has them. Two more tests check the lock: it covers the sources and license files, and a changed tag,
  license, commit, path or hash is rejected.
- In the guest, the C++, UCRT and STL scenarios moved into their own image, the host runtime fixture
  (`HostRuntimeImage`, `tests/User.X64/host_runtime_main.cpp`): the C++ runtime, the UCRT subset, the six STL
  sources and the guest's native support in 225 KiB with 589 unwind entries, which the kernel loads with the full
  runtime profile, as it will load the host. Mode 21 (C++), mode 22 (UCRT) and the new mode 23 (STL) run there; the
  CoreCLR mapper fixture is back to 163 entries, so the runtime profile's quota (`WIT_PE_RUNTIME_UNWIND_ENTRIES`,
  `WIT_PE_MAX_UNWIND_RANGES`) is 320 again. Mode 23 (`tests/User.X64/stl_scenarios_guest.cpp`) compares the trace
  with `WINDOWS_TRACE`, which the tool generates into `stl_trace.h`, and checks `system_category()` messages from the
  guest's catalogue through `FormatMessageA`'s allocated buffer and `LocalFree`. It also calls `FormatMessageA`
  directly: a buffer of the exact size keeps the previous last error, one character less, a missing buffer, an
  unsupported request and another language fail with the wide form's errors, and `GetLocaleInfoEx` fails without
  writing. `coreclr-memory` passes
  `Code.StlSupport` in both profiles: the 128 MiB boot runs on `qemu64`, whose SSE2 leaves the scalar paths
  (`[STL-ISA] 1`), and the 512 MiB boot on `max`, which advertises AVX and AVX2 and runs the SSE4.2 paths
  (`[STL-ISA] 2`); `BootValidation` checks the level against the CPU model.

### Limitations

The SSE4.2 paths run only in the guest and the AVX2 paths only on Windows. `system_category()` messages are WitOS's
own texts, not Windows'. The NativeAOT COM probe is one unwind entry below the default profile's quota, so later
Win32 adapters that its platform objects would carry need their own files or a quota decision.

## P6.4.i2: threads and synchronization

`cond.cpp`, `mutex.cpp`, `cthread.cpp`, `xnotify.cpp` and `xtime.cpp` join the build; with their internal headers
`primitives.hpp` and `awint.hpp` the lock now covers 189 files. They implement `std::mutex`, `std::recursive_mutex`,
`std::condition_variable` and `std::thread` over SRW locks, condition variables and `_beginthreadex`, the legacy C
functions `_Mtx_*`, `_Cnd_*` and `_Thrd_*`, notification at thread exit and the clocks.

### The STL's own build options

i1 compiled with a subset of the STL's options. The sources now get the options of the STL's x64 release static
library, libcpmt, from its CMake files: also `/Os /fastfail /guard:cf`, `/w14265 /w15038` and the definitions
`_AMD64_`, `WIN32_LEAN_AND_MEAN`, `STRICT`, `_CRT_STDIO_ARBITRARY_WIDE_SPECIFIERS`, `_WIN32_WINNT=0x0A00` and
`NTDDI_VERSION=NTDDI_WIN10_NI`. Three differences are deliberate:

- **Windows level.** The x64 build sets `_STL_WIN32_WINNT` and `_VCRT_WIN32_WINNT` to XP and looks up newer functions
  at run time through `winapisupp.cpp` and `GetProcAddress`. WitOS sets Windows 10, as the STL's own ARM64 build does,
  so the sources call the functions WitOS provides directly: `xtime.cpp` calls `GetSystemTimePreciseAsFileTime`
  instead of `__crtGetSystemTimePreciseAsFileTime`.
- **No GS cookie**, which the hosted builds have no runtime for, as before.
- **No `_ANNOTATE_STL`**: no AddressSanitizer annotations, which would need the STL's ASan objects.

### Guest synchronization

The kernel has events with deadlines, not waits on an address, and Windows' SRW locks and condition variables are
one pointer each with no initialization or destruction call. `native_stl.witos.cpp` implements them with a parking
lot:

- A thread that must wait queues a record on its own stack with the address it waits on and an auto-reset event,
  under one native gate, releases the gate and waits on the event. The thread that wakes it dequeues it, marks it
  woken and sets the event under the gate. The gate is never held across a wait.
- Events come from a pool: a thread takes an idle one or creates one, and returns it unsignaled after the wait. When a
  timed wait ends just as a wake arrives, the waiter clears the late signal first. At most one event per thread is in
  use (`WIT_NATIVE_PARKING_EVENTS`, four), and a thread that must park when no event can be created ends the
  component.
- An SRW lock word holds a lock bit and a contended bit, which changes only under the gate. The fast paths take and
  release the lock with one compare-exchange; a contender sets the contended bit and parks, and the release of a
  contended lock wakes the first parked thread, which tries again, so a new arrival may take the lock first, as on
  Windows. Only exclusive mode exists; releasing a lock that is not held, or finding a shared-mode bit, ends the
  process.
- `SleepConditionVariableSRW` queues the thread on the variable's address before it releases the lock, so a wake
  that follows the release finds it. A wait ends at a wake or at its timeout with `ERROR_TIMEOUT`, and the lock is
  held again either way. `CONDITION_VARIABLE_LOCKMODE_SHARED` fails with `ERROR_NOT_SUPPORTED`. The variable's own
  memory is never written.

The same file adds `GetExitCodeThread` (from the kernel's thread reference: `STILL_ACTIVE` until the thread exits),
`GetNativeSystemInfo` (the kernel's processors and page size, the reservation alignment and the dynamic arenas as the
application range, and level and revision from CPUID), `SwitchToThread` (nonzero only when another thread ran) and
`GetSystemTimePreciseAsFileTime`, which ends the process: the guest has no UTC clock and the Windows function cannot
fail. `std::chrono::system_clock` therefore ends a guest component instead of inventing a time; the steady clock
uses the kernel's monotonic counter.

### C runtime

- `_beginthreadex` and `_endthreadex` (`thread.cpp`) over the platform's `CreateThread`; the procedure's signature is
  the start routine's on x64, so no per-thread block wraps it, and `_endthreadex` ends the thread through the guest's
  thread exit with its TLS, runtime and library notifications. A failure maps the platform error to errno.
- `errno.cpp` holds UCRT's mapping of Windows errors, shared by both platforms. The subset's earlier table mapped
  `ERROR_INVALID_NAME` to `ENOENT`; UCRT reports `EINVAL`, which the differential now checks with a name Windows
  rejects.
- `calloc` zeroes through the platform (`HEAP_ZERO_MEMORY` on Windows) and rejects a product beyond `_HEAP_MAXREQ`
  with `ENOMEM`; `fputc` writes the low byte and `fputs` the string in the stream's mode, both returning UCRT's
  values.

### Evidence

- `StlTests.PinnedStlMatchesMsvcpTest` now also runs threads: a sum and identities, a mutex three threads contend for
  while they yield holding it, a producer and two consumers on one condition variable, `notify_all` for three
  waiters, `try_lock` while another thread holds the mutex, a recursive mutex, relocking that throws
  `resource_deadlock_would_occur`, `_Thrd_create`/`_Thrd_join` with exit codes and `_Thrd_exit`, the steady clock,
  yielding until another thread ran, and `notify_all_at_thread_exit` of a detached thread. msvcp140 and the pinned
  sources on the WitOS runtimes over kernel32 print the same trace.
- `CrtTests` compare `calloc`, `fputc` and `fputs` with UCRT in the scenarios and in the differential, which also
  writes them to files in every mode and buffering.
- In the guest, mode 23 runs the same scenarios on the parking lot and the guest's threads and then checks the
  guest functions directly: timed waits that nothing wakes, shared mode, twenty short timeouts before a wait that
  another thread wakes, `GetExitCodeThread` while the thread runs and after it ended, `_beginthreadex` with what the
  guest's `CreateThread` rejects (errno `EINVAL`), `SwitchToThread` with a ready thread and `GetNativeSystemInfo`.
  The kernel checks the component's counters: the threads parked and were woken on events, timed waits timed out,
  and the main thread started exactly eighteen threads (`[STL-PARKING] parks=217 wakes=203 timeouts=22 threads=19` at
  128 MiB). Mode 24 calls `system_clock::now()` and must end with the native fail-fast exit
  (`Code.StlNoUtcClock`). The host runtime fixture now starts as a module does, with
  `wit_native_tls_initialize`, and links the guest's thread lifecycle and Win32 adapters for events, waits, handles,
  sleeping, threads and clocks: 256 KiB with 699 unwind entries.

### Limitations

Shared SRW mode, `std::shared_mutex`, `sleep_for` and `wait_for` (`sharedmutex.cpp`, which the host does not link)
and `std::call_once` are not built. A component has four threads, so at most four can park at once. Locales and
streams (i3) are next.

## P6.4.i3a: the C runtime under the STL's locales

The STL's locale and stream sources (i3) form a closure of 28 files: `locale0`, `locale`, `wlocale`, `xlocale`, `ios`,
`iosptrs`, `xlock`, `xmtx`, the ctype, case, collation and conversion helpers and the `xsto*` parsers. Three of them
(`StlCompareStringA`, `StlLCMapStringA`, `xwcsxfrm`) use owners of CRT allocations from the closed `internal_shared.h`;
WitOS's header now defines `__crt_unique_heap_ptr`, `__crt_scoped_stack_ptr` (always a heap block here) and
`_malloc_crt_t`/`_malloca_crt_t`. Beyond the Win32 NLS functions, critical sections and `EncodePointer`, which come
with the sources in i3b, they call 33 UCRT functions the subset did not have. i3a adds them, each checked against
UCRT:

- **Narrow printf.** The printf engine is a template over the character type. `__stdio_common_vsprintf_s` follows
  UCRT's narrow rules: `%s` and `%c` take narrow arguments, `l`, `w`, `%S` and `%C` wide ones, which the locale
  converts as `wcrtomb` does. The precision and width of a wide string count wide characters; the padding goes out
  first and the characters are converted in order as they are written, a surrogate pair together in the UTF-8 locale
  (a lone high surrogate at the end writes nothing). An unconvertible character fails the call with `EILSEQ` after
  what came before it, and `sprintf_s` then ends the written text and empties the string. The STL formats integers
  (`%[+][#]{l|I64}{d|u|o|x|X}`) and pointers this way; floating-point conversions still end the process.
- **The global locale.** `setlocale` reports the C locale and sets only it (other names return null, as for
  `_create_locale`); `localeconv` gives the C conventions with their wide fields; `___mb_cur_max_func`,
  `___lc_codepage_func`, `___lc_collate_cp_func`, `___lc_locale_name_func`, `__pctype_func` and
  `_lock_locales`/`_unlock_locales` answer for it.
- **Time names.** `_Getdays`, `_Getmonths` and their wide forms list `:Sun:Sunday:...` and `:Jan:January:...`.
  `_Gettnames` hands out the subset's own record, which only `_Strftime` and `_Wcsftime` read: with a record, UCRT takes
  `%c` (`%m/%d/%y %H:%M:%S`) and `%r` (`%H:%M:%S`, which `#` does not change) from its Windows formats, unlike
  `wcsftime` in the C locale. `_Strftime`, like UCRT's `strftime`, formats the widened format and narrows the result.
- **Characters and strings.** `islower`, `isupper` and `isspace` read the C locale's table for -1 to 255 (UCRT reads
  past it beyond that range; the subset answers 0); `__strncnt`, `wcsnlen`, `strcspn` and `_wcsdup`.
- **Numbers.** `frexp`, `_dclass` and `_ldclass` exactly as UCRT (infinities and NaNs give the exponent -1 and a
  quiet NaN). `strtod` and `strtof`, which the STL's `num_get` for floating point calls, end the process, like the
  floating-point conversions of printf: the host parses no floating-point text through streams.

The differential calls each against UCRT: every printf case of the wide matrix also through the narrow `sprintf_s`,
`_Strftime` and `_Wcsftime` with UCRT's names record over the time cases, the global locale's queries, the classes
for -1 to 255, 200,000 string-helper cases and a million `frexp`/`_dclass` inputs: 12,594,710 comparisons, none
different. The scenario trace gains these functions (`n1`–`n11`), which the hosted build and the guest print as UCRT
does.

## P6.4.i3b: locales and streams

i3b builds the 28 locale and stream sources of the pinned STL with the Win32 functions they call, and the host's
`std::wstringstream` works: on Windows over kernel32 with the trace msvcp140 prints, and in the guest.

### Sources and startup

The pin grows to 219 files: the 28 sources and the two private headers they include (`init_locks.hpp`, `xmtx.hpp`),
all compiled unchanged with the STL's own options. `xlock`, `iosptrs`, `locale0` and `locale` create their locks and
the classic locale's objects in static initializers, which `#pragma init_seg(compiler)` and `init_seg(lib)` place in
the CRT's initializer sections. The WitOS C++ runtime now runs them as vcruntime's startup does:
`src/Runtime.Cxx/startup.cpp` brackets the C initializers (`.CRT$XIA`–`XIZ`, which return a failure code) and the
C++ ones (`.CRT$XCA`–`XCZ`) with markers, and `wit_cxx_run_initializers` calls the C initializers, stopping at the
first failure, then the C++ ones, skipping the zeros the linker may pad between contributions. A module's startup
calls it once, after the GS cookies and its compiler TLS and before its own code: the host runtime fixture now sets
the cookies itself (`cxx_exceptions_guest.cpp` did before), and the hosted WitOS build's entry calls it too. A pure
virtual call (`_purecall`) ends the process, as vcruntime does without a handler.

### Win32 under the sources

On Windows the sources call kernel32. In the guest, `native_stl` gains the functions they import:

- **Critical sections.** `InitializeCriticalSectionEx`, `EnterCriticalSection`, `LeaveCriticalSection` and
  `DeleteCriticalSection`, which the STL's locks use: an exclusive SRW lock of the parking lot (i2) in the
  `LockSemaphore` field, the owner's thread identifier and a recursion count. Initialization fills the fields as
  Windows does (`LockCount` −1, `DebugInfo` −1, the spin count kept) and rejects flags outside
  `RTL_CRITICAL_SECTION_FLAG_*` with `ERROR_INVALID_PARAMETER`; leaving a section the thread does not own, or deleting
  one that is held, ends the process.
- **`EncodePointer` and `DecodePointer`**, with which `iosptrs` keeps its pointers: Windows' scheme, the pointer XORed
  with a per-process secret and rotated right by its low six bits, the secret drawn once from the kernel's random
  source.
- **`GetStringTypeW`** for `CT_CTYPE1`, which `ctype<wchar_t>::is` calls for every character: the classes of
  U+0000–U+00FF from a table (`native_ctype.witos.h`) that a host test compares with Windows character by character.
  The guest has no Unicode character database: a character beyond U+00FF ends the process rather than get a wrong
  class, and `CT_CTYPE2`/`CT_CTYPE3` fail with `ERROR_NOT_SUPPORTED`. Count −1 includes the terminator, and a zero
  count, null pointers or an unknown type fail as on Windows.
- **`GetCPInfo`** for UTF-8 and the ANSI code pages, all UTF-8 in the guest (`MaxCharSize` 4, default `?`), and
  `MultiByteToWideChar`/`WideCharToMultiByte` from the existing encoding adapter.
- **`CompareStringEx` and `LCMapStringEx`** fail with `ERROR_NOT_SUPPORTED`: the STL calls them only for a named
  locale, and the C locale's collation compares code units.

### C runtime

The STL's `_Lockit(_LOCK_LOCALE)` takes UCRT's locale lock again while holding it, so the subset's lock is now
recursive, as UCRT's is: the owner is the thread's identity (generation-bearing in the guest) with a depth, and an
unlock by any other thread ends the process. The number facets also call `_dtest`, `_ldtest`, `fabs`, `abs` and
`llabs`, which the subset now has exactly as UCRT; the differential compares them over the same million inputs as
`frexp` and the extremes of `int` and `long long`: 14,594,751 comparisons, none different.

### The native heap

Copying the classic locale, which `imbue` with a replaced facet does, creates every facet with its strings at once.
That exceeded the 128 live blocks of the bootstrap native heap, and the guest threw `bad_alloc` where Windows
succeeded. The heap now allocates from pages of 21 size classes and runs of whole pages, with descriptors in their own
pages ahead of a guard page, and a component loaded with the full runtime profile gets a 4 MiB arena and 65,536 live
blocks while every other component keeps the bounds its tests exhaust; see
[ADR 0013](NativeAot-Native-Heap.md). The profile is the one the kernel reports through the reservation capacity,
not the owned limit, which the GC initialization-failure test of `runtime-boot` lowers after admission. That test's
budget and the interface-dispatch probe's committed pages follow the new layout (the ADR gives the numbers). The host
runtime fixture checks the large heap in its own mode (`Code.NativeHeapLarge`).

### Evidence

- `StlTests.PinnedStlMatchesMsvcpTest` now also runs streams and locales (`w1`–`w10`): splitting a wide string with
  `getline` as the host does, integers through `ostringstream` in every base with `showbase`, `uppercase` and
  `showpos`, parsing in mixed bases, `wistringstream` extraction across whitespace, `boolalpha` both ways, the fail
  state of bad and overflowing input, widths, fills and adjustment, the classic locale's facets and character classes
  (including U+00C9 and U+00AD), collation, `numpunct`, and a locale with a replaced `numpunct` imbued for grouped
  output and input. msvcp140 and the pinned sources on the WitOS runtimes over kernel32 print the same trace, and
  kernel32 stays the only import.
- `StlTests.GuestCharacterClassesMatchWindowsTest` compares the guest's `CT_CTYPE1` table with `GetStringTypeW` for
  all 256 characters.
- `CrtTests` cover the recursive locale lock and the new numeric functions.
- In the guest, mode 23 prints the same trace on both CPU models and both memory profiles, and mode 25 checks the
  large heap. The host runtime fixture is 520 KiB with 1,278 unwind entries.

### Limitations

Only the C locale exists: `setlocale` and the STL's named locales fail, and with them `CompareStringEx` and
`LCMapStringEx`. Character classes end at U+00FF, so a stream that classifies text beyond Latin-1, as extraction
skipping whitespace does, ends the process; `getline` with a delimiter does not classify. Floating-point input and
output through streams end the process (`strtod`, printf's floating-point conversions). `iomanip.cpp` (`std::setw`
and the other manipulators with arguments) and `iostream.cpp` (the standard stream objects) are not built: the host
links neither. Next is P6.4.j, the host's own Win32 imports.

## P6.4.j: the host for the guest

The real host is three images on Windows: `dotnet.exe` resolves and loads `hostfxr.dll`, which reads the
application's runtimeconfig, resolves the framework and loads that framework's `hostpolicy.dll`, which reads the deps
and loads `coreclr.dll`. Their code is upstream's, unchanged; below them sits the hosting PAL (`pal::` in
`hostmisc/pal.h`), whose Windows implementation, `pal.windows.cpp`, is where 40 of the 42 Win32 imports of the old
diagnostic link came from. WitOS keeps upstream's sources and policy (`fx_muxer`, `fx_resolver`, `deps_resolver`)
and replaces only that file with its own PAL objects over the guest's native backends, compiled against the same
pinned `pal.h` (with the one recorded correction, `munmap` as a declaration).

### P6.4.j1: the guest build and its inventory

`coreclr-host-guest` takes the corehost sources from the runtime checkout that `runtime-source` verifies (pinned
commit, clean tree) and copies them, with `rapidjson` and the headers they include, into its build directory: sources
in `hostmisc` include `pal.h` from their own directory first, so the corrected header replaces the copy, after a check
that the checkout's `pal.h` equals the hash-verified pin. It writes the two version headers upstream's MSBuild
generates (product 10.0.8 with the pinned commit, Arcade's local-build file version 42.42.42.42424, as the reference
build has). The sources are those upstream's CMake lists for Windows: `hostmisc` without `pal.windows.cpp` and
`longfile.windows.cpp` (only that PAL uses the long-path helpers), `libhostcommon`, and each library's own. They compile
with upstream's code generation and warning policy (`/O2 /GS /EHsc /GR- /guard:cf /guard:ehcont`, `/W4 /WX` with its
exceptions), except that no default library is named (`/Zl` instead of `-MT`), and against the pinned STL's headers
ahead of the toolset's. WitOS's PAL objects compile against the same headers.

Each library links strictly (`/NODEFAULTLIB`) over the WitOS C++ runtime, the UCRT subset, the STL's sources, the
guest's native support and the Win32 adapters, as the host runtime fixture does, and the x64 stack probe. The
command compares each image's unresolved externals with `experiments/CoreClrHost/guest-link.json` and fails on any
difference, in either direction: a slice that implements something removes it from the file, and nothing returns
unnoticed. The inventory is 27 symbols for each image, not the 119 of the old link: the C++ runtime, the UCRT subset
and the STL closed everything else.

| Group | hostfxr | hostpolicy |
|---|---|---|
| `pal::` functions without a WitOS implementation | 20 | 18 |
| Win32: environment (`GetEnvironmentVariableW`, `GetEnvironmentStringsW`, `FreeEnvironmentStringsW`) | 3 | 3 |
| Win32: `GetCurrentProcessId`, `OutputDebugStringW` (`pal.h` inline, `trace.cpp`) | 2 | 2 |
| Win32: `CreateDirectoryW`, `RemoveDirectoryW` (`pal.h` inline, bundle extraction) | 0 | 2 |
| `atexit`, `wit_native_main` (a library's startup) | 2 | 2 |

`wit_native_main` comes from the process entry, linked for the system-call primitives in the same object.

The zlib inflate functions of `bundle/extractor.cpp` are compiled only for the single-file host
(`NATIVE_LIBS_EMBEDDED`) and are not needed.

The environment adapters exist (`native_environment.asm` over the PAL environment), but they are not linked: their
state is the table the process entry publishes from its own image, and a library linked with its own copy would see
none. That is the design question of j3. A module statically links its runtimes, as upstream's host does with the
static CRT, so each has its own heap and C++ state, which the hosting interfaces allow (strings cross them as
borrowed pointers). Process state cannot be per module: the environment, the console and the current directory must
be the process's. The loader resolves a library's imports only to siblings in its own directory, and `hostfxr.dll`
and `hostpolicy.dll` live in different directories, so a shared system library is not the answer; the kernel must
hand that state to every module. `coreclr.dll` is a separate module under any composition of the host, so the
question does not go away with a static host.

### P6.4.j2: the rest of the PAL

Three more PAL objects implement the 17 functions that need no new kernel interface, and a host-only adapter
(`native_host.witos.cpp` with its Win32 binding, apart from the NativeAOT archive like `native_stl`) the four Win32
functions upstream's `pal.h` and `trace.cpp` call directly. The inventory falls to 8 symbols for `hostfxr` and 6 for
`hostpolicy`, all of them j3's.

- **Strings** (`host_strings.witos.cpp`): `pal_utf8string`, `pal_clrstring`, `clr_palstring` convert as the Windows
  PAL does, through `WideCharToMultiByte`/`MultiByteToWideChar`, which in the guest are the native encoding adapter's:
  lengths include the terminator, a buffer too small gets nothing and the size it needs, ill-formed UTF-16 becomes
  U+FFFD, and an empty input to `clr_palstring` fails, as on Windows. `xtoi` is the UCRT subset's `_wtoi`.
- **Output** (`host_trace.witos.cpp`): `file_vprintf` formats with a UTF-8 locale into the C runtime's stream, as the
  Windows PAL does. Its `err_print_line` and `out_vprint_line` write UTF-16 to a Windows console with `WriteConsoleW`
  and otherwise take that stream path; the guest's console is no Windows console, so they always take the stream
  path, which writes the bytes the Windows PAL writes when its output is redirected (UCRT converts wide output to the
  locale's characters only in text mode, which the standard streams and the trace file have). `get_timestamp`
  formats UTC as Windows does; the guest has no UTC clock, so it returns `(no UTC clock)` rather than an invented time.
- **Installation and queries** (`host_install.witos.cpp`): the Windows PAL reads Program Files, ProgramData and the
  registry, which the guest has none of, so the guest follows the Unix PAL over the immutable package:
  - default installation directory `/` (the boot package places `shared/Microsoft.NETCore.App` at its root), none for
    another architecture, and no global directories;
  - registration in `/etc/dotnet/install_location_x64`, then `/etc/dotnet/install_location`, first line;
  - servicing from `CORE_SERVICING` when it names a directory, otherwise `/opt/coreservicing` if present;
  - no breadcrumb store, no bundle extraction directory, and `touch_file` fails (`ERROR_FILE_EXISTS` or
    `ERROR_WRITE_PROTECT`, as `CreateFileW` with `CREATE_NEW` reports on write-protected media): nothing can be
    written;
  - runtime identifier platform `witos` (the generated configuration's fallback OS, so RID-specific Windows assets are
    not selected), no WOW64, case-sensitive path equality, and `is_directory` from the package's metadata.
- **Win32 for the host** (`native_host.witos.cpp`): `GetCurrentProcessId` is the kernel's process identifier from the
  thread record; `OutputDebugStringW` discards the string, as Windows does with neither a debugger nor a system
  debugger (no debugger attaches to a guest component); `CreateDirectoryW` reports an existing name as
  `ERROR_ALREADY_EXISTS` and any other as `ERROR_WRITE_PROTECT`, and `RemoveDirectoryW` a missing name as not found and
  any other as write-protected, which is what the bundle code's `pal::mkdir`/`pal::rmdir` see.

`__chkstk` now belongs to the runtimes every guest C++ module links: the PAL's path buffers exceed a page.

Evidence. `coreclr-host-files` checks the objects on Windows with the package's syscall model: the conversions with
supplementary characters and a lone surrogate against the exact UTF-8 bytes, the sized and too-small buffers, `xtoi`,
the formatted line read back from a text-mode stream, the error and output lines, which the harness requires as
UTF-8 on the process's streams, the UTC timestamp, every policy answer, `is_directory`/`touch_file` with their errors
and the servicing directory with and without `CORE_SERVICING`. In the guest, the host runtime fixture's mode 26
publishes its image and an environment (`CORE_SERVICING=/`) before compiler TLS, as the host's startup will, and
checks the same contracts over the guest's adapters, with the guest's own answers (no UTC clock, `/` as servicing),
the four Win32 functions, and lines on the console that the kernel test requires in UTF-8 (`[HOST-PAL-ERR] λ`,
`[HOST-PAL-OUT] λ 7`, with the file compiled as UTF-8 so the markers stand in it as written), at 128 and 512 MiB (`Code.HostPal`). The environment adapter links into that fixture, a
process; the host libraries still leave it unresolved until j3.

### P6.4.j3a: process state in the kernel

A Windows process has one environment block and one current directory, which kernel32 keeps for every module. The
guest's modules link their own copies of the adapters, so that state cannot live in them, and a shared system library
cannot hold it either (the loader resolves imports only to siblings). The kernel keeps it in the component's record
(decided 2026-10-05). One call, `WIT_CALL_PROCESS_STATE` (69, user ABI v50), takes a 64-byte
`WitProcessStateRequest` (`process_state.h`):

- `ENV_GET`, `ENV_SET`, `ENV_BLOCK`: the environment is a block of `Name=Value\0` records in the order they were set,
  with a final `\0`, as `GetEnvironmentStringsW` returns it; names compare with ASCII case folding and hold no `=` or
  NUL, and a variable set again moves to the end. The block holds 4,096 UTF-16 units and 64 variables
  (`limits.h`); beyond that a set is `NO_MEMORY` and changes nothing. Output is copied only whole and the result is
  its size either way, so one call both sizes and fills a buffer.
- `CWD_GET`, `CWD_SET`: the current directory is canonical UTF-8 from `/`, up to 1,025 bytes. Resolving a relative
  path stays in user space (`path.c`); the kernel takes the canonical result and accepts it only when the package
  names a directory (a missing name is `NOT_FOUND`, a file `WRONG_TYPE`).

Each operation runs with interrupts disabled, so it is atomic for the component's threads, and a failure changes
neither state nor output. A component starts with an empty environment and `/`; its creator may set variables before
it runs (`wit_user_environment_set`), as a parent passes an environment to a Windows process.

In user space, `pal_environment.witos.cpp` keeps ADR 0021's PAL contract over the kernel's state
([revision](NativeAot-Pal-Environment.md#revision-the-processs-environment-p64j3a-user-abi-v50)): lookups and blocks
come from the kernel, the image's readonly table seeds only variables the creator did not set, and
`SetEnvironmentVariableW` is new, with the semantics measured on Windows (a null value removes, removing an absent
variable succeeds, success preserves last error, an empty value is a value, an empty name or one with `=` is
`ERROR_INVALID_PARAMETER`). `current_directory.c` reads and sets the kernel's directory instead of a module's copy.

Both adapters now link into `hostfxr` and `hostpolicy` with the other adapters, and the three environment imports
leave the inventory: 5 unresolved symbols remain for `hostfxr` and 3 for `hostpolicy`, the module paths (j3b),
`atexit` and `wit_native_main` (j3c).

The adapter now makes system calls, and `/GS` protects the 64-byte request each call builds on the stack (MSVC
treats a structure of more than 8 bytes without pointers as a buffer), so seeding the environment is GS-checked code
in the runtime archive. The NativeAOT boot driver seeded it before initializing
its cookies, and `runtime-boot-run` ended with the GS failure exit; the driver now initializes the cookies first, as
the host runtime fixture already did.

Copying the environment block made a kernel cost visible: `wit_user_copy_from` and `wit_user_copy_to` translated every
byte through the page tables, and the PAL environment fixture ran out of its ten-tick budget. They now translate once
per page, after the same whole-range validation.

Evidence. In mode 26 of the host runtime fixture the kernel test sets `WITOS_CREATOR=kernel` and
`WITOS_SEEDED=creator` as the creator, and the image's table seeds `CORE_SERVICING=/` and `WITOS_SEEDED=table`. The
guest checks that the creator's value wins and that lookup ignores ASCII case, `SetEnvironmentVariableW` with last
error preserved, the block in setting order with a variable set again at the end, removing twice, the invalid names,
and the current directory through `pal::getcwd` and `wit_native_cwd_set` (a missing directory is `NOT_FOUND` and keeps
`/`). The PAL environment fixture now starts from an empty process environment: lookups before initialization find
nothing, the empty block holds two terminators and a rejected table seeds nothing. The hosted system-call model
(`FileViewFaults.c`, under `coreclr-host-files` and the native tests) answers `CWD_GET`/`CWD_SET`; the file and
directory fixtures of `coreclr-storage` resolve against the kernel's directory. `coreclr-memory` and `coreclr-storage` pass at 128 and 512 MiB, and `coreclr-host-guest`
enforces the smaller inventory.

Limitations. The console is not yet a library's: a library gets no startup descriptor until j3c. Case folding is
ASCII, where Windows folds Unicode, and the adapter keeps ADR 0021's names (printable ASCII, up to 63 units). There
is no process creation, so no child inherits an environment.

### P6.4.j3b: the module at an address

The Windows PAL finds its own module and the module of a function with `GetModuleHandleExW` and an address, and names
it with `GetModuleFileNameW`. `hostfxr` takes the current host's path from the executable and derives the .NET root
from its own module's path (`get_dotnet_root_from_fxr_path`), which it also hands to the SDK; the trace in both
libraries names its file after the executable. In the guest only the kernel knows which module holds an address. `WIT_CALL_LIBRARY`
gains `MODULE_PATH` (13, user ABI v51), which takes no handle: it writes the `WitLibraryPath` of the module whose
image holds the address in `Ordinal`, the component's main image or a loaded library, or with
`WIT_LIBRARY_MAIN_IMAGE` and no address that of the main image. An address in no module image, or a main image that
came from no package file, is `NOT_FOUND`. It is a query of immutable records and is allowed during library
lifecycles too.

A library has always had its package path; a main image created from bytes in the kernel (every fixture so far) has
none, and the kernel does not invent one. `wit_user_create_package_pe` creates a component from a file of the boot
package and records the file's name, as the launcher of the .NET host will.

`host_library_discovery.witos.cpp` implements `get_own_executable_path`, `get_own_module_path` (the module of its own
code) and `get_method_module_path` over that query, with `/` and the package key as the path, the conversion
`get_module_path` already used. No module at the address is `ERROR_MOD_NOT_FOUND`, as `GetModuleHandleExW` reports,
and success keeps the last error. The inventory falls to 2 unresolved symbols for each library, `atexit` and
`wit_native_main`, both j3c's.

Evidence. The hosted reference model answers `MODULE_PATH` with Windows' own `GetModuleHandleExW`, and
`coreclr-host-files` checks the three functions against it: a symbol of the loaded DLL, the harness's own code, and a
stack address with `ERROR_MOD_NOT_FOUND` and the output unchanged. In the guest, the boot package of `coreclr-memory`
carries the host runtime fixture as `host/HostRuntimeFixture.pe`, and mode 26 now runs from it; the guest checks the
executable and own-module paths, its code, its headers and its data, a function and a data export of
`/native/lib.dll` loaded from the package, and after the unload that the library's address and a stack address belong
to no module (`Code.HostPal`, 128 and 512 MiB).

Limitations. Paths are package keys; the guest has no other file system, and no image is loaded from anywhere else.
The NativeAOT runtime's `GetModuleFileNameW` adapter still answers from the single image's boot resource name.

### P6.4.j3c1: a C++ library's startup

An MSVC DLL links the static CRT's DLL startup, `_DllMainCRTStartup`, which prepares the module before `DllMain` and
cleans it up after. The guest's C++ modules link their runtimes the same way, each its own copy, so a library needs the
same startup. `library_startup.cpp` supplies it as the entry point `wit_library_cxx_entry`, around the existing dynamic
TLS entry `wit_library_dll_entry` (P6.4.c), which initializes the loading thread's `thread_local` objects and calls
`DllMain`:

- **Process attach**, in vcruntime's order: the module's GS cookies; the process's context, which the module
  publishes from the immutable startup descriptor at `WIT_USER_INFO`, so that its adapters see the process's main image
  and console as the process entry's do; the STL's processor level and the C runtime's stream options; then the
  module's C and C++ initializers. A failing C initializer fails the load without `DllMain`, after the callbacks
  registered so far ran.
- **atexit** registers with the module, as the static CRT's does in a DLL. Only the module's own code is accepted.
  The table holds 32 callbacks (`WIT_NATIVE_EXIT_MAX_CALLBACKS`), and a module that has detached refuses more.
- **Process detach** runs `DllMain` first, then the module's callbacks, newest first, each popped before it is called.
  A callback may register another within a bound.

A library links what a process links, except three objects: the process entry, the process's compiler TLS
(`tls.witos.cpp`) and its TLS directory (`tls_metadata.c`). In their place it takes the library's dynamic TLS and
startup and the system-call primitives without the entry (`native_start.asm` with `WITOS_NATIVE_TRANSPORT_ONLY`).
`CoreClrMemoryImage.BuildLibrarySupportAsync` builds that set for the test library and for the host.

**Threads.** A library starts no thread yet. A thread's lifecycle belongs to the process entry: the main image's
compiler TLS, the runtime's thread notifications, and the libraries' thread attach around them. A library cannot
reach that lifecycle, so its copy of the thread support accepts no entry point. `CreateThread` fails with
`ERROR_INVALID_ADDRESS`, and the start and exit paths that would follow are unreachable. Threads that start in any
module come with `coreclr.dll`, which needs them.

**Admission.** The kernel admitted libraries only within the default limits: a 256 KiB image and 320 unwind entries.
Linked with the C++ runtime and the STL, the host's libraries exceed both: `hostfxr` has a 432 KiB image and about
1,200 unwind entries. A component with the full runtime profile now validates its libraries with that profile's
limits, 1088 KiB and 4,096 entries. A component with the default profile keeps the default limits.

With this set, `hostfxr` and `hostpolicy` link with no unresolved symbol, and `guest-link.json` is empty.

Evidence. Mode 27 of the host runtime fixture loads `host/cxxlib.dll` (`tests/User.X64/cxx_library.cpp`) from
`coreclr-memory`'s boot package. The kernel test first checks that the file fails the default library profile
(`TooLarge`: 491 unwind entries) and passes the full one. The library is linked as the host's are, with its startup
as the entry point and no import, and the fixture checks:

- its static objects were constructed before the load returned, and the loading thread's `thread_local` was
  initialized;
- the STL, on the module's own heap, and `std::wostringstream` on the locale objects its initializers created;
- `swprintf` from the module's C runtime;
- the process's environment both ways: the library reads a variable the fixture set and sets one the fixture reads;
- `CreateThread` refused;
- a line on the process console through the library's `stdout` (`[CXX-LIBRARY] ready`);
- at the unload, its function-local static, then its two globals, destroyed in that order through its atexit, each
  reporting to a hook in the fixture.

The fixture's own runtimes are separate copies from the library's. `Code.CxxLibrary` passes at 128 and 512 MiB.

Limitations. A C++ exception thrown in a library cannot be dispatched yet. The dispatcher of every module accepts
code and unwinds frames of the main image and of registered dynamic code only, so a library's frames are j3c2's work.
The host libraries link, but they do not run in the guest yet (j3c3).

### P6.4.j3c2: C++ exceptions in a library

A module raises its exceptions through its own copy of the guest dispatcher, which looks functions up and unwinds
frames of the main image, of registered dynamic code and, through the lookup the CoreCLR support added for module
readers, of loaded libraries. Two checks still knew only the first two kinds of code:

- **`code()`** decides which program counters, handlers, targets and continuations the dispatcher accepts. It now
  also accepts the code of a loaded library: `wit_native_library_code` takes a reader on the module that holds the
  address, which keeps it loaded, and checks that the address lies in a section of the module's image that is
  executable and not writable. The image's headers are readonly after the load. A frame of a library that is neither
  main image nor dynamic code no longer ends the search.
- **`call_handler`** called every handler outside the main image through the funclet gate, which closes the unwind
  scope around the call and bridges collided unwinds for CoreCLR's dynamic code. A library's C++ frame handler then
  started its catch's consolidation under that bridge, and `consolidate` refused it. Only registered dynamic code takes
  the gate now; a library's handler is called directly, as the main image's is.

So a C++ exception thrown and caught in one library works, frames of other modules in between included: the
dispatcher calls their handlers in the search and the unwind, and their destructors run.

Evidence. `CxxLibraryExceptions` in mode 27 throws a `std::runtime_error` and catches it in the library. Then it
throws an `int` from a library function that the fixture calls back from a frame of its own, which holds an object
with a destructor. The library's catch receives 5, and the fixture sees its destructor ran once (`Code.CxxLibrary`,
128 and 512 MiB).

Limitations. A catch in a different module than the one that raised the exception is not supported: that module's
`RtlUnwindEx` does not see the dispatch, which is the raising module's own state, so the unwind fails and the component
ends. Windows has one dispatcher in ntdll for every module; a guest equivalent comes when exceptions must cross the
host's modules. Hardware exceptions in a library still go to the one dispatcher the process registered with the
kernel.

### P6.4.j3c3: the host's libraries in the guest

`coreclr-memory` now builds upstream's `hostfxr.dll` and `hostpolicy.dll` for the guest. It uses
`CoreClrHostGuest.BuildAsync`, the build `coreclr-host-guest` inventories, from the checkout `runtime-source`
verifies, so this gate needs that checkout. It fails if either library leaves a symbol unresolved. Its boot package
places the two libraries as a .NET root lays them out: `host/fxr/10.0.8/hostfxr.dll`, then
`shared/Microsoft.NETCore.App/10.0.8/hostpolicy.dll` beside the framework's `Microsoft.NETCore.App.deps.json`.
hostfxr ignores a framework version without that file. The version comes from the runtime pin.

Mode 28 of the host runtime fixture (`tests/User.X64/host_libraries_guest.cpp`, against upstream's `hostfxr.h`)
loads `hostfxr.dll` from there. Its startup runs at the load, with its `thread_local` error writer in the library's
TLS. The fixture then checks:

- `hostfxr_get_dotnet_environment_info("/")` reads the root through WitOS's PAL. It looks for `global.json`, gathers
  the SDK and framework locations, and reports no SDK and one framework, `Microsoft.NETCore.App` 10.0.8, with
  hostfxr's own version 10.0.8.
- With a non-null `reserved`, it fails with upstream's `InvalidArgFailure` (`0x80008081`), and upstream's message
  reaches the writer `hostfxr_set_error_writer` installed.
- `hostpolicy.dll` loads, exports `corehost_main` and unloads.
- `hostfxr.dll` unloads.

`Code.HostLibraries` passes at 128 and 512 MiB. With `COREHOST_TRACE=1` in the process's environment, hostfxr's own
trace reaches the console through WitOS's PAL as on Windows. It begins with `Tracing enabled @ (no UTC clock)` and the
entry point with its version and commit, then reports `Found FX version [10.0.8]`.

The host is compiled for Windows, so it joins paths with `\`. Under the root `/` the framework's path is
`/\shared\Microsoft.NETCore.App`, which the fixture expects exactly. WitOS's paths accept both separators, and
upstream's path joining is not patched. (P6.4.k1 then moved the .NET root to `/dotnet` and made the PAL hand out
`\`-separated paths; see there.)

With this, P6.4.j is complete: upstream's host, unchanged, runs in the guest as libraries of a process. What it does
not do yet is start an application. `hostfxr_main` through `hostpolicy` to `coreclr_initialize` is P6.4.k, and it
needs `coreclr.dll`, which also creates threads in its libraries.

## P6.4.k: the application's startup

### P6.4.k1: `hostfxr_main` up to CoreCLR

`coreclr-storage` delivers the unchanged framework and a portable application, and since k1 it also carries the host:
its boot package installs .NET in `/dotnet`, with the host runtime fixture as the muxer `/dotnet/dotnet`, `hostfxr`
in `/dotnet/host/fxr/10.0.8` and `hostpolicy` beside the framework in `/dotnet/shared/Microsoft.NETCore.App/10.0.8`.
The kernel creates the component from `/dotnet/dotnet`, and mode 29 of the fixture calls `hostfxr_main` with
`/dotnet/dotnet /app/CoreClrProbe.dll`, as `dotnet app.dll` would.

Two corrections came out of the first runs, both about paths:

- **The host's separator.** The host is built for Windows, and its path helpers (`get_directory`, `get_filename`,
  `append_path`) know only `\`. From `/dotnet` it made the root `/dotnet\`, and from the application's path the
  directory `/app/CoreClrProbe.dll\`. On Windows this never arises, because `GetModuleFileNameW` and
  `GetFullPathNameW` return `\`-separated paths. WitOS's PAL now does the same: every path it hands to the host
  (`fullpath`, `realpath`, `getcwd`, module paths, the installation, registration and servicing locations) uses `\`
  between components and `\` as the root, as the Windows API would. WitOS's paths accept both separators on the way
  back, so nothing else changes.
- **The .NET root is a directory.** Upstream drops a root's trailing separator, so a .NET installed at the root of the
  namespace becomes the empty path, on Unix as on Windows, and the framework's path turns relative. The package
  therefore installs .NET in `/dotnet`, as every installation is a directory (`C:\Program Files\dotnet`,
  `/usr/share/dotnet`), and the PAL's default installation directory is `\dotnet`.

Evidence. With them, the real host runs the whole way in the guest, as its trace (`COREHOST_TRACE=1`) shows:

- **hostfxr.** It takes its own path from the kernel (`\dotnet\dotnet`) and selects the muxer. It reads
  `\app\CoreClrProbe.runtimeconfig.json`, rolls `Microsoft.NETCore.App` 10.0.0 forward to the delivered 10.0.8 and
  reads the framework's runtimeconfig. It then loads `hostpolicy.dll` from the framework's directory.
- **hostpolicy.** It reads the application's and the framework's deps and resolves their managed and native assets.
  It then finds no CoreCLR.

`hostfxr_main` returns `CoreClrResolveFailure` (`0x80008087`), and hostpolicy's message, `Could not resolve CoreCLR
path.`, reaches the error writer the fixture installed in hostfxr. hostfxr hands that writer on to hostpolicy.
`Storage.HostfxrMain` passes at 128 and 512 MiB. The storage tests now find the framework under `/dotnet`. Mode 28
reads `\dotnet` and reports the framework at `\dotnet\shared\Microsoft.NETCore.App`.

The framework's deps are Windows', so the native assets they list are the `win-x64` runtime pack's. The boot package
delivers none of them, and the host does not check a framework asset's existence. The guest's own native assets
begin with `coreclr.dll` (k2).

### P6.4.k2: the inventory of the guest's `coreclr.dll`

`coreclr-guest` takes upstream CoreCLR from the reference build that `coreclr-source` makes from the verified
checkout: its 452 inputs are taken unchanged, objects and resources, as the build's link step for `coreclr.dll` in
`build.ninja` lists them, with the module definition file. Upstream's own static libraries stay:
`System.Globalization.Native-Static`, `coreclrminipal`, `gc_pal`, `minipal`. The Windows and CRT libraries upstream
links are left out, and in their place the image links what every WitOS C++ library links:

- the C++ runtime, the UCRT subset and the STL's sources;
- the guest's native support and adapters, as a library links them;
- the library startup as its entry point.

The objects carry link-time code generation, so the reference build's own linker links them, the one `CMakeCache.txt`
names. A different toolset's linker refuses them.

The image's unresolved externals are the inventory, `experiments/CoreClr/guest-link.json`. The command enforces it in
both directions, as `coreclr-host-guest` does the host's. There are 276 symbols. The report counts them by the library
upstream would have taken them from, a library's public symbols as `dumpbin /linkermember:1` lists them:

| Library | Symbols |
|---|---:|
| kernel32 | 124 |
| ucrt (the dynamic UCRT, which upstream links instead of the static one) | 54 |
| oleaut32 | 29 |
| uuid (COM interface identifiers) | 23 |
| advapi32 | 17 |
| ole32 | 17 |
| libvcruntime (`longjmp`, `strchr`, `strrchr`, `wcsrchr`, `wcsstr`) | 5 |
| version | 3 |
| runtimeobject | 2 |
| libcmt (`__report_rangecheckfailure`) | 1 |
| user32 | 1 |

That is the size of the platform CoreCLR asks for beyond what the host needed. The C++ runtime is nearly complete for
it: five string and jump functions and one GS report. Win32 is the bulk. COM, OLE automation and WinRT are a
quarter, and need an explicit decision about what a WitOS process offers rather than adapters that pretend.

Linking is not running. These obstacles stand before the image can load, and each is its own slice:

- `coreclr.dll` is 5.4 MB, beyond the kernel's limits for a PE image (1 MiB of file, 1088 KiB of image).
- Its owned pages would exceed the full profile's 8 MiB before the JIT and the GC heap.
- CoreCLR creates threads, which a library cannot yet.
- It reads the Windows TEB (`NtCurrentTeb`, `ThreadLocalStoragePointer`).
- Its exceptions cross modules, which need one dispatcher for the process.

### P6.4.k3: closing `coreclr.dll`'s inventory

Decided 2026-10-06: CoreCLR stays an honest Windows target. Upstream's sources and configuration are unchanged, and
COM interop stays compiled in. Where a COM, OLE or WinRT function is data or memory, WitOS supplies the real thing:
the interface identifiers of `uuid.lib`, and the `BSTR`, `VARIANT` and `SAFEARRAY` helpers that marshalling uses.
Apartments, activation, type libraries and WinRT fail explicitly, as on a system without those services; nothing
returns a pretended success. The alternative, CoreCLR without `FEATURE_COMINTEROP`, is not a configuration upstream
builds for Windows, and was rejected.

#### P6.4.k3a1: vcruntime's searches and the range-check report

The C runtime CoreCLR needs from vcruntime was five functions and one report. The UCRT subset now has `strchr` and
`strrchr`, which `utilcode`'s namespace helpers call, and `wcsrchr` and `wcsstr`, which `minipal` calls. As in
vcruntime, the value converts to the string's character, and a search for 0 finds the terminator; an empty `wcsstr`
pattern finds the start. The in-process differential compares all four with UCRT's over 200,000 random strings,
terminators included, and `CrtTests` passes.
`__report_rangecheckfailure`, the compiler's range check under `/GS`, ends the process in the C++ runtime as
`_purecall` does. The NativeAOT archive keeps its own report with a diagnostic line; no image links both.

The inventory falls from 276 to 271. `longjmp` remains: CoreCLR's managed exception handling calls it
(`vm/exceptionhandling.cpp`), and an MSVC x64 `longjmp` is an unwind through `RtlUnwindEx` with `STATUS_LONGJUMP`,
which belongs to the exception work.

#### P6.4.k3a2: classes, integers, secure strings, environment and sorting

Thirty of the UCRT functions CoreCLR calls join the subset. Each has UCRT's contract, and an invalid parameter ends
the process, as UCRT's default handler does:

- **Character classes and case.** `isalpha` and `isdigit` read the C locale's table. `iswalpha`, `iswspace` and
  `iswupper` read Windows' `CT_CTYPE1` classes for Latin-1, the table the STL's locale already uses, which equals UCRT's
  `_pwctype`. Beyond Latin-1 they ask the platform's `GetStringTypeW`. In the guest that ends the process, which has
  no Unicode character database (the rule of P6.4.i3b). `iswascii` is below U+0080. `towlower` and `towupper` change
  only A-Z and a-z, as UCRT does in the C locale.
- **Integers.** `strtol`, `atol` and `_atoi64` parse narrow text as UCRT does: the C locale's white space, ASCII
  digits, and a `0x` prefix that converts nothing when no digit follows it. They keep reading after an overflow and
  report `ERANGE`. `_wcstoui64` is the wide parser, Unicode digits included, at 64 bits. `_ltow_s` formats in radix 2
  to 36, signed only in radix 10.
- **Secure strings.** `strcpy_s`, `strcat_s`, `strncpy_s` and `strncat_s` follow UCRT's rules in narrow and wide form.
  A result that does not fit is an invalid parameter, except under `_TRUNCATE`, which returns `STRUNCATE`. Also:
  `strnlen`, `_strdup`, `_strnicmp` (ASCII case), `_strupr_s`, `_wcslwr_s` and `strtok_s`.
- **Environment.** `getenv` reads a narrow copy of the process's environment, made at the first call in the platform's
  ANSI code page (UTF-8 in the guest). UCRT makes a library's narrow environment the same way. Later changes to the
  process's environment are not in the copy, as in UCRT, which updates it only for `_putenv`. Names compare without
  ASCII case.
- **Sorting.** `qsort` is a heap sort; the C standard leaves the order of equal elements unspecified.
- **Invalid parameters.** `_invalid_parameter_noinfo`, the report of UCRT's inline functions, ends the process.

The platform files gain `NarrowEnvironment` and `CharacterType`, both over kernel32 functions that the guest's
adapters supply.

Evidence. A new section of the in-process differential compares each function with UCRT's:

- every class and case of -1 to 255 and of all 65,536 wide characters;
- 300,000 random integer texts in six bases, with results, end pointers and `errno`;
- `_ltow_s` in every radix;
- 100,000 random secure copies and appends, fitting and truncating, compared byte for byte;
- 50,000 tokenizations;
- the environment's variables, and 2,000 sorts with duplicates.

That adds 5.1 million comparisons to the 13.4 million before, with none failing. The inventory falls from 271 to
241; ucrt's share from 54 to 24, which are formatting and scanning, mathematics, `wcstod`, `_controlfp_s` and files.

