#ifndef WITOS_NATIVE_LIBRARY_LIFECYCLE_H
#define WITOS_NATIVE_LIBRARY_LIFECYCLE_H
#include "bootstrap.h"
#ifdef __cplusplus
extern "C" {
#endif
WitU64 wit_native_library_execute_lifecycle(WitU64);
/* Call from the main image after quiescing users/readers; shutdown closes the
 * DLL namespace and performs user-space detach with nonnull reserved argument. */
WitU64 wit_native_library_shutdown(void);
WitU64 wit_native_library_thread_enter(void);
WitU64 wit_native_library_thread_leave(void);
#ifdef __cplusplus
}
#endif
#endif
