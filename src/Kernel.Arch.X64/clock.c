#include "x64.h"
#include "witos/platform.h"

/* This first clock is explicitly the q35 HPET at the board's fixed aperture.
 * General ACPI discovery and non-q35 targets are separate platform work. */
static WitU64 frequency, last_counter;
static int ready;
static WitU32 read32(WitU32 offset) { return *(volatile WitU32*)(WIT_X64_HPET_BASE + offset); }
static void write32(WitU32 offset, WitU32 value) { *(volatile WitU32*)(WIT_X64_HPET_BASE + offset) = value; }
static WitU64 counter(void)
{
    for (WitU32 attempt = 0; attempt < 8; ++attempt) {
        const WitU32 high = read32(0xF4);
        const WitU32 low = read32(0xF0);
        if (high == read32(0xF4)) return ((WitU64)high << 32) | low;
    }
    wit_panic("Unstable HPET counter");
}
static void reset_counter(WitU64 value)
{
    write32(0x10, 0); // No enable and no legacy replacement routing.
    write32(0xF0, (WitU32)value);
    write32(0xF4, (WitU32)(value >> 32));
    write32(0x10, 1);
}
static void require(int condition, const char* message) { if (!condition) wit_panic(message); }

void wit_platform_clock_initialize(const WitBootInfo* boot)
{
    WitU32 capabilities, period, timers;
    WitU64 start, value, ticks;
    require(!ready && !(wit_x64_read_flags() & 0x200), "Invalid clock initialization context");
    wit_x64_map_hpet(boot);
    capabilities = read32(0);
    period = read32(4);
    timers = ((capabilities >> 8) & 31) + 1;
    require((capabilities & 0xFF) && (capabilities & (1U << 13)) &&
        (capabilities >> 16) == 0x8086 && timers >= 3 && timers <= 24 &&
        period >= 1000000 && period <= 100000000 && 1000000000000000ULL % period == 0,
        "Unsupported q35 HPET");
    frequency = 1000000000000000ULL / period;
    write32(0x10, 0);
    for (WitU32 i = 0; i < timers; ++i) {
        const WitU32 reg = 0x100 + 0x20 * i;
        write32(reg, read32(reg) & ~0x4004U); // Comparator IRQ and FSB delivery disabled.
        require(!(read32(reg) & 0x4004U), "Cannot disable HPET interrupts");
    }
    write32(0x20, (1U << timers) - 1);
    /* Hardware low-word rollover probe occurs before publishing the clock. */
    reset_counter(0xFFFFFFF0ULL);
    value = 0;
    for (WitU32 i = 0; i < 1000000; ++i) {
        value = counter();
        if (value >= 0x100000000ULL) break;
    }
    require(value >= 0x100000000ULL, "HPET 64-bit rollover failed");
    wit_console_write("[TEST-PASS] Clock.Counter64\n");
    reset_counter(0); // Final boot epoch. Never reset after ready is published.
    ticks = wit_x64_clock_ticks();
    start = counter();
    value = start;
    for (WitU32 i = 0; i < 10000000 && value - start < frequency / 500; ++i) {
        const WitU64 next = counter();
        require(next >= value, "HPET moved backwards");
        value = next;
    }
    require(value - start >= frequency / 500 && wit_x64_clock_ticks() == ticks &&
        !(wit_x64_read_flags() & 0x200) && read32(0x10) == 1, "HPET did not advance with IRQs disabled");
    last_counter = value;
    ready = 1;
    wit_console_write("HPET frequency: "); wit_console_write_u64(frequency);
    wit_console_write("\n[TEST-PASS] Clock.IrqIndependent\n");
}

WitU64 wit_x64_monotonic_read(void)
{
    WitU64 value;
    require(ready && !(wit_x64_read_flags() & 0x200), "Monotonic read outside serialized kernel context");
    value = counter();
    if (value > 0x7FFFFFFFFFFFFFFFULL) value = 0x7FFFFFFFFFFFFFFFULL;
    require(value >= last_counter, "HPET reset or moved backwards");
    last_counter = value;
    return value;
}
WitU64 wit_x64_monotonic_frequency(void)
{
    require(ready, "Clock frequency requested before initialization");
    return frequency;
}
