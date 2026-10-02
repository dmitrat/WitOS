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
WitU64 wit_native_thread_create_reference(WitNativeThreadMain entry, WitU64 argument, WitU64 stack_bytes, WitU64 flags,
    WitU64 native_id_output, WitU64 *reference);
/* One private runtime exit notification per thread; not a general FLS API.
 * Register during TLS initialization/normal execution. Context is opaque.
 * Notification runs after TLS cleanup (and after atexit on process shutdown). */
typedef void (*WitNativeThreadExitCallback)(void *context);
WitU64 wit_native_thread_on_exit(WitNativeThreadExitCallback callback, void *context);
/* One private platform cleanup after the runtime notification, before kernel exit.
 * Same registration/owner checks; not a second runtime attach or general FLS API. */
WitU64 wit_native_thread_on_cleanup(WitNativeThreadExitCallback callback, void *context);
void wit_native_thread_notify_exit(void);
WIT_NORETURN void wit_native_thread_exit(WitU64 code);
#ifdef __cplusplus
}
#endif
#endif
