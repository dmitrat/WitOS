#ifndef WITOS_PLATFORM_H
#define WITOS_PLATFORM_H

#include "types.h"

struct WitBootInfo;

/* Board console: initialization and one byte out. src/Kernel/console.c formats on top of it. */
void wit_console_initialize(void);
void wit_platform_console_put(WitU8 value);

void wit_console_write(const char *text);
void wit_console_write_u64(WitU64 value);
void wit_console_write_buffer(const WitU8 *data, WitU32 size);
void wit_console_write_hex(WitU64 value);

/* Device pages the board uses before it can map pages on demand, such as a memory-mapped console. The
 * architecture maps them as device memory when it installs the kernel's translation tables. Returns the count,
 * at most capacity. */
WitU32 wit_platform_boot_devices(WitU64 *pages, WitU32 capacity);

/* The board's devices (RFC 0011 section 7.7, plan step K3.1): enumerated once after paging is active, from the
 * firmware tables the boot contract carries or the board's own configuration mechanism, into descriptors the kernel
 * publishes unchanged. Returns the count, at most capacity; a board that found no bus returns zero. The platform
 * maps the pages it reads through wit_arch_map_device_page and never keeps a device mapped for the kernel. */
struct WitDeviceDescriptor;
WitU32 wit_platform_devices(const struct WitBootInfo *boot, struct WitDeviceDescriptor *table, WitU32 capacity);

/* Board timer delivering scheduler ticks, and the monotonic clock. */
void wit_platform_timer_start(void);
void wit_platform_timer_stop(void);
void wit_platform_timer_acknowledge(void);

/* Boards whose interrupts share one processor vector (Arm GIC): claims the pending interrupt and returns nonzero
 * when it is the scheduler timer, which wit_platform_timer_acknowledge then completes; other interrupts are
 * completed here. Boards with one vector per interrupt need not provide it. */
int wit_platform_interrupt_claim(void);
void wit_platform_clock_initialize(const struct WitBootInfo *boot);
WitU64 wit_platform_monotonic_read(void);
WitU64 wit_platform_monotonic_frequency(void);

/* Test exit device; halts where it is absent. */
WIT_NORETURN void wit_platform_finish(WitU32 code);
WIT_NORETURN void wit_panic(const char *reason);

#endif
