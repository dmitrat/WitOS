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
/* Whether the console's last byte ended a line: a write that continues a line takes no new prefix. */
int wit_console_line_start(void);

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

/* The scheduler tick of the calling processor (P1.3a: each processor has its own, started, acknowledged and stopped
 * on it), and the monotonic clock. */
void wit_platform_timer_start(void);
void wit_platform_timer_stop(void);
void wit_platform_timer_acknowledge(void);

/* Boards whose interrupts share one processor vector (Arm GIC): claims the pending interrupt and returns 1 when it
 * is the scheduler timer, which wit_platform_timer_acknowledge then completes, 2 when it is a device line, written
 * to *line for the kernel to mask, complete and deliver, and 0 for anything else, completed here. Boards with one
 * vector per interrupt need not provide it. */
int wit_platform_interrupt_claim(WitU32 *line);

/* Interrupt lines of devices (plan step K3.2), numbered as the board's descriptors name them (WitDeviceLine): a
 * valid line can be bound; unmask enables its delivery, mask disables it, complete acknowledges the controller
 * after the kernel took the interrupt. All run with interrupts disabled. */
int wit_platform_line_valid(WitU32 line);
void wit_platform_line_unmask(WitU32 line);
void wit_platform_line_mask(WitU32 line);
void wit_platform_line_complete(WitU32 line);
void wit_platform_clock_initialize(const struct WitBootInfo *boot);
WitU64 wit_platform_monotonic_read(void);
WitU64 wit_platform_monotonic_frequency(void);
/* The board's real-time clock once, after the clock initialization: seconds since 1970-01-01 UTC; 0 when the board
 * has none or its value is implausible (K6). */
int wit_platform_realtime_seconds(WitU64 *seconds);
/* The board's processors once at boot (K7.1): their hardware identities and whether the firmware marks them
 * usable, in the firmware's order; the count enumerated, at most the capacity. */
struct WitProcessorDescriptor;
WitU32 wit_platform_processors(const struct WitBootInfo *boot, struct WitProcessorDescriptor *table, WitU32 capacity);
/* Secondary processors (K7.2): the boot processor maps a processor's interrupt controller frames before the start;
 * the started processor enables its own interrupts; an inter-processor interrupt of a kind of witos/cpu.h goes to a
 * processor by its hardware identity, and the receiver completes it by the number the claim returned (3). */
void wit_platform_processor_prepare(const struct WitBootInfo *boot, WitU32 index, WitU64 hardware_id);
void wit_platform_processor_interrupts_enable(WitU32 index, WitU64 hardware_id);
void wit_platform_ipi(WitU64 hardware_id, WitU32 kind);
void wit_platform_ipi_complete(WitU32 number);
/* The boot processor takes the doorbell too, once others are online (P1.2): what its interrupt controller needs. */
void wit_platform_ipi_enable_boot(void);

/* The end of a boot (ADR 0029, plan step H1.2a): the board's test exit with the code (0x10 success, 0x11 failure),
 * which a QEMU board's runner catches; where it is absent, a success powers the board off when the board can, and
 * otherwise the processor halts with a line that says why. */
WIT_NORETURN void wit_platform_finish(WitU32 code);
/* A reset of the board the root task asked for (WIT_ROOT_EXIT_RESET); returns only where the board cannot reset. */
void wit_platform_reset(void);
WIT_NORETURN void wit_panic(const char *reason);

#endif
