# P1.3 native math and compiler profile (partial)

The first math checkpoint below preceded secure formatting and GS integration. Those primitive checks are now complete; see P1-Security-Cookie.md and P1-Entropy.md. Full driver/exception integration remains P1.9/P1.8. All work remains unpublished.

The WitOS native archive now uses the upstream per-target CLR_CONTROL_FLOW_GUARD / CLR_EH_CONTINUATION properties with value OFF. No fake dispatch symbol is introduced. The actual minimal ILC response must also omit --guard:cf; Windows reference must retain its upstream CFG compile profile. /GS is retained throughout the ordinary runtime sources. This is an explicit no-Windows-CFG bring-up profile, not CFI or CET support.

The source archive supplies real log and the MSVC floating-use marker _fltused (0x9875). log uses the pinned OpenLibm e_log.c at 9fbeafcd4f1b6ef6aa3946c1c8faead50f38a94d, with canonical-byte SHA-256 verification of source and license. The original numerical body/constants are unchanged. Header/compiler glue uses binary64 word access; the optional long-double alias is omitted. A separate wrapper implements ERANGE for signed zero and EDOM for negative finite/infinite input, preserving errno otherwise and leaving native last-error alone.

The two objects use strict FP and the plain-unwind /Od profile. MSVC warning C4723 is scoped off only around the intentional IEEE exceptional divisions in upstream log; /WX remains enabled. Guest tests check divide-by-zero/invalid MXCSR flags rather than accepting only the returned NaN/infinity.

Actual archive objects are verified byte-for-byte, copied and hash-checked in the guest platform fixture. Ninety-six independent expected binary64 results were produced with Python decimal at 100 digits, from exact binary64 inputs. They include subnormals, maximum finite values, values around one and around normalization boundaries, plus deterministic sampled inputs. A one-ULP bound, exact log(1), NaN/Infinity/signed-zero behavior, errno/native-error separation and three native workers across yields are checked. Static compiler TLS exists in the before-constructor test; no no-TLS errno support is claimed.

Validation: Release build, 19 ordinary boot scenarios, source audit (66 .NET files plus OpenLibm source/license), hosted probe, native target/source/reference and four runtime-config profiles passed. Each runtime profile passed 214 user groups and 54 expected contained faults. Full runtime archive: 91 members; minipal: 11; configuration probe: 21 source objects. The broad strict boundary is 67 symbols, minimal startup is 61. This is native evidence, not managed guest execution.

## Formatting investigation

The reached native formatting dependency is gccommon.cpp log_init_error_to_host: a 256-byte buffer, _vsnprintf_s with _TRUNCATE, C locale and integer/string/fixed-float diagnostics including %.3f.

stb_sprintf at 2c980bb59875b0d32144a71867fbdebb2f77cd20 was evaluated, but NOT adopted: a hosted differential check found round-half behavior differing from Windows CRT (0.0625 at %.3f and 2.5 at %.0f). Its source remains only in ignored research artifacts.

A new private fixed-decimal converter is under development. It decodes binary64, computes mantissa * 5^precision in a bounded 1280-bit integer, applies the binary shift and explicit rounding, then emits decimal digits. It has no allocation, locale, floating arithmetic, or CRT math dependency. This converter is not yet linked into the runtime and does not close the secure formatting dependency. Non-nearest reference comparisons exposed a separate Windows CRT oracle discrepancy for tiny negative values; those cases require independent exact-decimal validation before acceptance.

## Subsequent formatting result

A real bounded C-locale secure formatter now supports the reached GC diagnostic grammar, including native integer widths, strings, fixed decimal output, width/precision, truncation and errno. Legacy nearest/away and explicit standard rounding are distinguished by the actual CRT options; the earlier default-CRT comparison alone did not establish the options=0 contract. The underlying integer conversion also passed 5120 independent exact-decimal cases across all five rounding policies. This additional exact-decimal experiment currently lives in ignored research artifacts.

The reproducible RuntimeFormattingReference runs 49484 comparisons with explicit options 0/4/32/36, initial round-to-nearest FP state, and matching Windows CRT calls. A separate guest semantic probe covers readonly/end-page input, guarded destination, truncation, native integer widths, rounding-control discovery, invalid formats, errno and workers. Full runtime formatter objects remain /GS protected; their GS execution is separate from the probe's /GS- compile profile. Unsupported locale, wide/scientific/hex-float/positional/%n formats or prototype bounds return explicit failure rather than fake output. This is not a general Windows CRT implementation.
