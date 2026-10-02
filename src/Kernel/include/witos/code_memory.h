#ifndef WITOS_CODE_MEMORY_H
#define WITOS_CODE_MEMORY_H
#include "types.h"
#define WIT_CODE_MEMORY_VERSION 1U
#define WIT_CODE_RESERVE 0U
#define WIT_CODE_ALIAS 1U
#define WIT_CODE_PROTECT 2U
#define WIT_CODE_PUBLISH 3U
#define WIT_CODE_MAP_SPARSE 4U
#define WIT_CODE_RESET_SPARSE 5U
#define WIT_CODE_VALIDATE 6U
#define WIT_CODE_READ_EXECUTE 5U
/* Private evolving runtime ABI. Upper address bound is exclusive. Requests are
 * copied before validation/mutation; only reserve returns a nonzero result. */
typedef struct WitCodeMemoryRequest {
    WitU32 Version,Size,Operation,Protection;
    WitU64 Address,Source,Bytes,Alignment,Minimum,Maximum;
} WitCodeMemoryRequest;
WIT_STATIC_ASSERT(sizeof(WitCodeMemoryRequest)==64,"Code memory request ABI");
#endif
