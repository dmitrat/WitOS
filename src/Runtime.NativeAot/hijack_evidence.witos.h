#ifndef WITOS_HIJACK_EVIDENCE_H
#define WITOS_HIJACK_EVIDENCE_H
#include "witos/types.h"

// Private bring-up counters, not an application capability or suspension API.
// Writers are serialized by the real runtime ThreadStore suspension protocol.
struct WitHijackEvidence {
    WitU64 Attempts, Redirects, ReturnHijacks, UnsafeSnapshots;
};

WitHijackEvidence wit_pal_hijack_evidence();
#endif
