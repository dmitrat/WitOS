# S4 — the C++ runtime of the system layer

Plan step S4 gives layer 2 its C++ runtime ([RFC 0011 v3 §9.1](../RFC-0011-Kernel-Architecture-and-ABI.md)): LLVM's
libunwind, libc++abi and libc++ at the toolchain's release, built over the musl of S1 for both triples, with C++
exceptions in an ELF program. The exception scenarios of `tests/User.X64/cxx_exceptions.cpp`, compiled unchanged in
their Itanium form, print the trace Linux prints, on x64 and ARM64, and libc++ serves the containers, streams, threads
and the rest that the runtime port will need.

## What changed

**The pins (`src/Substrate/llvm-runtimes.lock.json`, `linux-headers.lock.json`).** The three runtimes come from the
release's source tarballs (`libunwind`, `libcxxabi`, `libcxx` `-20.1.8.src.tar.xz`) by the SHA-256 of their
downloaded bytes, extracted by `setup` through the system's tar. libc++ 20 also includes LLVM libc's shared headers
(`libc/shared/fp_bits.h`, `str_to_float.h`, `str_to_integer.h`) for `from_chars`, which upstream reaches through
`runtimes/cmake/Modules/FindLibcCommonUtils.cmake` with `-I libc` and `LIBC_NAMESPACE=__llvm_libc_common_utils`; their
closure — 137 headers and libc's license — is pinned file by file from the tag, as compiler-rt's builtins are. libc++'s
atomic waits include `<linux/futex.h>` on a Linux triple, so the runtimes' build sees sabotage-linux's sanitized
kernel headers 4.19.88-2 (the set musl-cross-make uses), pinned by tarball hash; only `generic/include` and the
`asm` directories of x86 and arm64 are extracted, and no program sees them.

**The build (`tools/WitOS.Dev/Substrate/LlvmRuntimes.cs`).** It replicates the runtimes' CMake rules for a static
Linux-musl build, as `MuslLibc` does musl's Makefile. libc++'s `__config_site` is configured from the upstream
`__config_site.in` the way `configure_file` does (`#cmakedefine01` to 0 or 1, `#cmakedefine` to a definition or an
`#undef` comment): ABI version 1 in `__1`, threads over pthreads, the monotonic clock, musl, the filesystem, the
random device, localization, Unicode and wide characters, no time zone database, the serial PSTL backend and no
hardening; `__assertion_handler` is the default one. libc++ compiles its base sources with the thread, random device,
localization and filesystem lists (`int128_builtins.cpp` included, since the pinned builtins are not all of
compiler-rt) as C++23 with `_LIBCPP_BUILDING_LIBRARY`, `_LIBCPP_REMOVE_TRANSITIVE_INCLUDES` and
`LIBCXX_BUILDING_LIBCXXABI`; the operators new and delete are libc++abi's (`stdlib_new_delete.cpp`), as upstream's
defaults put them. libc++abi compiles with exceptions, thread-safe statics and `cxa_thread_atexit.cpp`, whose fallback
over pthread keys runs since musl has no `__cxa_thread_atexit_impl` — the `__cxa_thread_atexit` S2 left for S4.
libunwind compiles for the native target alone (`_LIBUNWIND_IS_NATIVE_ONLY`), C++17 without exceptions or RTTI, its
C files with `-fexceptions`, and its register save and restore assembly. A C++ program compiles with `-nostdinc++`
against libc++'s generated and public headers ahead of the libc's, with unwind tables, and links libc++, libc++abi and
libunwind between its objects and libc.a; every program now links with `--eh-frame-hdr`.

**The program image (`src/Substrate/libc/static.ld`).** The linker script kept no unwind information. It now places
`.eh_frame_hdr`, `.eh_frame` and `.gcc_except_table` in the read-only segment; lld's `--eh-frame-hdr` adds the
`PT_GNU_EH_FRAME` header that libunwind finds through musl's `dl_iterate_phdr` and `AT_PHDR`, and the flat image
conversion, which keeps loadable segments alone, carries them with the read-only data. `__dso_handle`, which crtbegin
provides elsewhere, is the image base: a static program is one module for `__cxa_atexit`.

**The kernel: a stack at another reservation's base.** `wit_user_space_reservation_bounds` matched an address equal
to a reservation's end, so that a stack pointer at the top of a stack would match. When musl maps a thread's stack
right after another reservation, the stack's base is that reservation's end, and `THREAD_EXIT` with the stack's
reservation found the earlier reservation and refused with `NOT_RESERVED`; a version 2 `THREAD_CREATE` whose stack
top is the next reservation's base could pick that one. The lookup is now strict (`[base, base + size)`):
`THREAD_EXIT` looks the reservation up by its base, and `THREAD_CREATE` by the byte below the stack pointer. No call,
status or structure changed.

**The library.** A thread's exit looks its record up by identity alone — never falling back to the main thread's
record — and a refused exit ends the process with a report instead of spinning in musl's exit loop with the record
gone; that spin is how the kernel defect above showed, in the detached thread libc++'s `std::async` starts. The file
layer serves `/dev/urandom` and `/dev/random` from the kernel's entropy (`RANDOM`), which libc++'s `random_device`
opens on a Linux triple, as .NET's native layer will.

## Tests

- The `cxx` scenario of both suites boots `tests/User/cxx_main.cpp` with `tests/User.X64/cxx_exceptions.cpp` as the
  root task and requires `[CXX] C++ runtime on <isa>: `; the program exits nonzero on any failed check. It runs the
  21 exception scenarios and requires their trace to equal `tests/User/cxx_itanium_trace.h` and every exception
  object to be destroyed, then checks libc++: strings, vector with sort and accumulate, map, unordered_map with
  `to_string`, `shared_ptr`, `weak_ptr` and `unique_ptr`, `std::function`, string streams both ways, a library
  exception with its message, two threads under a mutex and a condition variable with `thread_local` objects whose
  destructors run at the threads' exit, `std::async` with a future, an `exception_ptr` carried from one thread to
  another, `steady_clock` with `sleep_for`, `random_device` and `std::filesystem` on the package's root — 21 checks on
  both ISAs.
- The `itanium-reference` job of the kernel workflow builds the same two sources natively on Ubuntu 24.04 with its
  clang and C++ library and runs them: Linux must print the same trace and pass the same checks. The trace was first
  taken from the guest; CI's Linux run is what makes it Linux's.
- The trace differs from vcruntime's (`NativeCxxExceptionImage.WINDOWS_TRACE`) where the ABIs differ: an Itanium
  catch parameter is built in the landing pad, after the frames in between unwound (S17: `-:171 -:170 copy:172`),
  where vcruntime builds it in the search phase.

## Limits kept explicit

- Static linking alone: one module, one copy of each runtime; `dlopen` and shared C++ runtimes wait for S5.
- musl's own code has no unwind tables (musl's build turns them off, as Alpine's does): an exception cannot cross a
  C frame of the libc, such as a throwing `qsort` comparator; C++ code throwing through its own frames works.
- libc++'s time zone database is off (`_LIBCPP_HAS_TIME_ZONE_DATABASE` 0); the filesystem works on the read-only
  package (every write fails with `EROFS`); locales are musl's C and UTF-8.
- The kernel's quota of four threads of a process bounds `std::thread` and `std::async` alike (sixteen since K5.3).
