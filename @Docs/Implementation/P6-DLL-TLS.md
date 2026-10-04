# P6.4 multi-module DLL TLS work

Date: 2026-10-02, closed 2026-10-03; P6.4.a–c 2026-10-04. ABI48 static DLL TLS is implemented; the full regression matrix passed in CI and again on the consolidated Q2 code. ABI v49 adds PE TLS callbacks (P6.4.a/b) and dynamic C++ `thread_local` objects in DLLs (P6.4.c). Admission of threads that predate the load (P6.4.d) remains open.

## Existing boundary to extend

The current compiler TLS page stores the GS table pointer at offset 0x58, slot vector at 0x80 and main-module data at 0x100. Slot zero belongs to the main image. The 4-thread fixed layout has a 0x20000 stride; any extra TLS mapping must be checked against stack guards, the peer page and fixed image windows before being allocated. Kernel records must own every block and its generation; caller-writable vector/index bytes cannot become ownership authority.

The next backend must validate real PE TLS metadata/fixups before allocation, capture relocated template bytes before publication, reserve distinct module slots, and supply independent initialized/zero data to each thread. It must roll back all block/header/page-table additions on failure, clear active translations before reuse, preserve the main TLS/raw FS contract and reclaim module blocks on thread/module teardown. Loading into already-live threads and dynamic constructors/destructors require explicit integration; they are not implied by static copying.

## Genuine compiler fixture and Windows evidence

`tests/User.X64/library_tls.c` uses actual __declspec(thread) integer, pointer and zero data, and exports getters/setters/address/index. Its pointer initializer refers to an ordinary image variable, requiring real template relocation. `tests/WitOS.Dev.Tests/LibraryTls.c` checks main/worker isolation, distinct addresses, zero bytes and the relocated pointer's value.

A standard Windows CRT-linked control passed. A custom metadata image with no imports loaded and entered, but retained index zero and returned wrong TLS values; adding an entrypoint or a callback table alone did not correct that result. In this environment, the same custom image passed after adding one real import. Replacing the Windows-only import with the existing WitLibraryFixture LibraryAdd dependency also passed, including the native subsystem profile. This is observed reference-loader behavior, not an inferred general Windows rule or a requirement to change WitOS semantics.

The successful custom reference links no Windows CRT/OS implementation into its DLL; its dependency is the project's own native fixture. Evidence is under artifacts/dll-tls-reference/, notably reference-standard.log, reference-directory.log, reference-import.log and reference-native-import.log. The failing controls are retained. At this reference-only stage, these fixtures were not yet integrated into guest builds; the subsequent ABI48 backend is described below.


The successful own-dependency fixture is now reproducible through `--dll-tls-reference`, using NativeTlsLibraryImage and a machine-readable source/DLL/executable hash report. The command and full host-suite group passed. At that reference-only stage, guest admission still rejected DLL TLS; ABI48 adds the separate static-only profile below. Windows comparison is preparation, not guest support.


## Static guest backend (ABI48)

The dedicated library TLS profile validates the existing PE TLS directory, real VA fixups and bounded initialized/zero template, retaining explicit refusal of nonempty callbacks. Main slot zero is preserved; each library slot uses a separate nonzero compiler index. The kernel captures the relocated template before publication and creates independent per-thread backing. Fixed layout assertions keep the vector before main TLS data and the new pages away from peer/stack/image regions.

Kernel records own every module block and header. Loading into a C component without main TLS creates a compiler header as needed; unloading its last DLL TLS block removes that header and restores GS state. A main TLS header/data block remains alive. Caller-writable vectors and module index bytes do not define ownership. Thread-info v3 separates existing CompilerTls main-readiness semantics from the actual CompilerTlsHeader, avoiding accidental errno/runtime access through a DLL-only header.

