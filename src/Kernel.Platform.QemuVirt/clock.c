#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "virt.h"

/* QEMU virt monotonic clock: the generic counter at CNTFRQ_EL0. The counter register is 64 bits wide and read in
 * one instruction, so reads cannot tear; it is a monotonic domain, not UTC. The real-time clock of the virt profile
 * is the PL031 at its fixed base, like the console: its data register holds the seconds since 1970 (K6). */

#define PL031_DATA 0x000U

static WitU64 frequency;

void wit_platform_clock_initialize(const struct WitBootInfo *boot)
{
    WitU64 previous;
    WitU64 now;
    WitU64 ticks;

    if (wit_arch_interrupts_enabled()) {
        wit_panic("Clock initialization requires interrupts disabled");
    }
    frequency = wit_virt_counter_frequency();
    if (frequency == 0) {
        wit_panic("Generic counter frequency is not set");
    }
    previous = wit_virt_counter();
    now = wit_virt_counter();
    if (now < previous) {
        wit_panic("Generic counter is not monotonic");
    }
    wit_console_write("[TEST-PASS] Clock.Counter64\n");

    /* Two milliseconds of counter time pass with interrupts masked and no timer tick. */
    ticks = wit_arch_clock_ticks();
    const WitU64 target = previous + frequency / 500;
    do {
        now = wit_virt_counter();
        if (now < previous) {
            wit_panic("Generic counter went backwards");
        }
        previous = now;
    } while (now < target);
    if (wit_arch_clock_ticks() != ticks || wit_arch_interrupts_enabled()) {
        wit_panic("Generic counter depends on interrupts");
    }
    wit_console_write("[TEST-PASS] Clock.IrqIndependent\n");
    wit_virt_interrupts_map(boot);
    wit_arch_map_device_page(boot, WIT_VIRT_PL031_BASE);
    wit_console_write("Counter frequency: ");
    wit_console_write_u64(frequency);
    wit_console_write("\n");
}

WitU64 wit_platform_monotonic_read(void)
{
    return wit_virt_counter();
}

WitU64 wit_platform_monotonic_frequency(void)
{
    return frequency;
}

int wit_platform_realtime_seconds(WitU64 *seconds)
{
    const WitU32 value = *(volatile WitU32 *)(WIT_VIRT_PL031_BASE + PL031_DATA);
    *seconds = value;
    return value != 0;
}
