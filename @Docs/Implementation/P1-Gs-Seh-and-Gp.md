# P1.8.e.4/e.5: combined GS/SEH and the selected GP profile

The selected native exception/handler boundary now passes guest acceptance. This closes P1.8.e, not runtime attachment, collector startup or managed EH. Nested hardware faults and unclassified GP instruction forms remain explicit contained failures. User ABI stays v35; no Windows implementation library is linked into the guest.

## Combined GS/SEH

The new source-built seh_security.witos.cpp supplies __GSHandlerCheck_SEH. It validates the canonical function entry, actual handler identity, complete readonly scope table and trailing GS payload before any callback. The shared cookie helper validates the original unwind header and binding, derives the cookie location (including alignment metadata), queries kernel-owned current stack bounds and performs the real security-cookie check. Only the applicable dispatch/unwind bit in GS metadata permits entry into the real C-specific engine. Cookie checks precede filters and phase-two finally/catch transfer.

The plain __GSHandlerCheck path retains its independent binding. Combined handling delegates through an internal C-specific dispatcher that accepts exactly the plain and combined identities and verifies the actual metadata association. It does not rewrite metadata or pretend that the combined handler is the plain handler.

Both Windows and WitOS compile the same tests/Runtime.NativeAot/seh_gs_frame.cpp with real /GS and SEH, in ordinary and 32-byte-aligned forms. The builder requires __GSHandlerCheck_SEH, __security_check_cookie and __security_cookie COFF dependencies. Negative tests derive the cookie displacement from the compiler listing and verify that the buffer and cookie use the same frame base. The generated readonly layout object and protected objects are hashed as fixture inputs. The aligned fixture exercises the full three-word GS payload and compiler frame setup, not a hand-written equivalent.

Windows and guest cases cover catch/abnormal finally, continuation/normal finally, ordinary return, corruption before search and corruption inside the filter before phase two. Corruption before search invokes no filter; corruption in the filter invokes that filter once and prevents finally/catch. Windows terminates with 0xC0000409; the guest uses its existing GS failure exit 0xFFFF0004 and component cleanup. Hosted validation additionally covers 11 trailing-payload boundary cases, including a real guard page, invalid alignment, excessive count and unchanged output on failure. Guest modes 160-162 reject incorrect HandlerData, a copied noncanonical function entry and a substituted language handler.

## General-protection translation

The new runtime-gp command is a mandatory runtime-source gate. Its real x64 instruction fixture records and verifies Windows behavior for an invalid segment selector, CLI, HLT, noncanonical load/store and unaligned MOVAPS. The same assembly executes in the guest.

| Tested cause | Windows/native record |
| --- | --- |
| CLI, HLT | EXCEPTION_PRIV_INSTRUCTION, no parameters |
| Invalid selector, noncanonical load/store, unaligned MOVAPS | EXCEPTION_ACCESS_VIOLATION, two parameters: 0 and ~0 |

The unknown address is deliberately ~0. The pinned NativeAOT handler uses the address to recognize null references; fabricating zero would request the wrong managed translation. For these GP cases Windows reports parameter zero as 0 even for a noncanonical store; this is not a reconstructed effective address or access direction.

Instruction classification stays in Kernel.Arch.X64/native_exception_x64.cpp. The native adapter copies at most 15 initialized RX image bytes after readonly range validation, then asks the bounded classifier about the kernel-captured GP error. The selected forms are CLI/HLT, MOV segment with a nonzero selector error, memory MOV load/store and aligned MOVAPS/MOVAPD forms (0F 28/29 with permitted prefixes). Register-only forms, truncated prefixes/opcode fields, oversized input and unrecognized families are unsupported. This is a classifier for the selected native profile, not a complete x64 decoder or universal Windows fault emulation.

Hosted guard-page checks prove bounded reads at the input end. Guest mode 166 verifies all six record tuples, exception address/context, continued execution, error/errno preservation, a compiler catch and a fresh follow-up exception. The existing unhandled-selector test now explicitly returns ContinueSearch and verifies that the original GP remains the failure cause. Mode 167 executes an unaligned FXSAVE outside the selected classifier profile: it must preserve vector/error/RIP and fail before invoking handlers. No unsupported case returns invented success.

## Evidence

- Release solution build: zero warnings/errors.
- All 20 kernel integration scenarios pass.
- Four runtime-config profiles pass (qemu64 128/512 MiB, Nehalem/max 256 MiB): 284 User groups and 66 expected contained faults each.
- Audit/target/source/readiness gates, Windows SEH/GS/GP references and hosted runtime-probe pass.
- Source-built native archive: 139 members; configuration probe: 51 objects. Seven exception objects and six unwind objects are tracked; the new production objects retain GS and are verified byte-for-byte in the archive.
- Actual runtime fixture: 88,576 file bytes, 283 unwind records. Existing 64 KiB stacks, 320 runtime unwind capacity and 128 owned-page quota remain bounded and tested.
- Minimal/broad strict-link inventories remain 3/9 unresolved: PalInitComAndFlsSlot, PalAttachThread and PalHijack are still required for minimal startup.

Logs: artifacts/p1-gs-gp-build.log, artifacts/p1-gs-gp-config-final.log, artifacts/p1-gs-gp-test-final.log, artifacts/p1-gs-gp-probe.log. Reference inputs, compiler listings, cookie-failure logs and hashes live under artifacts/runtime-seh-reference and artifacts/runtime-gp-reference. Source/archive/image identity is retained in the normal runtime-source/runtime-config reports.

## Next boundary: P1.8.f

Pinned FinalizerStart calls PalInitComAndFlsSlot on the finalizer thread, signals its initialization handshake and only then attaches. ThreadStore::AttachCurrentThread calls PalAttachThread before Thread::Construct and list publication. The existing private exit-notification facility already preserves TLS destructors -> runtime notification -> apartment cleanup, with kernel-confirmed owner identity and pop-before-call behavior.

The next adapter must connect these real paths to RuntimeThreadShutdown and ThreadStore::DetachCurrentThread, including GC allocation-context cleanup. A native callback registration probe cannot close attachment/detach acceptance; keep full runtime dependencies and verify integration with the actual collector. PalHijack, the guest entry thunk and strict complete link remain subsequent P1 work.
