# P1.3 GS checks and initialization evidence

The port implements the x64 compiler __security_check_cookie contract, __report_gsfailure and the selected version-1 __GSHandlerCheck metadata path. No Windows implementation object is linked into the guest. Installed Microsoft CRT sources/object disassembly were inspected only to establish the ABI; reference objects remain ignored artifacts.

Initialization is one-time and fails closed for zero, the known default CRT cookie, or a value whose low 48 bits are zero. It publishes a cookie with a zero upper word and its complement before setting ready state. The checker rejects use before initialization, mismatches and nonzero upper-word corruption even if the caller supplied the corrupted global value. The special compiler success ABI preserves return/argument registers and FP return state. Failure takes the raw component exit with reason 0xFFFF0004, not TLS/atexit cleanup.

The v1 handler validates readonly image metadata, actual image identity/code bounds, handler binding and descriptor ranges before reading the stack cookie. It derives stack bounds from the kernel thread snapshot, handles the optional aligned-base descriptor and frame-register offset, and checks the decoded cookie. Writable metadata, bad alignment, unsupported unwind versions/chaining and out-of-stack addresses fail closed. Return ExceptionContinueSearch follows the real check; it is not an unconditional handler substitute.

The cookie initializer and its check/handler implementation use /GS- to avoid recursive dependence on the protection they initialize/validate. Ordinary runtime and formatter objects retain /GS. Exact security objects are verified in the full archive, copied/hash-checked into the guest probe. Kernel PE parsing still rejects registered handler/chained forms; the handler tests pass immutable ABI fixture records directly. They do not claim real exception dispatch, registered GS unwind execution or managed unwinding.

Modes 45-57 exercise startup/check ABI, plain/aligned readonly records, native workers, no compiler TLS, before-init failure, cookie mismatch, upper-word corruption, writable records, invalid metadata/alignment, repeated init and invalid seed values. These use explicit deterministic test seeds to verify mechanics. They are NOT production entropy and are never used as a runtime bootstrap fallback.

Validation: Release build, all 19 boot scenarios and four runtime-config profiles passed (218 user groups / 54 expected contained faults each). Source archive: 97 members; configuration probe: 25 objects. Minimal link: 57 unresolved symbols; broad link: 63. P1.3 remains open until production entropy and pre-protected-frame startup ordering are integrated. Full native exception/context integration remains P1.8/P3.

## Entropy prerequisite investigation

An isolated UEFI application executed on the pinned QEMU/firmware package. With rng-builtin plus virtio-rng-pci, EFI_RNG_PROTOCOL.GetRNG completed two nonidentical reads; without the device on qemu64, LocateProtocol reported not found and the probe failed closed. Seed bytes were not printed. This confirms availability/absence handling, not a statistical proof of entropy quality. The provider contract is defined by the [UEFI RNG specification](https://uefi.org/specs/UEFI/2.10/37_Secure_Technologies.html#random-number-generator-protocol); the selected QEMU backend is documented in [QEMU invocation](https://www.qemu.org/docs/master/system/invocation.html).

Production handoff, seed consumption/erasure, a kernel generator, whole-buffer validation, BCrypt bindings and system-seeded GS startup are not supplied by this feasibility probe. They are the next prerequisite work under the existing P1 goal.

## Production entropy and compiler-frame follow-up

The entropy prerequisite is now implemented and guest-validated; see P1-Entropy.md. The generator-backed system initializer executes before compiler TLS constructors, including in the raw-TLS-free fixture.

RuntimeSecurityReference additionally compiles an actual MSVC /GS frame, verifies its emitted cookie slot and security dependencies before executing a negative case, and links the real WitOS cookie/check code into a HOSTED child. A real BCrypt transport initializes the cookie before protected entry. The intact frame returns 42; one-bit cookie corruption exits 0xFFFF0004. The host uses Windows GS unwind support solely for its own image; this is not guest exception-dispatch evidence.

P1.3 primitive implementation and initialization ordering are now validated. Integration into the complete executable handoff remains the explicit P1.9 gate; kernel handler/chained metadata rejection is unchanged.
