# Native CRT memory, strings and unsigned parsing

**Status:** WitOS 0.0.39; ABI v17 unchanged.

This implements six previously unresolved native runtime dependencies: memset, memmove, memcmp, strcpy, strstr and strtoul. It does not replace CoreLib or provide a complete CRT.

## Contracts

The byte/string functions are C implementations using the real SDK declarations. They allocate nothing, need no compiler TLS or PAL initialization, and preserve errno/native last-error. Explicit volatile byte accesses and MSVC function pragmas prevent recursive CRT lowering and compiler-wide reads beyond the required range.

- memset writes exactly count bytes, using the low unsigned byte of the int value, and returns the original destination.
- memmove handles overlap in both directions and returns the original destination. It compares integer addresses in the current flat-address ABI and uses subtraction to avoid an overflowing source-plus-count test.
- memcmp examines exactly count unsigned bytes and returns the sign of the first difference. Embedded zero bytes do not terminate comparison.
- strcpy copies the terminating zero and returns the original destination. The caller must provide sufficient destination capacity; overlapping strings are outside its C contract.
- strstr returns the first match, the original haystack for an empty needle, or null. It stops at the first terminator and does not read beyond either string. The simple search has quadratic worst-case work and is a correctness-first bootstrap implementation.

Count-zero byte operations accept null pointers without access, matching the existing native memcpy extension. Nonzero buffers and strings must satisfy ordinary C pointer/range/lifetime requirements. These user-space routines are not kernel copy helpers or a boundary between native code sharing an address space. They do not infer rights from user pointers or turn invalid accesses into successful operations.

The existing C-locale unsigned parser is shared by strtoull and the new strtoul. The latter explicitly requires the current Windows-codegen 32-bit unsigned-long width. It supports ASCII whitespace/sign, bases 2-36 and base-zero octal/decimal/hex detection. It detects overflow before multiplication, continues consuming valid digits to set the correct end pointer, returns ULONG_MAX/ULLONG_MAX with ERANGE on overflow, and applies negation in the selected unsigned width only for an in-range magnitude. Success/no-conversion preserves prior errno; invalid base/null-input handling retains the existing EINVAL extension. Native last-error remains separate and unchanged.

## Source integration

crt_memory.witos.c is compiled into the full WitOS Runtime.WorkstationGC archive, with explicit C17/O1 settings. Its exact archived object is copied into the configuration probe and hash-checked before guest linking. The guest therefore executes that memory implementation, not a separately recreated test body. MSVC's memmove intrinsic is explicitly disabled for its definition, as for the other intrinsic CRT definitions.

strtoul and strtoull share the existing crt_config.witos.cpp source in the full archive and the guest CRT object. Existing 64-bit conversion tests remain active to detect regressions from sharing the parser. Windows-reference sources/CRT, package/source pins and CoreLib remain unchanged.

The source verifier rejects any of these six functions remaining unresolved. The broad boundary falls from 85 to 79 symbols; minimal executable startup falls from 79 to 73. Managed thread attachment, GC coordination, exceptions and complete guest startup remain separate requirements.

## Guest evidence

CrtMemoryAndStrings runs before image/PAL/compiler-TLS initialization in the no-TLS fixture, at both image bases. Volatile function pointers force calls to the native definitions. Checks include:

- A 324-case memmove matrix covering both overlap directions, identical pointers, disjoint spans, unaligned positions and zero lengths, with expected bytes derived independently from the original input pattern.
- memset byte truncation and surrounding canaries; memcmp count bounds, unsigned ordering and embedded zeros.
- Readonly source pages and unmapped guard pages around source/destination, including end-of-page copies/comparisons and terminators, repeated-prefix substring search, longer non-matching needles, empty strings and high-byte characters.
- Poisoned destination bytes before strcpy, explicit checks that it writes the terminator, and unchanged adjacent canaries. A no-op copy cannot satisfy the test.

CrtUnsignedLong checks 32-bit maxima, positive/negative overflow, negation, prefixes, bases, no conversion, invalid bases, long overflowing digit sequences and end-pointer placement. Two batches of three workers check errno isolation and native error preservation across yields and slot reuse. The normal TLS image also repeats the byte/string checks.

Both modes require exact thread counters, restored owned pages, empty handle/event tables and recovery of all physical pages on teardown. Raw byte tests run with no compiler TLS; number conversion uses the actual per-thread errno storage.

## memchr (P6.4.i)

The pinned microsoft/STL's vectorized algorithms call `memchr`, which `crt_memory.witos.c` now defines as well. It
reads one byte at a time and stops at the first byte equal to the low unsigned byte of the value, so a match before
the end of the object never reads past it, and a count of zero accepts a null pointer. The guarded test of
`tests/User.X64/runtime_crt.cpp` finds a terminator in a page's last byte with a count reaching into the guard page,
a byte in a readonly page, a missing byte, a truncated value (0x180) and -1. The hosted STL build links the same
source (P6.4.i, `StlTests`).

## Validation

Both runtime-config VMs passed 199 user groups and 51 contained hardware faults. The probe has fourteen source objects and a 46,080-byte import-free image with 111 plain unwind entries on the local compiler. The full WitOS runtime archive has 84 members; minipal retains 11. Source audit (64 files), hosted probe (eight groups), native/source references (four groups each) and minimal startup diagnostics passed. All 19 ordinary QEMU scenarios passed, retaining 178 user groups and 51 contained hardware faults in successful boots. Release solution builds passed without warnings or errors.

This is native platform support only. No managed guest entry, running guest collector, managed exceptions or CoreCLR/JIT is claimed.
