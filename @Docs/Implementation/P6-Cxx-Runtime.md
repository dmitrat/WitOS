# P6.4 guest C++ runtime for the host

Date: 2026-10-04. Status: P6.4.e (Windows), P6.4.f (guest) and P6.4.g (vcruntime surface) complete; P6.4.h next.

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
- [ ] **P6.4.h** microsoft/STL `vs-2022-17.14`: pin, audit and build of the separately compiled sources the host
  needs; their Win32 calls go through WitOS adapters, and any change to a pinned file is an upstream patch.
- [ ] **P6.4.i** The host's C runtime and Win32 surface: the UCRT subset it calls (`malloc`/`free`, wide `printf`,
  stdio, time, the C locale, strings) from a pinned musl (MIT) source behind thin WitOS UCRT entry points (decided
  2026-10-04: suitable open code from a pinned source rather than our own; the Windows SDK's UCRT sources are not
  used), and its Win32 imports; strict link of `hostfxr` and `hostpolicy` with no unresolved symbol.
- [ ] **P6.4.j** Guest `hostfxr_main` reads a real application's runtimeconfig and deps through `hostpolicy` and
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

Mode 21 of the CoreCLR mapper fixture (`tests/User.X64/cxx_exceptions_guest.cpp`) runs the 21 scenarios on the
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
