#ifndef WITOS_PLATFORM_H
#define WITOS_PLATFORM_H

#include "types.h"

struct WitBootInfo;

void wit_console_initialize(void);
void wit_console_write(const char *text);
void wit_console_write_u64(WitU64 value);
void wit_console_write_hex(WitU64 value);
void wit_disable_interrupts(void);
WIT_NORETURN void wit_platform_enter(const struct WitBootInfo *boot);
void wit_platform_initialize(void);
void wit_platform_fault_test(void);
void wit_scheduler_self_test(void);
WIT_NORETURN void wit_platform_finish(WitU32 code);
WIT_NORETURN void wit_panic(const char *reason);

#endif
