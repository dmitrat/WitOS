#include "witos/types.h"
extern __declspec(thread) volatile WitU64 tls_value;
extern __declspec(thread) volatile WitU64 tls_zero;
extern __declspec(thread) WitU64 *tls_pointer;
__declspec(noinline) WitU64 wit_tls_read(void) { return tls_value; }
__declspec(noinline) void wit_tls_write(WitU64 value) { tls_value = value; }
__declspec(noinline) WitU64 wit_tls_address(void) { return (WitU64)&tls_value; }
__declspec(noinline) WitU64 wit_tls_zeros(void) { return tls_zero; }
__declspec(noinline) WitU64 wit_tls_pointer(void) { return (WitU64)tls_pointer; }
