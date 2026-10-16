#include "witos/platform.h"

/* What the virt (riscv64) board does not serve yet (plan step V1.1): every platform operation the common kernel links
 * beyond the console and the test exit panics by name. V1.2 brings the clock (the time CSR and sstc), V1.2–V1.4 the PLIC,
 * the device tree's devices and the other harts through SBI. */

#pragma clang diagnostic ignored "-Wunused-parameter"

#define NOT_PORTED(name) wit_panic("QemuVirtRv: " name " is not ported (plan step V1.2)")

WitU32 wit_platform_devices(const struct WitBootInfo *boot, struct WitDeviceDescriptor *table, WitU32 capacity)
{
    NOT_PORTED("wit_platform_devices");
}

void wit_platform_timer_start(void)
{
    NOT_PORTED("wit_platform_timer_start");
}

void wit_platform_timer_stop(void)
{
    NOT_PORTED("wit_platform_timer_stop");
}

void wit_platform_timer_acknowledge(void)
{
    NOT_PORTED("wit_platform_timer_acknowledge");
}

int wit_platform_interrupt_claim(WitU32 *line)
{
    NOT_PORTED("wit_platform_interrupt_claim");
}

int wit_platform_line_valid(WitU32 line)
{
    NOT_PORTED("wit_platform_line_valid");
}

void wit_platform_line_unmask(WitU32 line)
{
    NOT_PORTED("wit_platform_line_unmask");
}

void wit_platform_line_mask(WitU32 line)
{
    NOT_PORTED("wit_platform_line_mask");
}

void wit_platform_line_complete(WitU32 line)
{
    NOT_PORTED("wit_platform_line_complete");
}

void wit_platform_clock_initialize(const struct WitBootInfo *boot)
{
    NOT_PORTED("wit_platform_clock_initialize");
}

WitU64 wit_platform_monotonic_read(void)
{
    NOT_PORTED("wit_platform_monotonic_read");
}

WitU64 wit_platform_monotonic_frequency(void)
{
    NOT_PORTED("wit_platform_monotonic_frequency");
}

int wit_platform_realtime_seconds(WitU64 *seconds)
{
    NOT_PORTED("wit_platform_realtime_seconds");
}

WitU32 wit_platform_processors(const struct WitBootInfo *boot, struct WitProcessorDescriptor *table, WitU32 capacity)
{
    NOT_PORTED("wit_platform_processors");
}

void wit_platform_processor_prepare(const struct WitBootInfo *boot, WitU32 index, WitU64 hardware_id)
{
    NOT_PORTED("wit_platform_processor_prepare");
}

void wit_platform_processor_interrupts_enable(WitU32 index, WitU64 hardware_id)
{
    NOT_PORTED("wit_platform_processor_interrupts_enable");
}

void wit_platform_ipi(WitU64 hardware_id, WitU32 kind)
{
    NOT_PORTED("wit_platform_ipi");
}

void wit_platform_ipi_complete(WitU32 number)
{
    NOT_PORTED("wit_platform_ipi_complete");
}
