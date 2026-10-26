#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "pc.h"
#include "witos/x64_instructions.h"

/* The monotonic clock's reference is the HPET the ACPI HPET table names (plan step H1.1; q35's fixed aperture before):
 * its event timer block in system memory, a 64-bit main counter, at least three comparators, all of them kept from
 * raising an interrupt. Its period may be any the specification allows (at most 100 ns): the frequency is 10^15 / period
 * rounded to the nearest hertz, exact where the period divides 10^15 (QEMU's 10 ns) and within 0.005 ppm of the
 * counter's rate on VMware, whose 69,841,279 fs does not divide it: a second in seven years, far below a crystal's
 * tolerance.
 *
 * The monotonic clock itself is the time stamp counter where it runs at a constant rate (plan step H5.3): CPUID names it
 * invariant, or the processor runs under a hypervisor, which keeps a guest's counter constant. A read of the HPET is
 * three loads of a device register, and on VMware each of them leaves the guest: 85 us a clock read, which every call
 * that waits or wakes paid. The counter's rate is the hypervisor's timing leaf where it has one (VMware's and KVM's
 * 0x40000010, in kHz), otherwise measured against the HPET over 50 ms, each end the HPET read inside the narrowest pair
 * of counter reads; the counter is shifted right until its rate is at most 10^9, the bound the conversions to UTC and
 * the libc's to nanoseconds keep within 64 bits. The HPET stays the clock where the counter's rate is not constant. */
#define TABLE_BYTES 56U /* the header, the event timer block id, the base address structure, the number and the tick */
#define TABLE_ADDRESS_SPACE 40U /* the base address structure's space: 0, system memory */
#define TABLE_ADDRESS 44U
#define REGISTER_BYTES 0x400U /* the general registers and up to 24 comparators */

static WitU64 base, frequency, last_counter;
static int ready;
static int tsc; /* the monotonic clock is the time stamp counter's */
static WitU64 tsc_origin; /* the counter when the clock started */
static WitU32 tsc_shift;
static WitU64 tsc_hertz; /* the counter's rate before the shift */
static const char *tsc_source;

#define CALIBRATION_DIVISOR 20U /* 50 ms of the HPET */
#define TSC_MINIMUM_HERTZ 100000000ULL
#define TSC_MAXIMUM_HERTZ 20000000000ULL
#define MONOTONIC_MAXIMUM_HERTZ 1000000000ULL

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

/* The time stamp counter at the moment of an HPET read: the read inside the narrowest of a few pairs of counter reads,
 * the counter taken at the pair's middle. */
static void sample(WitU64 *counter_at, WitU64 *hpet)
{
    WitU64 narrowest = ~0ULL;
    for (WitU32 attempt = 0; attempt < 8; ++attempt) {
        const WitU64 before = wit_x64_read_tsc();
        const WitU64 value = counter();
        const WitU64 after = wit_x64_read_tsc();
        if (after - before < narrowest) {
            narrowest = after - before;
            *counter_at = before + (after - before) / 2;
            *hpet = value;
        }
    }
}

/* The time stamp counter's rate: the hypervisor's timing leaf, or the counter measured against the HPET; zero when the
 * counter's rate is not constant. */
static WitU64 tsc_rate(void)
{
    int r[4];
    wit_x64_cpuid(r, 1);
    const int hypervisor = ((WitU32)r[2] >> 31) & 1;
    wit_x64_cpuid(r, (int)0x80000000U);
    int invariant = 0;
    if ((WitU32)r[0] >= 0x80000007U) {
        wit_x64_cpuid(r, (int)0x80000007U);
        invariant = ((WitU32)r[3] >> 8) & 1;
    }
    if (!invariant && !hypervisor) {
        return 0;
    }
    if (hypervisor) {
        wit_x64_cpuid(r, 0x40000000);
        if ((WitU32)r[0] >= 0x40000010U) {
            wit_x64_cpuid(r, 0x40000010);
            if ((WitU32)r[0]) {
                tsc_source = "the hypervisor's timing leaf";
                return (WitU64)(WitU32)r[0] * 1000ULL;
            }
        }
    }
    WitU64 start_tsc = 0, start_hpet = 0, end_tsc = 0, end_hpet = 0;
    sample(&start_tsc, &start_hpet);
    while (counter() - start_hpet < frequency / CALIBRATION_DIVISOR) {
    }
    sample(&end_tsc, &end_hpet);
    if (end_tsc <= start_tsc || end_hpet <= start_hpet) {
        return 0;
    }
    tsc_source = "measured against the HPET";
    return (end_tsc - start_tsc) * frequency / (end_hpet - start_hpet);
}

/* The time stamp counter as the monotonic clock where its rate is constant and known (H5.3); what was measured outside
 * 0.1-20 GHz keeps the HPET. */
static void choose_tsc(void)
{
    const WitU64 hertz = tsc_rate();
    if (hertz < TSC_MINIMUM_HERTZ || hertz > TSC_MAXIMUM_HERTZ) {
        wit_console_write("Monotonic clock: the HPET (no time stamp counter of a constant, known rate)\n");
        return;
    }
    tsc_hertz = hertz;
    while ((tsc_hertz >> tsc_shift) > MONOTONIC_MAXIMUM_HERTZ) {
        ++tsc_shift;
    }
    tsc_origin = wit_x64_read_tsc();
    last_counter = 0;
    tsc = 1;
    wit_console_write("Monotonic clock: the time stamp counter at ");
    wit_console_write_u64(tsc_hertz);
    wit_console_write(" Hz (");
    wit_console_write(tsc_source);
    wit_console_write("), ");
    wit_console_write_u64(tsc_hertz >> tsc_shift);
    wit_console_write(" Hz after a shift by ");
    wit_console_write_u64(tsc_shift);
    wit_console_write("\n");
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
    wit_console_write("HPET frequency: ");
    wit_console_write_u64(frequency);
    wit_console_write(" (period ");
    wit_console_write_u64(period);
    wit_console_write(" fs at ");
    wit_console_write_hex(base);
    wit_console_write(")\n[TEST-PASS] Clock.IrqIndependent\n");
    choose_tsc();
    ready = 1;
}

WitU64 wit_platform_monotonic_read(void)
{
    WitU64 value;
    require(ready && !wit_arch_interrupts_enabled(), "Monotonic read outside serialized kernel context");
    /* Every processor reads the one counter (P1.2): the last value any of them saw, loaded before this read, is never
     * above it, and the largest value seen is kept with a compare-and-exchange. */
    WitU64 last = __atomic_load_n(&last_counter, __ATOMIC_ACQUIRE);
    if (tsc) {
        const WitU64 now = wit_x64_read_tsc(); /* a processor behind the origin reads zero, then the last value */
        value = now > tsc_origin ? (now - tsc_origin) >> tsc_shift : 0;
    } else {
        value = counter();
    }
    if (value > 0x7FFFFFFFFFFFFFFFULL) {
        value = 0x7FFFFFFFFFFFFFFFULL;
    }
    /* The HPET is one counter for every processor; their time stamp counters may stand a little apart (H5.3), and a
     * read below the last value seen gives that value. */
    if (tsc && value < last) {
        return last;
    }
    require(value >= last, "HPET reset or moved backwards");
    while (value > last &&
        !__atomic_compare_exchange_n(&last_counter, &last, value, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) {
    }
    return value > last ? value : last; /* another processor published a later value meanwhile */
}

WitU64 wit_platform_monotonic_frequency(void)
{
    require(ready, "Clock frequency requested before initialization");
    return tsc ? tsc_hertz >> tsc_shift : frequency;
}
