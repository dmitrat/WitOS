#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "arm.h"

/* The monotonic clock: the generic counter at CNTFRQ_EL0. The counter register is 64 bits wide and read in one
 * instruction, so reads cannot tear; it is a monotonic domain, not UTC. The real-time clock is the PL031 the device tree
 * names (QEMU's virt has one, K6; H7.1): its data register holds the seconds since 1970; a board without one reports
 * none. */

#define PL031_DATA 0x000U

static WitU64 frequency, realtime;

void wit_platform_clock_initialize(const struct WitBootInfo *boot)
{
    WitU64 previous;
    WitU64 now;
    WitU64 ticks;

    if (wit_arch_interrupts_enabled()) {
        wit_panic("Clock initialization requires interrupts disabled");
    }
    frequency = wit_arm_counter_frequency();
    if (frequency == 0) {
        wit_panic("Generic counter frequency is not set");
    }
    previous = wit_arm_counter();
    now = wit_arm_counter();
    if (now < previous) {
        wit_panic("Generic counter is not monotonic");
    }
    wit_console_write("[TEST-PASS] Clock.Counter64\n");

    /* Two milliseconds of counter time pass with interrupts masked and no timer tick. */
    ticks = wit_arch_clock_ticks();
    const WitU64 target = previous + frequency / 500;
    do {
        now = wit_arm_counter();
        if (now < previous) {
            wit_panic("Generic counter went backwards");
        }
        previous = now;
    } while (now < target);
    if (wit_arch_clock_ticks() != ticks || wit_arch_interrupts_enabled()) {
        wit_panic("Generic counter depends on interrupts");
    }
    wit_console_write("[TEST-PASS] Clock.IrqIndependent\n");
    wit_arm_interrupts_map(boot);
    realtime = wit_arm_board(boot)->RealTimeClock;
    if (realtime) {
        wit_arch_map_device_page(boot, realtime & ~4095ULL);
    }
    wit_console_write("Counter frequency: ");
    wit_console_write_u64(frequency);
    wit_console_write("\n");
}

WitU64 wit_platform_monotonic_read(void)
{
    return wit_arm_counter();
}

WitU64 wit_platform_monotonic_frequency(void)
{
    return frequency;
}

int wit_platform_realtime_seconds(WitU64 *seconds)
{
    const WitU32 value = realtime ? *(volatile WitU32 *)(realtime + PL031_DATA) : 0;
    *seconds = value;
    return value != 0;
}
