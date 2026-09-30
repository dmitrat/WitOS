# P1.6 UTF conversion bindings

The initial WitOS native encoding profile uses UTF-8 for CP_UTF8, CP_ACP and CP_THREAD_ACP. It has no mutable ANSI codepage or per-thread locale service. Other codepages fail explicitly. Standard CoreLib remains unchanged.

## Reached upstream contracts

At pinned runtime b82454cad0aaaae3db2cf18fbf2cccc36e201ccc, `System/String.cs::CreateStringForSByteConstructor` calls MultiByteToWideChar with CP_ACP and MB_PRECOMPOSED, first for sizing and then for conversion. `Internal/Console.Windows.cs::WriteCore` calls WideCharToMultiByte with the console UTF-8 codepage, explicit input length, flags zero and no default-character pointers. These are actual ILC relocation roots in the earlier startup boundary report.

`native_encoding.witos.cpp` implements both conversions. Architecture assembly supplies direct entrypoints and readonly import slots. The production archive objects, including their hashes, are copied into the guest test fixture through the existing exact-object verification path. The implementation retains the runtime GS profile and uses /Od for the plain-unwind test profile; it uses no heap, compiler TLS, locale storage or OS conversion library.

Positive input lengths preserve embedded NULs and consume exactly the indicated units. Length -1 includes the terminator. Capacity zero queries the required size. UTF-16 surrogate pairs and all Unicode scalar values are encoded, isolated surrogates are replaced or rejected according to the strict flag. Malformed UTF-8 uses the measured Windows replacement grouping, including second-byte range violations and truncated prefixes. Strict errors report ERROR_NO_UNICODE_TRANSLATION.

Sizing and validation precede output. Short buffers and invalid encoding in strict mode preserve the output. Counts are checked against INT_MAX; the writing pass separately checks remaining capacity. Buffers belong to the native caller and must stay valid and stable for the call, as with the existing CRT routines. Partial/full overlap is rejected. This is a user-space conversion routine, not an atomic kernel copy-out service. It does not turn invalid native pointers into safe managed exceptions.

Historical MB_PRECOMPOSED/MB_COMPOSITE/MB_USEGLYPHCHARS and the accepted WC conversion flags do not request normalization for UTF-8. Windows accepts those known bits even though the API documentation describes the smaller UTF-8 flag set. The complete 0..2047 flag matrix is therefore checked against the actual hosted API for all three supported codepage selectors. The strict bits retain their actual effect. Unknown flags fail. UTF-8 default-character pointers are rejected; there is no lossy legacy-codepage conversion. Successful operations preserve native last-error; errno is independent.

## Evidence

`runtime-encoding` builds a Windows reference with an embedded UTF-8 activeCodePage manifest and first verifies GetACP()==CP_UTF8. It compares 9,456,444 cases: every Unicode scalar and isolated UTF-16 surrogate, strict/replacement sizing/writing, all two-byte sequences, 100,000 deterministic malformed/random byte and UTF-16 inputs, NUL/count behavior, short capacities and the flag matrix. Outputs, return values and last-error are compared; errno and destination bounds are separately asserted. Failure destination preservation is an explicit WitOS property, not an assumption that Windows always preserves partial output.

Guest coverage is in `runtime_encoding.cpp`, called by console fixture modes 68/69 and all three workers. It includes direct/import equivalence, readonly import slots, UTF-8 active-codepage aliases, the exact CoreLib MB_PRECOMPOSED call, malformed input, strict errors, output preservation, query sizes, explicit/terminated input ending at a guard page, and destinations ending at a guard page. The kernel checks a completion value, worker lifecycle, allocation recovery and the existing console counters; the runner requires User.NativeUtfConversions. Mode 69 has no compiler TLS.

Validation passed: Release build, all 20 boot scenarios, runtime-audit/probe/target/source/readiness and four QEMU runtime profiles (232 user groups, 54 expected contained faults each). The full native archive has 113 members; the configuration archive has 32 exact objects. Minimal/broad strict links retain 37/43 unresolved dependencies. The thread/UTF fixture is 27136 bytes with 70 plain unwind records. UTF objects are linked into that fixture only; the older CPU fixture remains at 122 records under its unchanged 128-record limit. P1.6 is not complete. Authoritative logs are under `artifacts/runtime-encoding/`, `artifacts/p1-encoding-config.log`, and `artifacts/p1-encoding-test.log`. No guest managed execution is claimed.

## Sources

- [MultiByteToWideChar API contract](https://learn.microsoft.com/en-us/windows/win32/api/stringapiset/nf-stringapiset-multibytetowidechar)
- [WideCharToMultiByte API contract](https://learn.microsoft.com/en-us/windows/win32/api/stringapiset/nf-stringapiset-widechartomultibyte)
- [Windows UTF-8 process codepage](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/use-utf8-code-page)
- Pinned local upstream String.cs and Internal/Console.Windows.cs; source and ILC provenance remain in runtime-source/readiness reports.
