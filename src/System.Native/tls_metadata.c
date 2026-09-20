#include "witos/types.h"
/* Static module metadata. The WitOS user entry drives dynamic TLS lifecycle;
 * the PE callback list remains empty, so arbitrary loader callbacks stay rejected. */
#pragma section(".tls", long, read, write)
#pragma section(".tls$ZZZ", long, read, write)
__declspec(allocate(".tls")) char _tls_start = 0;
__declspec(allocate(".tls$ZZZ")) char _tls_end = 0;
WitU32 _tls_index;
typedef struct TlsDirectory {
    const void *Start, *End, *Index, *Callbacks;
    WitU32 ZeroFill, Characteristics;
} TlsDirectory;
const TlsDirectory _tls_used = { &_tls_start, &_tls_end, &_tls_index, 0, 0, 0 };
