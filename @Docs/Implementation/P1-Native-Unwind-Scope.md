# P1.8.d: native walk scope and production unwind binding

Follow-up: [P1-Guest-Unwinder](P1-Guest-Unwinder.md) records actual production GS context/scope/unwinder execution and the explicit runtime PE profile. The results and limitations below describe this earlier implementation checkpoint; managed execution and full exception dispatch remain open.

This slice adds a native scope over ABI v31 stack leases and includes the real checked upstream unwinder in the source-built WitOS archive. It does not complete guest unwind/handler execution, runtime attachment or managed startup.

## Native lifetime scope

WitNativeUnwindScope acquires a kernel lease from the caller's actual thread reference. Foreign targets must already be suspended; the scope never silently stops or resumes them. It is noncopyable/nonmovable and uses a compiler-TLS stack of lexical scopes. Before the first TLS access, it queries the kernel for current identity and a compiler-TLS page. Missing TLS is a checked failure.

Current() always queries the kernel token and uses the returned owner/target identities and stack interval. It accepts only the innermost scope and never searches outer scopes to turn an out-of-range stack pointer into a success. Refusal preserves the complete destination. Thread-local pointers and cached fields provide navigation, not kernel authority.

Close() checks kernel ownership and LIFO order before releasing. A foreign thread cannot close an owner's scope, and closing an outer scope early returns BUSY. Successful close restores the preceding scope. Normal C++ returns, including error returns, run the destructor; explicit Close is required before a nonlocal context transfer. The destructor fails fast on lost kernel authority or cleanup failure. Raw exits still rely on kernel cleanup and do not pretend to run C++ destructors. Scope operations use raw native calls and preserve native last-error and errno.

The enclosing scope must remain alive while callers use returned original-stack root/return-address locations. Kernel leases do not by themselves provide runtime safepoints or GC thread attachment.

## Production binding

RuntimeUnwindReference.PrepareAsync prepares canonically hash-verified headers and the separately adapted upstream AMD64 source before the source-built archive is compiled. The same exact replacements and hosted differential gate remain mandatory; Windows reference and WitOS overlay stay separate.

The WitOS archive now contains the native scope, guest binding/failure transport, checked wrapper, metadata/read validator, pinned upstream algorithm and x64 direct/import transport. Source verification checks actual archive/object byte identity and rejects /GS- for these production objects. The binding verifier checks a readonly .rdata alias owned by __imp_RtlVirtualUnwind, targeting the public RtlVirtualUnwind symbol, and the code relocation from that symbol to the real native implementation. MASM's standard .text$mn subsection is accepted as executable code, not mistaken for a missing .text section.

A foreign RtlVirtualUnwind call requires an enclosing native scope. With no scope, a current-stack call acquires a temporary current-thread lease and checks the supplied RSP against kernel-derived bounds. The executing caller intrinsically retains its own stack after return; this fallback never grants access to a foreign stack. Wrong image base, missing authority, invalid metadata, bounded-read failure or unsupported inputs terminate the component through the existing raw fail-fast transport. Null handler remains a successful no-handler result, never a substitute for failed unwinding.

The binding uses the published single-image descriptor, preserves standard CONTEXT and output semantics, and does not link Windows implementation libraries. Its failure transport uses neither heap allocation nor recursive exception unwinding.

## Evidence and limits

Guest scope tests use a separately compiled source-equivalent /GS- probe, as the current PE loader still rejects handler/chained metadata. They cover current/foreign nesting, quota and failed acquisition without disturbing the outer scope, innermost-range rejection, foreign Close denial, restoration of the previous scope, missing compiler TLS, preservation of last-error/errno and fail-fast after a deliberately revoked token.

A worker exposes a real local variable. The owner obtains its original address through a helper, returns from that helper, schedules another thread, confirms final Resume is blocked and updates the original location. After scope release/resume, the worker observes the changed value. This is a native lifetime test, not GC root enumeration.

The protected production scope has actual GS handler dependencies and is distinct from the guest probe. The full checked unwinder/binding is source-built and link-tested but has not executed in WitOS. The binding removes RtlVirtualUnwind from the diagnostic unresolved list: minimal/broad boundaries are 8/14. That reduction does not close P1.8.d.

The existing hosted gate continues to compare 20 upstream/checked/Windows scenarios, 13 transactional failures, 20 metadata cases, eight bounded-read cases and all 2,593 entries of the full NativeAotBoot reference image. Guest scope markers are User.NativeUnwindScope and User.NativeUnwindScopeRejection; all four final runtime profiles passed with 262 User groups / 55 expected contained faults each. Release build, audit/probe/target/source/readiness and all 20 boot scenarios passed. The full archive contains 132 members, the configuration probe 45 objects.

Artifacts: artifacts/p1-unwind-scope-config.log, p1-unwind-scope-config-final.log, p1-unwind-scope-test.log, p1-unwind-scope-probe.log; artifacts/runtime-source/witos-unwind-objects.json and the runtime-unwind reference manifests. The object report explicitly records guestUnwinderExecuted=false.

## Remaining P1.8.d/e work

Pair the validated loader extension with actual guest consumers: execute the archived protected unwind/scope/context objects, verify both direct/import bindings, malformed metadata and guard-page failures, and run the real GS/SEH handlers. The loader must validate kernel-owned PE file bytes before allocation and reject relocations into every accepted metadata range, including chains. Runtime-wide foreign walks must establish an enclosing scope using the actual Thread/reference relation and retain it through root or hijack updates. Current-thread fallback does not solve foreign GC rendezvous.

Exception delivery, language-specific handler payload validation and managed unwind semantics remain separate mandatory gates; metadata acceptance and handler-address discovery do not prove them.
