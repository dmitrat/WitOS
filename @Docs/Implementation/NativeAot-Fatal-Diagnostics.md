# Native fatal diagnostics

WitOS 0.0.43 implements `PalPrintFatalError`, `_purecall` and `__report_rangecheckfailure` in the source-built NativeAOT workstation archive. This supplies native diagnostic and termination behavior, not managed exceptions or runtime startup. ABI v18 is unchanged.

## Contract

The native image context captures the startup console capability before publishing readiness, alongside the existing immutable image descriptor. The kernel still validates the live handle type and write rights on every write; the captured value grants no new authority. There is no new syscall or implicit privileged output channel.

`PalPrintFatalError` uses the actual pinned upstream PAL declaration. It accepts a NUL-terminated message of at most 256 bytes, scans with exact volatile byte reads, and submits one existing WRITE call. The kernel validates the complete readable range before output and returns the written count. An empty message validates the handle but emits no bytes. A valid message returns normally, as upstream's printing function does.

No allocation, compiler TLS, errno, native last-error mutation, locking or waiting is required. A NUL in the final byte of a readable page is sufficient; the scanner does not read past it. The maximum-sized message needs its NUL at offset 256. Null, overlong, unavailable-console or rejected-write cases take the existing generic raw fail-fast exit (`0xFFFF0001`). This is an explicit bounded bootstrap profile; it does not promise arbitrary-length output. Invalid nonnull native pointers can fault while scanning, before any output. Concurrent mutation of caller-owned strings is outside the string contract; the kernel still validates the final copied range.

The CRT hooks attempt a fixed diagnostic, then unconditionally exit with `0xFFFF0002` for pure virtual calls or `0xFFFF0003` for compiler range-check failure. A missing or revoked console cannot replace the original cause. These exits bypass TLS destructors, atexit and native thread notifications. They do not provide configurable CRT purecall handlers, Windows fast-fail exception dispatch, general CRT abort support or managed shutdown. Low-stack diagnostics also remain subject to the fixed native stack budget; this is not a managed stack-overflow recovery path.

## Source and guest evidence

Windows-reference libraries remain unchanged. The WitOS fatal source uses the actual upstream PAL and CRT declarations and a `/Od;/GS-` plain-unwind profile. Its object is verified byte-for-byte inside the full archive, copied to the guest platform fixture and hash-checked before linking. Other runtime and CFG dependencies remain unresolved in the full link check.

The small platform fixture uses the existing guest-profile process-exit object to register callbacks and provide a positive orderly-shutdown control. The source-archive process-exit object still requires Windows CFG dispatch; it is not substituted in the full runtime archive or declared ported by this fixture.

Modes 29-38 test normal/empty/256-byte messages, a readonly string ending immediately before an uncommitted page, native error and errno preservation, operation without compiler TLS, both CRT termination reasons, uninitialized/revoked console cases, null/overlong rejection and raw cleanup bypass. The orderly control runs all three cleanup callbacks; fatal cases must run none. Kernel assertions require exact exit codes/write counts, original owned-page accounting, no live handles/events and complete page recovery. A fresh successful component runs after the rejected cases. Relocated images are included.

## Validation

All four runtime-config profiles passed 210 user groups and 54 expected contained faults each: qemu64 at 128/512 MiB and Nehalem/max at 256 MiB. The platform fixture is 13,312 file bytes with 53 plain unwind records and no OS/CRT imports. The configuration archive has 18 exact source objects; the workstation archive has 88 members and minipal retains 11.

Source audit verified all 66 pinned files. Hosted NativeAOT probe, native C-host target/reference and minimal executable checks passed. The strict broad link boundary drops from 74 to 71 unresolved symbols; minimal executable startup drops from 68 to 65. Guest managed execution remains pending.

All 19 ordinary boot scenarios passed, retaining 178 required user groups and 51 expected contained faults in successful boots. Release solution builds completed with zero warnings and errors. Generated images and logs remain under ignored artifacts/.
