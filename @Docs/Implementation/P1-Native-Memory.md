# P1.5 native memory bindings

VirtualAlloc/VirtualFree direct and readonly import bindings now execute against owned WitOS reservations. The reached CoreLib FrozenObjectHeapManager path (null-address reserve, explicit-address incremental commit, exact-base release with zero size) is implemented without Windows memory implementations.

The adapter supports reserve, commit, combined null-address reserve/commit, reset and explicit-range decommit. Byte intervals are rounded to whole pages with overflow checks; allocation alignment is 64 KiB. Reserve consumes no backing RAM. Existing commitments retain data/protection on recommit, including no-access commitments. Reset keeps commitment/permissions and eagerly zeroes backing. Failed combined commit releases its new reservation while preserving the original last-error. Only the exact reservation base can be released. Native errno is untouched.

Fixed-address reservation, executable/guard/copy-on-write modes, special allocation flags and zero-size decommit discovery remain explicitly unsupported. These are not silently substituted. This is the prototype memory profile already used by the PAL, not a full Windows memory manager.

Exact new adapter/binding objects are verified in the full native source archive and copied/hash-checked into the guest. Tests cover reserve accounting, cross-page byte rounding, data/protection preservation, reset, decommit/recommit zeroing, invalid release/flags/overflow, commit-OOM rollback, import-slot protection, no-access accounting and worker ownership. Raw compiler-TLS-free execution preserves native last-error; normal workers also preserve errno.

Validation: Release build; 20 boot scenarios; source/reference/target/readiness; four runtime-config boots with 222 user groups and 54 contained faults each. The new mandatory hosted compiler-GS reference passed intact return (42) and corrupted cookie termination (0xFFFF0004) after actual system entropy initialization. Runtime source archive: 101 members; configuration archive: 27 objects. Minimal/broad unresolved boundary: 54/60. Guest managed execution and the rest of P1 remain pending.
