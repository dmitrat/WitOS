# P1.4: Boot entropy, random service and system-seeded cookie

The q35 runner now provides rng-builtin plus virtio-rng-pci, without deterministic -seed/replay options. The firmware adapter obtains 32 bytes through EFI_RNG_PROTOCOL before GetMemoryMap/ExitBootServices. Missing/failing RNG aborts boot; no clocks, IDs or deterministic test seeds substitute for entropy. The UEFI-specific declarations stay in Boot.Uefi.

Boot contract v3 transfers only a native pointer/size to a private writable seed buffer. The kernel validates the seed range against writable, non-executable image sections, consumes it once and wipes it. Firmware memory stays reserved. No seed/key/cookie bytes are printed. UEFI's RNG contract provides the entropy guarantee; output-difference tests are liveness checks, not entropy estimates.

The common kernel implements the RFC 8439 ChaCha20 block function with a 256-bit key. For every at-most-64-byte internal request, block 0 privately derives the next key and block 1 supplies output. The prior key is replaced and temporary state is wiped. Calls are serialized with interrupts disabled on the sole online CPU. There is no post-boot reseed or VM snapshot/fork recovery service in this prototype; a new boot requires fresh firmware entropy. The primitive passes the RFC 8439 section 2.3.2 known-answer vector.

Experimental user ABI v19 adds RANDOM(buffer, size, reserved=0). It validates the complete writable destination before consuming generator state or emitting any bytes; mappings remain serialized through copy-out. Zero bytes is a no-op. A 65536-byte work quota bounds each syscall, and the exact maximum is tested successfully. Failed size/flag/range requests return zero copied bytes and do not advance generator generation. No-access commitments remain owned but unwritable.

Native BCryptGenRandom supports the actually reached system-preferred provider contract: null algorithm handle and BCRYPT_USE_SYSTEM_PREFERRED_RNG. Direct/import bindings are equivalent and the import slot is readonly. Unsupported providers/flags, invalid buffers and quota excess return explicit NTSTATUS failures. Native last-error and errno are preserved. This does not implement the rest of BCrypt's algorithm-provider APIs.

wit_native_security_initialize_system obtains its cookie input through the real kernel random syscall before image publication or compiler TLS constructors. It rejects reinitialization and retries only the two excluded 48-bit cookie values, then invokes the real initializer; it never falls back to a known value. Deterministic seeds remain confined to separate GS mechanism tests.

## Evidence

- Isolated firmware probe: success with the entropy device; LocateProtocol failure without it; no byte logging.
- All 20 ordinary boot scenarios passed, including fail-closed no-rng and the existing CPU/memory/timeout checks.
- Four runtime-config profiles passed 220 user groups and 54 expected contained faults each, including the exact 64 KiB random request.
- Kernel checks compare actual generator advancement with successful request byte/block accounting, including the system cookie seed. Invalid requests and zero-length requests cannot silently consume state.
- Guest cases cover null/overflow/readonly/import-slot destinations, uncommitted/readonly/no-access second pages without partial output, valid cross-page output, direct/import calls, workers, preserved error state and no compiler TLS.
- Source audit, hosted probe, source-built reference, strict target/startup links and Release build passed. At this checkpoint the minimal/broad boundaries are 56/62 unresolved symbols. This remains native adapter evidence, not guest managed execution.

## Sources

[UEFI RNG protocol](https://uefi.org/specs/UEFI/2.10/37_Secure_Technologies.html#random-number-generator-protocol), [RFC 8439](https://www.rfc-editor.org/rfc/rfc8439.html#section-2.3.2), [QEMU invocation](https://www.qemu.org/docs/master/system/invocation.html). QEMU's rng-builtin delegates to guest-random; its ordinary path uses crypto randomness, while explicit deterministic seeding/replay changes that behavior. The runner supplies neither option. The installed QEMU/firmware package remains pinned by Toolchain.cs.
