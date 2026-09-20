#ifndef WITOS_NATIVE_ERROR_H
#define WITOS_NATIVE_ERROR_H
#include "witos/types.h"
#ifdef __cplusplus
extern "C" {
#endif
/* Direct native aliases of the x64 GetLastError/SetLastError implementation. */
WitU32 wit_native_error_get(void);
void wit_native_error_set(WitU32 value);
#ifdef __cplusplus
}
#endif
#endif
