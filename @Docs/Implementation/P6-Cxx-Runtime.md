# P6.4 guest C++ runtime for the host

Date: 2026-10-05. Status: P6.4.e (Windows), P6.4.f (guest), P6.4.g (vcruntime surface), P6.4.h (UCRT subset) and
P6.4.i1 (STL exceptions and algorithms) complete; P6.4.i2 next.

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
- [ ] **P6.4.i** microsoft/STL `vs-2022-17.14`: pin, audit and build of the separately compiled sources the host
  needs with the UCRT functions those sources call. STL's build includes vcruntime's `internal_shared.h` from the
  toolset's reference sources; a minimal WitOS header replaces it. Their Win32 calls go through WitOS adapters, and
  any change to a pinned file is an upstream patch.
  - [x] **P6.4.i1** Exceptions and algorithms: the pin, `std::_X*` and `_Throw_Cpp_error`, system error messages,
    `std::uncaught_exception` and the vectorized algorithms.
  - [ ] **P6.4.i2** Threads and synchronization: `_Mtx_*`, `_Cnd_*`, `_Thrd_*` with `_beginthreadex`.
  - [ ] **P6.4.i3** Locales and streams: the support behind `std::wstringstream`.
- [ ] **P6.4.j** The host's Win32 imports (files, mappings, critical sections, modules, console, registry) over
  WitOS adapters; strict link of `hostfxr` and `hostpolicy` with no unresolved symbol.
- [ ] **P6.4.k** Guest `hostfxr_main` reads a real application's runtimeconfig and deps through `hostpolicy` and
  reaches `coreclr_initialize`/`coreclr_execute_assembly`.

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
| `format.cpp` | the wide printf engine; `__stdio_common_vswprintf`, `__stdio_common_vsnwprintf_s` |
| `stdio.cpp` | `__acrt_iob_func`, `__stdio_common_vfwprintf`, `fputwc`, `fwrite`, `fflush`, `setvbuf`, `_wfsopen`, `fclose`, `_wremove`, `_wrename` |
| `locale.cpp` | `_create_locale`, `_free_locale`; the C and UTF-8 conversions |
| `string.cpp` | `wcslen`, `wcscmp`, `wcsncmp`, `wcschr`, `_wcsicmp`, `_wcsnicmp`, `tolower`, `toupper`, `wcstoul`, `_wtoi`, `_wcserror_s` |
| `time.cpp` | `_time64`, `_gmtime64_s`, `wcsftime` |
| `heap.cpp`, `runtime.cpp` | `malloc`, `realloc`, `free`; `ceilf`, `terminate`, `_fltused`; `abort`, `_invoke_watson` (P6.4.i) |
| `platform_windows.cpp`, `platform_witos.cpp` | locks, fail-fast, standard handles, files, the heap, the UTC clock; `_errno` on Windows |

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
slice adds its sources. `StlSources` downloads them at the commit into `.tools/stl/<commit>` and verifies every file
on every use; no STL file is in the repository (`src/Runtime.Cxx/THIRD-PARTY-NOTICES.md`). The sources compile
unchanged with the options of the STL's own static-library build (`/std:c++latest /permissive- /Zc:preprocessor
/Zc:threadSafeInit- /Gy /Zp8 /EHsc`, `_CRTBLD`, `_VCRT_ALLOW_INTERNALS`, `_ITERATOR_DEBUG_LEVEL=0`); i1 needed no
upstream patch.

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
own texts, not Windows'. The NativeAOT COM probe is one unwind entry below the default profile's quota, so later Win32
adapters that its platform objects would carry need their own files or a quota decision. Threads, mutexes and condition variables (i2) and locales and streams (i3) are next.
