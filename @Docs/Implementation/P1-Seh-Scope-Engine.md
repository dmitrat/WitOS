# P1.8.e: C-specific scope tables and scope engine

This slice implements plain C-specific payload validation and the filter/finally decision engine. It is compiled into the actual native archive and exercised against live compiler frames on Windows. It does not define __C_specific_handler or claim target/collided unwind or guest SEH execution. Those dependencies remain real.

## Validated payload

The parser requires an actual aligned exception-directory entry and ties the table address to that entry's validated primary handler-data address. The complete count and every 16-byte scope record must be in initialized readonly non-executable image storage. At most 128 records are accepted. Protected ranges must be nonempty initialized RX ranges; filter/finally and target RVAs must point to code. Handler value one is the catch-all sentinel only when a nonzero target exists. Empty tables are valid. Invalid tables preserve the caller's output.

This parser is for plain __C_specific_handler payloads. It is not a generic parser for arbitrary language handlers or GS/SEH combined payloads. The eventual dispatcher must also verify the actual handler identity and retain image/stack lifetime.

## Scope behavior

Search validates the entire table before invoking anything. It preserves compiler-declared order, skips finally records and out-of-range scopes, invokes real filter funclets with EXCEPTION_POINTERS and the actual establisher frame, and returns explicit Search, Continue or Target decisions. A positive filter produces a target decision; it does not pretend to have transferred execution.

Termination likewise validates before callbacks. It excludes scopes that contain the target and stops at the selected target-handler boundary. Before invoking each abnormal-finally funclet it advances the cursor, so resuming the engine does not repeat completed cleanup. The caller is responsible for retaining the live frame and for actual stack/context transfer. Nested exceptions/collided unwind need their own orchestration and are not supplied by this engine alone.

## Evidence

The hosted executable uses real MSVC __try/__except/__finally functions, real RaiseException and compiler-generated scope tables. It verifies filter-before-finally-before-handler order, abnormal versus normal finalization, continuation, and the constant catch-all encoding.

A live-frame test captures the actual compiler establisher, then invokes the port engine on its real filter/finally funclets. The filter reads a volatile local through its frame argument; finally changes the same original local. Search/Continue/Target results are checked, as are target-inside-scope exclusion and cursor-based prevention of duplicate cleanup. The same live frame is then passed to the actual Windows language handler with UNWINDING/TARGET_UNWIND; local values, finally count and ScopeIndex match the port engine.

Eighteen boundary cases cover valid filter/finally/catch-all/empty tables, association and alignment failures, quota, bad ranges/targets/handlers, writable/truncated tables, bogus exception-directory entries and inaccessible-page endpoints. A later malformed record prevents an earlier otherwise executable action, with unchanged output/cursor.

The gate is available as runtime-seh and is mandatory in runtime-source. Reference manifests contain source/input/executable hashes and explicitly mark hostOnly=true and guestSehImplemented=false. Production validator/engine objects retain GS protection and are verified byte-for-byte in the full archive.

Artifacts: artifacts/runtime-seh-reference/reference.log/reference.json/build.log, artifacts/p1-seh-scope-source-final.log, p1-seh-tables-probe.log and p1-seh-tables-test.log. Passed: Release build, audit/probe/target/source/readiness, runtime-seh and all 20 boot scenarios. The full native archive contains 137 members; minimal/broad link remains 4/10 unresolved. No new guest SEH execution is claimed. Source-only execution does not close P1.8.e.

## Next required transfer boundary

Connect these decisions to the real exception dispatcher and implement phase-two unwind to the selected frame, running actual finally handlers and restoring nonvolatile registers/context before entering the compiler target. Preserve nested/collided-unwind semantics, kernel exception nesting, stack-lease lifetime and frame/call-site identity. Only then supply __C_specific_handler and validate real guest catch/finally workloads; returning ContinueSearch or selecting an address is not sufficient.

Primary ABI references: [x64 exception handling](https://learn.microsoft.com/en-us/cpp/build/exception-handling-x64?view=msvc-170) and [RtlUnwindEx](https://learn.microsoft.com/en-us/windows/win32/api/winnt/nf-winnt-rtlunwindex). The language-specific details above are backed by compiler/Windows execution rather than copied Windows implementation code.
