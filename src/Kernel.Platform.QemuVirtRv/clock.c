#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "witos/rv64_csr.h"
#include "virt_rv.h"

/* QEMU virt (riscv64) monotonic clock (plan step V1.2): the time counter (the time CSR, rdtime), 64 bits read in one
 * instruction, running at the timebase frequency the device tree names; a monotonic domain, not UTC. The real-time
 * clock of the board is the goldfish RTC at its fixed base: nanoseconds since 1970 in two words, the high word latched
 * by a read of the low one (K6). */

#define RTC_TIME_LOW 0x00U
#define RTC_TIME_HIGH 0x04U
#define NANOSECONDS 1000000000ULL

static WitU64 frequency;

void wit_platform_clock_initialize(const struct WitBootInfo *boot)
{
    WitU64 previous;
    WitU64 now;
    WitU64 ticks;

    if (wit_arch_interrupts_enabled()) {
        wit_panic("Clock initialization requires interrupts disabled");
    }
    const WitVirtRvTree *tree = wit_virt_rv_tree(boot);
    frequency = tree->TimebaseFrequency;
    if (frequency == 0) {
        wit_panic("Device tree names no timebase frequency");
    }
    previous = wit_rv64_read_time();
    now = wit_rv64_read_time();
    if (now < previous) {
        wit_panic("Time counter is not monotonic");
    }
    wit_console_write("[TEST-PASS] Clock.Counter64\n");

    /* Two milliseconds of counter time pass with interrupts disabled and no timer tick. */
    ticks = wit_arch_clock_ticks();
    const WitU64 target = previous + frequency / 500;
    do {
        now = wit_rv64_read_time();
        if (now < previous) {
            wit_panic("Time counter went backwards");
        }
        previous = now;
    } while (now < target);
    if (wit_arch_clock_ticks() != ticks || wit_arch_interrupts_enabled()) {
        wit_panic("Time counter depends on interrupts");
    }
    wit_console_write("[TEST-PASS] Clock.IrqIndependent\n");
    wit_virt_rv_interrupts_map(boot);
    wit_arch_map_device_page(boot, WIT_VIRT_RV_RTC_BASE);
    wit_console_write("Counter frequency: ");
    wit_console_write_u64(frequency);
    wit_console_write("\n");
}

WitU64 wit_platform_monotonic_read(void)
{
    return wit_rv64_read_time();
}

WitU64 wit_platform_monotonic_frequency(void)
{
    return frequency;
}

int wit_platform_realtime_seconds(WitU64 *seconds)
{
    const WitU32 low = *(volatile WitU32 *)(WIT_VIRT_RV_RTC_BASE + RTC_TIME_LOW);
    const WitU32 high = *(volatile WitU32 *)(WIT_VIRT_RV_RTC_BASE + RTC_TIME_HIGH);
    const WitU64 nanoseconds = ((WitU64)high << 32) | low;
    *seconds = nanoseconds / NANOSECONDS;
    return *seconds != 0;
}
