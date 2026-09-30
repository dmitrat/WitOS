#ifndef WITOS_EXCEPTION_CLASSIFICATION_H
#define WITOS_EXCEPTION_CLASSIFICATION_H
#include "witos/types.h"
#define WIT_GP_UNSUPPORTED 0U
#define WIT_GP_ACCESS_UNKNOWN 1U
#define WIT_GP_PRIVILEGED 2U
#ifdef __cplusplus
extern "C" {
#endif
/* Selected x64 GP forms, at most 15 initialized instruction bytes. Unknown
 * forms remain unsupported; this does not reconstruct a memory address. */
WitU32 wit_x64_classify_gp(const WitU8* bytes,WitU32 size,WitU64 error);
#ifdef __cplusplus
}
#endif
#endif
