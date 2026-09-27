#ifndef WITOS_NATIVE_PROCESS_H
#define WITOS_NATIVE_PROCESS_H
#include "tls.h"
#ifdef __cplusplus
extern "C" {
#endif
/* Bounded private CRT lifecycle. Callers quiesce workers before shutdown.
 * Raw kernel exits/faults skip process cleanup; individual thread exits clean TLS and invoke their private exit notification. */
#define WIT_NATIVE_EXIT_MAX_CALLBACKS 32U
void wit_native_process_shutdown(void);
WIT_NORETURN void wit_native_process_exit(WitU64 code);
#ifdef __cplusplus
}
#endif
#endif
