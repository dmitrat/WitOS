#ifndef WITOS_NATIVE_SECURITY_H
#define WITOS_NATIVE_SECURITY_H
#ifdef __cplusplus
extern "C" {
#endif
#include "bootstrap.h"
/* Startup-only: caller must supply real entropy before any protected frame.
 * Test seeds validate check mechanics, not production entropy/readiness. */
void wit_native_security_initialize(WitU64 entropy);
void wit_native_security_initialize_system(void);
WIT_NORETURN void wit_native_security_failure(void);
extern WitU64 __security_cookie;
extern WitU64 __security_cookie_complement;
extern volatile WitU32 wit_native_security_state;
#ifdef __cplusplus
}
#endif
#endif
