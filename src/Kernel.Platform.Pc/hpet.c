#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "pc.h"

/* The monotonic clock is the HPET the ACPI HPET table names (plan step H1.1; q35's fixed aperture before): its event
 * timer block in system memory, a 64-bit main counter, at least three comparators, all of them kept from raising an
 * interrupt. Its period may be any the specification allows (at most 100 ns): the frequency is 10^15 / period rounded
 * to the nearest hertz, exact where the period divides 10^15 (QEMU's 10 ns) and within 0.005 ppm of the counter's rate
 * on VMware, whose 69,841,279 fs does not divide it: a second in seven years, far below a crystal's tolerance. */
#define TABLE_BYTES 56U /* the header, the event timer block id, the base address structure, the number and the tick */
#define TABLE_ADDRESS_SPACE 40U /* the base address structure's space: 0, system memory */
#define TABLE_ADDRESS 44U
#define REGISTER_BYTES 0x400U /* the general registers and up to 24 comparators */

static WitU64 base, frequency, last_counter;
static int ready;

static WitU32 read32(WitU32 offset)
{
    return *(volatile WitU32 *)(base + offset);
}

static void write32(WitU32 offset, WitU32 value)
{
    *(volatile WitU32 *)(base + offset) = value;
}

static WitU64 counter(void)
{
    for (WitU32 attempt = 0; attempt < 8; ++attempt) {
        const WitU32 high = read32(0xF4);
        const WitU32 low = read32(0xF0);
        if (high == read32(0xF4)) {
            return ((WitU64)high << 32) | low;
        }
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

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

void wit_platform_clock_initialize(const WitBootInfo *boot)
{
    WitU32 capabilities, period, timers, length = 0;
    WitU64 start, value, ticks;
    require(!ready && !wit_arch_interrupts_enabled(), "Invalid clock initialization context");
    const WitU8 *table = wit_pc_acpi_table(boot, "HPET", &length);
    require(table && length >= TABLE_BYTES, "No HPET in the ACPI tables");
    base = wit_pc_le64(table + TABLE_ADDRESS);
    require(table[TABLE_ADDRESS_SPACE] == 0 && base && !(base & (REGISTER_BYTES - 1)), "Unsupported HPET");
    wit_arch_map_device_page(boot, base & ~4095ULL);
    capabilities = read32(0);
    period = read32(4);
    timers = ((capabilities >> 8) & 31) + 1;
    require((capabilities & 0xFF) &&
            (capabilities & (1U << 13)) &&
            timers >= 3 &&
            timers <= 24 &&
            period >= 1000000 &&
            period <= 100000000,
        "Unsupported HPET");
    frequency = (1000000000000000ULL + period / 2) / period;
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
        if (value >= 0x100000000ULL) {
            break;
        }
    }
    require(value >= 0x100000000ULL, "HPET 64-bit rollover failed");
    wit_console_write("[TEST-PASS] Clock.Counter64\n");
    reset_counter(0); // Final boot epoch. Never reset after ready is published.
    ticks = wit_arch_clock_ticks();
    start = counter();
    value = start;
    for (WitU32 i = 0; i < 10000000 && value - start < frequency / 500; ++i) {
        const WitU64 next = counter();
        require(next >= value, "HPET moved backwards");
        value = next;
    }
    require(value - start >= frequency / 500 &&
            wit_arch_clock_ticks() == ticks &&
            !wit_arch_interrupts_enabled() &&
            read32(0x10) == 1,
        "HPET did not advance with IRQs disabled");
    last_counter = value;
    ready = 1;
    wit_console_write("HPET frequency: ");
    wit_console_write_u64(frequency);
    wit_console_write(" (period ");
    wit_console_write_u64(period);
    wit_console_write(" fs at ");
    wit_console_write_hex(base);
    wit_console_write(")\n[TEST-PASS] Clock.IrqIndependent\n");
}

WitU64 wit_platform_monotonic_read(void)
{
    WitU64 value;
    require(ready && !wit_arch_interrupts_enabled(), "Monotonic read outside serialized kernel context");
    /* Every processor reads the one counter (P1.2): the last value any of them saw, loaded before this read, is never
     * above it, and the largest value seen is kept with a compare-and-exchange. */
    WitU64 last = __atomic_load_n(&last_counter, __ATOMIC_ACQUIRE);
    value = counter();
    if (value > 0x7FFFFFFFFFFFFFFFULL) {
        value = 0x7FFFFFFFFFFFFFFFULL;
    }
    require(value >= last, "HPET reset or moved backwards");
    while (value > last &&
        !__atomic_compare_exchange_n(&last_counter, &last, value, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) {
    }
    return value;
}

WitU64 wit_platform_monotonic_frequency(void)
{
    require(ready, "Clock frequency requested before initialization");
    return frequency;
}