Module load/abort/unload and thread creation/reaping include the extra pages in rollback. A new failure sweep exposed delayed OOM after successful thread creation because notification plans were still allocated lazily. Entry-bearing workers now reserve a real readonly notification page and separate attach/detach handles as part of atomic creation. The handles retain distinct generations; the page stays thread-owned until reaping.

Targeted 128/512 MiB guest runs passed two-module isolation, real template-pointer relocation, zero data, fresh worker/reused-slot state, main-slot-zero coexistence and cleanup after corrupting writable compiler-vector hints. Module-load OOM rollback passed. The worker sweep now loads DLLs first, consumes real quota to leave exactly 0..19 pages, requires rollback at every boundary, and succeeds at 20 pages including notification resources. No limits were raised and failed creation preserves the native-ID output.

The new full-image TLS profile passed 4613 guarded admission/truncation/metadata cases under ASan, including retained rejection in the older library profile. The full matrix passed in CI on `main` ([nativeaot run 36972286145](https://github.com/dmitrat/WitOS/actions/runs/36972286145)). After the Q2 consolidation it passed again locally on the same ABI48 behavior: host tests 69, x64 `test` 20 and ARM64 `test` 14 scenarios, `release`, audit/probe/target/source, PE corpus/coverage/fuzz/imports ASan, QemuCleanup, coreclr-source/host/host-files/functions/memory/storage, `runtime-config` and a full `runtime-boot` rebuild. The static slice is closed; no final P6.4 or guest CoreCLR completion is claimed.

## Windows contract for TLS callbacks (P6.4.a)

Dynamic DLL TLS needs PE TLS callbacks: MSVC runs `thread_local` initializers and destructors in a DLL through the
callbacks `__dyn_tls_init` and `__dyn_tls_dtor`. Before the guest accepts them, a Windows reference fixes the order it
must reproduce.

`tests/User.X64/library_tls_callbacks.c` is a CRT-free DLL with static TLS, two TLS callbacks in the CRT layout
(`.CRT$XLA`..`.CRT$XLZ`, merged into `.rdata`) and an entry point. Every call records who ran, the reason, the calling
thread's TLS value and the low half of its TLS address, which names the thread without an OS import, into
`tests/User.X64/library_tls_sink.c`. The host loads the sink first and keeps it, so detach records survive the library.
`tests/WitOS.Dev.Tests/Native/LibraryTlsCallbacks.c` drives one scenario with step markers: a thread that is already
running, the load, a thread started after the load, the exit of both threads and the unload. Its trace keeps the
scenario threads only; a first version showed that a thread created but not yet running at the load is initialized
after it and gets an attach, so the host now waits until that thread runs.

Windows produced the same trace in every run (`NativeTlsCallbackLibraryImage.WINDOWS_ORDER`):

```
M1 M2 A1:731@T B1:731@T E1:731@T M3 T:731 A2:731@N B2:731@N E2:731@N N:731 A3:9@N B3:9@N E3:9@N M4
P:731 A3:731@P B3:731@P E3:731@P M5 A0:5@T B0:5@T E0:5@T M6
```

- For every reason both callbacks run, in list order, before the entry point; the calling thread's TLS is already
  initialized.
- A thread started after the load is attached before its body and detached at its exit with its final TLS values.
- A thread already running at the load gets no attach, but its TLS block exists with the template values, and it is
  detached at its exit.
- The unloading thread runs the process detach.

`LibraryTlsCallbacksTests` rebuilds both DLLs, requires this trace and records the source and binary hashes. This is
Windows evidence; P6.4.b reproduces it in the guest.

## Guest TLS callbacks (P6.4.b, ABI v49)

The PE validator accepts a nonempty callback list only in the library TLS profile: the list lies in readonly
initialized data, holds at most `WIT_PE_TLS_CALLBACK_CAPACITY` (8) entries before its null terminator, every entry
points into an executable section's initialized bytes, and every entry, but not the terminator, carries exactly one
DIR64 fixup, so the list follows the image when it is relocated. Other profiles still refuse a nonempty list. The host
import harness adds 3588 cases on the real callback DLL: acceptance with two callbacks, refusal without the TLS
profile, a callback into data, a callback without its fixup and every truncation; an earlier case that pointed the
list at the TLS directory now fails as a callback into data instead of as an unsupported list.

A library with an entry point or callbacks takes part in the process attach (`wit_user_library_attaches`), and the
sole-thread rule for loading uses that test. Only a library with an entry point takes thread notifications and the
detach (`wit_user_library_participates`), which also decides thread admission; P6.4.c established that rule from the
Windows reference below, after the first version had also notified callback-only libraries. Lifecycle
plan entries (`WitLibraryLifecycleEntry`, ABI v49, `WIT_LIBRARY_VERSION` 2) carry the library's callback list address
and count next to the entry point, which is zero for a callback-only library; the plan grows from 128 to 192 bytes.
The native executor checks that every list is readonly, never writable, null-terminated at its count and that every
callback is executable, then runs each library's callbacks in list order before its entry point for every reason,
including the reverse detach after a failed attach. The executor stays one function: a first version split into
helpers gave the runtime CPU fixture, which links it, more than the plain profile's 128 unwind entries
(`runtime-config` mode 64 failed with TooLarge), and no loader limit was raised.

`tests/User.X64/library_tls_callbacks_guest.c` runs the reference scenario in the `coreclr-storage` workload with the
same sink and library DLLs from the boot package: the load, a thread with library notifications, its exit and the
unload. It formats the trace the same way and requires `NativeTlsCallbackLibraryImage.GuestOrder`, the Windows order
without the thread that predates the load, which the tool generates into `tls_callback_order.h`. Both profiles pass
(`Storage.LibraryTlsCallbacks`). The thread that predates the load stays out until a library with callbacks or an entry
point may be loaded while other threads run (P6.4.d). The guest loader requires a relocation directory for a library it
maps away from its preferred base, so the sink holds one absolute pointer; Windows loads the same DLL unchanged.

## Dynamic C++ TLS in DLLs (P6.4.c)

### Libraries without an entry point

The callback library linked with `/noentry` (`tlsnoentry.dll`) gave Windows' rule for such a library in every run
(`NativeTlsCallbackLibraryImage.WINDOWS_NOENTRY_ORDER`):

```
M1 M2 A1:731@T B1:731@T M3 T:731 N:731 M4 P:731 M5 M6
```

Its TLS callbacks run for the process attach alone: no thread attach or detach and no process detach, while every
thread still has its TLS block with the template values. The P6.4.b guest notified such a library for every reason.
The kernel now separates `wit_user_library_attaches` (entry point or callbacks: the process attach and the sole-thread
rule for loading) from `wit_user_library_participates` (entry point: attach order for later notifications, thread
notifications, detach and thread admission). The executor's reverse detach after a failed attach skips a library
without an entry point. `LibraryTlsCallbacksTests` requires both Windows orders; the guest test runs the scenario again
with `tlsnoentry.dll` and a thread created without library notifications, which is now admitted, and requires the
generated `WIT_TLS_CALLBACK_NOENTRY_ORDER`.

### WitOS dynamic TLS support

MSVC compiles a `thread_local` object with a dynamic initializer into an initializer pointer in `.CRT$XDU`, a guard
check `__tls_guard` with a call to `__dyn_tls_on_demand_init` on each access, and a `__tlregdtor` registration of its
destructor; the CRT supplies those symbols, the TLS directory and the callbacks. WitOS DLLs link no CRT, so
`src/Runtime.Native/library_dynamic_tls.cpp` supplies them for one DLL:

- `_tls_used` with the template bounds and index, and the callback list `__dyn_tls_init` (`.CRT$XLC`) and
  `__dyn_tls_dtor` (`.CRT$XLD`);
- the initializer table between null sentinels in `.CRT$XDA` and `.CRT$XDZ`, and `__tls_guard`;
- a per-thread registry of at most `WIT_NATIVE_TLS_MAX_DESTRUCTORS` (32) destructors and `__tlregdtor`;
- the entry point `wit_library_dll_entry`, which calls the DLL's own `DllMain` or a default that accepts every reason
  (`/alternatename`).

The semantics are the CRT's: the loading thread initializes in the entry point at the process attach, before
`DllMain`; a thread attached after the load initializes in its attach callback; a thread that already ran initializes
on its first access; the detach callbacks run the registered destructors newest first, popping each before its call,
for a thread detach and, on the unloading thread, the process detach. Before the first initializer runs, the whole
table must lie in one read-only, non-executable section of the DLL and hold at most `WIT_NATIVE_TLS_MAX_INITIALIZERS`
(32) entries, each in an executable, non-writable section; destructors must also be code of the DLL, and destruction
stops after twice the registry capacity. The object imports nothing, reads the DLL's own section headers through
`__ImageBase` and ends the process with `__fastfail` on a broken contract. In the guest, `__fastfail` (`int 0x29`)
meets a DPL0 gate and ends the component with a general-protection user fault; no guest test covers that path yet.
Ordinary static constructors (`.CRT$XC*`) and `atexit` in a DLL remain unsupported.

### Windows reference

`tests/User.X64/library_tls_objects.cpp` holds two `thread_local` objects whose constructors and destructors record
the object, a construction serial and the thread into the sink. `tests/WitOS.Dev.Tests/Native/LibraryTlsObjects.c`
drives the P6.4.a scenario with one access per thread, and `LibraryTlsObjectsTests` runs it against the DLL linked with
the WitOS support and against the same source linked with the MSVC CRT (`/MD`, CRT entry point). Both produced the
same trace in every run (`NativeTlsCallbackLibraryImage.WINDOWS_OBJECTS_ORDER`):

```
M1 M2 C1:1@T C2:2@T M3 T M4 C1:3@N C2:4@N N D2:4@N D1:3@N M5 P C1:5@P C2:6@P D2:6@P D1:5@P M6 D2:2@T D1:1@T M7
```

The first version of the support initialized the loading thread lazily, on its first access; only the comparison with
the CRT showed that the CRT's DLL startup initializes it at the process attach, and the entry point now does the same.
Without an entry point Windows would not run the callbacks for a thread at all.

`DynamicTlsBoundsTest` builds `tests/WitOS.Dev.Tests/Native/LibraryTlsBoundsFixture.cpp` four times: 32 namespace-scope
initializers and 32 function-local destructor registrations on one thread succeed, and one more of either ends the
process with `__fastfail` (0xC0000409) before any output. Two linker facts shaped the fixture: an unreferenced
`thread_local` variable is dropped together with its initializer, and Windows sets up no TLS for a DLL without
imports, as the static slice already observed (the index stays zero and accesses reach another module's block), so
the fixture imports the sink. WitOS sets up TLS regardless of imports and does not reproduce that quirk.

### Guest

The boot package carries `native/tlsobjects.dll`, built by `NativeTlsCallbackLibraryImage.BuildObjectsAsync`. The
third run of `tests/User.X64/library_tls_callbacks_guest.c` loads it, touches the objects on the main thread, runs a
thread with library notifications that touches them, and unloads; it requires the generated `WIT_TLS_OBJECTS_ORDER`,
the Windows order without the thread that predates the load:

```
M1 M2 C1:1@T C2:2@T M3 T M4 C1:3@N C2:4@N N D2:4@N D1:3@N M5 M6 D2:2@T D1:1@T M7
```

The three runs use failure codes 36xx, 37xx and 38xx, and all passed in `coreclr-storage`
(`Storage.LibraryTlsCallbacks`). A raw thread exit or a fault still skips the destructors, as for the executable's dynamic TLS. The thread that predates
the load remains P6.4.d.
