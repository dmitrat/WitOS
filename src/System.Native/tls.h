#ifndef WITOS_NATIVE_TLS_H
#define WITOS_NATIVE_TLS_H
#ifdef __cplusplus
extern "C" {
#endif
#include "bootstrap.h"
/* Private single-module user-space lifecycle, not NativeAOT ThreadStore. */
#define WIT_NATIVE_TLS_MAX_INITIALIZERS 32U
#define WIT_NATIVE_TLS_MAX_DESTRUCTORS 32U
void wit_native_tls_initialize(const WitUserStartup *startup);
void wit_native_tls_enter(void);
void wit_native_tls_leave(void);
int wit_native_tls_code_pointer(WitU64 address);
typedef WitU64 (*WitNativeThreadMain)(WitU64 argument);
WitU64 wit_native_thread_create(WitNativeThreadMain entry, WitU64 argument, WitU64 *handle);
WitU64 wit_native_thread_create_detached(WitNativeThreadMain entry, WitU64 argument);
WIT_NORETURN void wit_native_thread_exit(WitU64 code);
#ifdef __cplusplus
}
#endif
#endif
