# P1.8 upstream AMD64 unwinder reference

A reproducible hosted reference now compiles the pinned upstream AMD64 unwinder and compares it with the actual Windows RtlVirtualUnwind. This prepares the real algorithm for a checked guest adapter; it does not resolve the guest symbol, enable handler/chained images in the kernel loader or prove guest unwinding.

## Source and compilation

Four canonical upstream files were added to the runtime lock at b82454cad0aaaae3db2cf18fbf2cccc36e201ccc: amd64/unwinder.cpp, amd64/unwinder.h, baseunwinder.h and win64unwind.h. The source audit now verifies 71 files. The generated source changes only the stdafx.h include to an explicit environment header; the algorithm remains unchanged. Metadata headers are copied byte-for-byte.

The hosted environment uses real SDK CONTEXT/RUNTIME_FUNCTION types, the upstream metadata definitions, non-DAC pointer access and always-active assertion failure. The narrow anonymous-union and optional-parameter warnings match the source's conventions; /W4 and /WX remain enabled. Host module/function lookup calls real GetModuleHandleExW/RtlLookupFunctionEntry. Those Windows operations are reference-only, not guest implementations.

`runtime-unwind` performs the source audit, builds the reference and executes it. runtime-source also runs this reference as a required gate. Generated sources, objects, executable, logs and hashes stay in artifacts/runtime-unwind.

## Differential evidence

Fourteen cases compare the full resulting CONTEXT, nonvolatile context pointers, handler/data and defined establisher-frame results. Real PE .pdata/.xdata records cover partial prolog, body and epilog positions, nonvolatile GPR restore, frame-pointer addressing, XMM restore, large stack allocation, chained metadata and a language handler. A separate compiler translation unit creates an actual /GS+SEH frame and executes it; COFF checks require security-cookie and GS-handler dependencies, and the runtime comparison requires a non-null handler. The observed record has version/flags byte 0x19.

The initial all-fields comparison exposed a difference in EstablisherFrame at a frame-pointer epilog. Microsoft's x64 contract explicitly leaves that value undefined in an epilog. The algorithm was not changed to imitate an unspecified Windows value. The test marks known epilog locations explicitly and still compares all defined register/context-pointer/handler outputs there; prolog/body establisher values remain exact comparisons. This distinction matters for future handler dispatch and cannot be treated as a fabricated valid frame identity.

These cases are a starting conformance set, not exhaustive coverage of every unwind opcode, metadata version or malformed record. The current reference directly accesses valid host memory and must not be connected to untrusted guest metadata as-is.

## Required guest integration

The next adapter needs checked stack reads, bounded instruction reads, validated immutable unwind metadata and real module/function lookup from the published image. It must bound chain traversal, reject malformed lengths/opcodes/cycles, avoid partial context publication on failure and use actual kernel-owned stack limits. Handler discovery is not handler execution. GS validation and exception dispatch must be connected before protected context objects can be accepted as executed.

The kernel loader still rejects handler/chained records. RtlVirtualUnwind and the other exception/attachment dependencies remain unresolved in the strict guest boundary. No /FORCE, alternative CoreLib, fake GC operation or successful unwind stub was introduced.

Validation passed: all 14 hosted reference cases, Release build, source audit with 71 canonical files, hosted NativeAOT probe, target/source/readiness and four runtime profiles (257 user groups / 55 expected contained faults each), plus all 20 boot scenarios. A separate exact-byte check confirms that reversing the include replacement reproduces the canonical upstream .cpp bytes. The strict minimal/broad boundary remains 9/15 unresolved symbols. Guest unwinding and guest managed execution remain unproven.

Evidence: artifacts/runtime-unwind/reference.json, reference.log and build.log; artifacts/p1-unwind-reference-config.log, p1-unwind-reference-test.log and p1-unwind-reference-probe.log.

References: [Microsoft x64 prolog/epilog rules](https://learn.microsoft.com/en-us/cpp/build/prolog-and-epilog?view=msvc-170), [Microsoft RtlVirtualUnwind2 establisher-frame contract](https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/DevNotes/rtlvirtualunwind2.md), and the pinned dotnet/runtime sources listed above.
