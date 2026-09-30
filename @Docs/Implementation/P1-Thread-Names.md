# P1.6 kernel-owned native thread names

User ABI v25 adds current-thread name set/query calls. The kernel owns a bounded UTF-16 description in each thread record; writable FS/GS storage, native DWORD IDs and user-supplied handles do not select a naming target. This is the private current-thread contract needed by PalSetCurrentThreadName/PalSetCurrentThreadNameW, not a general cross-thread SetThreadDescription API.

## Mutation and discovery

Names contain at most 127 UTF-16 units plus a terminator. Empty names clear the description. The wide PAL preserves exact UTF-16 units, including isolated surrogates, as diagnostic data. The UTF-8 PAL uses the already verified Windows-compatible replacement converter with flags zero, as pinned upstream PalMinWin.cpp does. It bounds byte scanning and sizes the result before allocating a temporary buffer from the real native heap. Oversize names and allocation failure return false with an explicit native error; neither silently truncates nor replaces the old name. Temporary storage is released while preserving the original error. Successful operations preserve last-error and never use errno.

The kernel validates flags, count and the entire source before mutating any name field. Embedded NULs in an explicit nonempty kernel request are rejected. Zero count consumes no input buffer and clears all storage. Copy, publication and the 280-byte query snapshot run with interrupts disabled. Query includes the kernel's generation-bearing thread identity, validates version/exact size and the whole writable destination, and does not partially copy to an invalid page tail. Unused name bytes and reserved fields are zero.

Names remain available through native TLS cleanup and the private native exit notification. They are cleared on actual thread exit, reaping, preparation/reuse and process termination/destruction. A reused slot starts unnamed. No name is used as authority or assumed unique.

## Actual objects and compiler profile

The PAL naming object and native_new.witos.cpp object are taken from the full source-built runtime archive, verified byte-for-byte, copied with hashes and linked into the guest fixture. No fake allocator or Windows implementation library is used. The initial optimized heap object contained chained unwind metadata and was correctly rejected by the loader. Its full WitOS source build now uses /Od, retaining the ordinary runtime GS profile, to match the existing plain-unwind execution boundary. This does not implement chained unwinding or relax PE validation. The Windows reference build remains separate.

## Tests and limits

Guest modes 74/75 cover both load bases, calls before native image/TLS publication, execution without compiler TLS, UTF-8 supplementary characters and malformed sequences, raw UTF-16 surrogate preservation, clear/replace, exact maximum and over-limit names, input/output guard-page boundaries, readonly query destinations, malformed version/size/flags, overflow counts and invalid source ranges. Raw TLS identity is deliberately changed and restored; the naming target and query identity remain kernel-owned.

Tests exhaust the real native heap registry and verify that failed UTF-8 conversion leaves the old name intact, then free allocations and retry successfully. Three concurrent workers plus a fourth reused slot begin unnamed, set their own names and read them from the exit callback after TLS cleanup. The main name remains unchanged. The supervisor checks thread lifecycle, zero name storage after exit, no unintended console writes and complete resource recovery. Other contained-fault and detached-thread regressions use the same cleanup code; these naming cases do not claim a separate renamed-fault/detached workload.

A direct Windows host check confirmed successful empty, ordinary and isolated-surrogate descriptions. The normative runtime signatures and UTF-8 conversion policy come from pinned Pal.h/PalMinWin.cpp at b82454cad0aaaae3db2cf18fbf2cccc36e201ccc. Unlike upstream's unchecked SetThreadDescription HRESULT, the WitOS boolean reports actual kernel mutation failure.

Validation passed: Release build, all 20 boot scenarios, runtime-audit/probe/target/source/readiness and four QEMU runtime profiles with 237 user groups and 54 expected contained faults each. The strict minimal/broad link now has 31/37 unresolved dependencies. The source-built archive contains 116 members; the configuration archive contains 34 exact objects. RuntimeThreadFixture is 36864 bytes with 95 plain unwind records, below the unchanged 128-record loader limit. P1.6 and P1 are still open; this is native naming, not managed Thread execution or runtime attachment.

Logs: artifacts/p1-thread-name-config.log, artifacts/p1-thread-name-test.log, artifacts/p1-thread-name-audit.log, artifacts/p1-thread-name-probe.log. Object hashes and compiler commands are in runtime-source/runtime-config reports.

References: [SetThreadDescription](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreaddescription), [GetThreadDescription](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getthreaddescription).
