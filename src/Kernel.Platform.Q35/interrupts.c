#include "witos/boot.h"
#include "witos/cpu.h"
#include "witos/platform.h"
#include "witos/x64_instructions.h"

/* q35 legacy interrupt routing: the 8259 PIC pair and the 8254 PIT as the scheduler tick source. */

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

/* The interrupt mask registers as written: IRQ0 alone until a device line is bound (plan step K3.2). */
static WitU8 mask_master = 0xFF, mask_slave = 0xFF;

static void io_wait(void)
{
    wit_x64_out8(0x80, 0);
}

void wit_platform_timer_start(void)
{
    const WitU64 apic = wit_x64_read_msr(0x1B);
    require((apic & (1ULL << 10)) == 0, "x2APIC is unsupported by the bootstrap timer");
    /* The PC backend uses the legacy PIC/PIT path. With one processor the local APIC is disabled so that it
     * does not retain firmware routing; with secondary processors online (K7.2) the architecture enabled it
     * with the PIC's line through LINT0, and it carries the inter-processor interrupts. */
    if (wit_cpus_online() == 1) {
        wit_x64_write_msr(0x1B, apic & ~(1ULL << 11));
    }
    wit_x64_out8(0x21, 0xFF);
    wit_x64_out8(0xA1, 0xFF);
    wit_x64_out8(0x20, 0x11);
    io_wait();
    wit_x64_out8(0xA0, 0x11);
    io_wait();
    wit_x64_out8(0x21, 0x20);
    io_wait();
    wit_x64_out8(0xA1, 0x28);
    io_wait();
    wit_x64_out8(0x21, 0x04);
    io_wait();
    wit_x64_out8(0xA1, 0x02);
    io_wait();
    wit_x64_out8(0x21, 0x01);
    io_wait();
    wit_x64_out8(0xA1, 0x01);
    io_wait();
    wit_x64_out8(0x21, 0xFF);
    wit_x64_out8(0xA1, 0xFF);
    /* Channel 0, low/high count, mode 2; approximately 100 Hz. */
    wit_x64_out8(0x43, 0x34);
    wit_x64_out8(0x40, (WitU8)(11932 & 255));
    wit_x64_out8(0x40, (WitU8)(11932 >> 8));
    mask_master = 0xFE; /* Only IRQ0. */
    mask_slave = 0xFF;
    wit_x64_out8(0x21, mask_master);
    wit_x64_out8(0xA1, mask_slave);
}

void wit_platform_timer_stop(void)
{
    wit_x64_disable_interrupts();
    mask_master = 0xFF;
    mask_slave = 0xFF;
    wit_x64_out8(0x21, mask_master);
    wit_x64_out8(0xA1, mask_slave);
}

/* Device lines are the PIC inputs 1 to 15 except the cascade; a slave line needs the cascade input open. */
int wit_platform_line_valid(WitU32 line)
{
    return line < 16 && line != 0 && line != 2;
}

static void write_masks(void)
{
    if (mask_slave != 0xFF) {
        mask_master &= 0xFBU; /* the cascade input open */
    } else {
        mask_master |= 4U;
    }
    wit_x64_out8(0x21, mask_master);
    wit_x64_out8(0xA1, mask_slave);
}

void wit_platform_line_unmask(WitU32 line)
{
    require(wit_platform_line_valid(line), "Unmasking an invalid PIC line");
    if (line < 8) {
        mask_master &= (WitU8)(0xFFU ^ (1U << line));
    } else {
        mask_slave &= (WitU8)(0xFFU ^ (1U << (line - 8)));
    }
    write_masks();
}

void wit_platform_line_mask(WitU32 line)
{
    require(wit_platform_line_valid(line), "Masking an invalid PIC line");
    if (line < 8) {
        mask_master |= (WitU8)(1U << line);
    } else {
        mask_slave |= (WitU8)(1U << (line - 8));
    }
    write_masks();
}

void wit_platform_line_complete(WitU32 line)
{
    if (line >= 8) {
        wit_x64_out8(0xA0, 0x20); /* Non-specific EOI to the slave, then the master. */
    }
    wit_x64_out8(0x20, 0x20);
}

void wit_platform_timer_acknowledge(void)
{
    wit_x64_out8(0x20, 0x20); /* Non-specific EOI to the master PIC. */
}

/* Secondary processors (K7.2): q35's inter-processor interrupts are the local APIC's, the architecture's business;
 * the PIC path has nothing to prepare or enable per processor. */
void wit_platform_processor_prepare(const WitBootInfo *boot, WitU32 index, WitU64 hardware_id)
{
    (void)boot;
    (void)index;
    (void)hardware_id;
}

void wit_platform_processor_interrupts_enable(WitU32 index, WitU64 hardware_id)
{
    (void)index;
    (void)hardware_id;
}

void wit_platform_ipi(WitU64 hardware_id, WitU32 kind)
{
    (void)hardware_id;
    (void)kind;
    wit_panic("The q35 platform has no inter-processor interrupt of its own");
}

void wit_platform_ipi_complete(WitU32 number)
{
    (void)number;
    wit_panic("The q35 platform has no inter-processor interrupt of its own");
}
