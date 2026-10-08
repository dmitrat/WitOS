#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "witos/cpu.h"
#include "virt.h"

/* QEMU virt scheduler timer: the EL1 virtual timer (PPI 27) through a GICv3 with a single security state. The
 * distributor and the boot processor's redistributor are mapped on demand as device pages. */

#define GICD_CTLR 0x0000U
#define GICD_CTLR_RWP (1U << 31)
#define GICD_CTLR_ENABLE_GROUP1 (1U << 1)
#define GICD_CTLR_ARE (1U << 4)
#define GICR_TYPER 0x0008U /* 64-bit: the affinity of the processor the redistributor serves in bits 63:32 */
#define GICR_WAKER 0x0014U
#define GICR_WAKER_PROCESSOR_SLEEP (1U << 1)
#define GICR_WAKER_CHILDREN_ASLEEP (1U << 2)
#define GICR_IGROUPR0 0x0080U
#define GICR_ISENABLER0 0x0100U
#define GICR_ICENABLER0 0x0180U
#define GICR_IPRIORITYR 0x0400U
#define GICD_IGROUPR 0x0080U
#define GICD_ISENABLER 0x0100U
#define GICD_ICENABLER 0x0180U
#define GICD_IPRIORITYR 0x0400U
#define GICD_IROUTER 0x6100U
#define GICD_IROUTER_ANY (1ULL << 31)
#define SPI_FIRST 32U
#define SGI_COUNT 16U
#define SGI_FENCE 0U
#define SGI_INVALIDATE 1U

#define TIMER_INTERRUPT 27U
#define SPURIOUS_INTERRUPT_FIRST 1020U
#define TIMER_HZ 100U
#define POLL_LIMIT 1000000U

static int mapped, distributor_enabled;
static WitU64 interval;

static volatile WitU32 *gic_register(WitU64 base, WitU32 offset)
{
    return (volatile WitU32 *)(base + offset);
}

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

void wit_virt_interrupts_map(const WitBootInfo *boot)
{
    wit_arch_map_device_page(boot, WIT_VIRT_GICD_BASE);
    /* The routing registers of the shared peripheral interrupts (GICD_IROUTER, K3.2) lie six to eight pages in. */
    wit_arch_map_device_page(boot, WIT_VIRT_GICD_BASE + 0x6000);
    wit_arch_map_device_page(boot, WIT_VIRT_GICD_BASE + 0x7000);
    wit_arch_map_device_page(boot, WIT_VIRT_GICD_BASE + 0x8000);
    wit_arch_map_device_page(boot, WIT_VIRT_GICR_BASE);
    wit_arch_map_device_page(boot, WIT_VIRT_GICR_SGI_BASE);
    mapped = 1;
}

/* Affinity routing with group 1 forwarded: once, before the first interrupt the distributor must carry, which is
 * the timer's or, with secondary processors (K7.2), the first inter-processor interrupt. Nothing is enabled at a
 * redistributor until its processor asks, so enabling the distributor early delivers nothing. */
static void distributor_enable(void)
{
    WitU32 spin = 0;
    require(
        mapped && !wit_arch_interrupts_enabled(), "Distributor enable needs the GIC mapped and interrupts disabled");
    if (distributor_enabled) {
        return;
    }
    *gic_register(WIT_VIRT_GICD_BASE, GICD_CTLR) = GICD_CTLR_ARE | GICD_CTLR_ENABLE_GROUP1;
    while ((*gic_register(WIT_VIRT_GICD_BASE, GICD_CTLR) & GICD_CTLR_RWP) && ++spin < POLL_LIMIT) {
    }
    require(spin < POLL_LIMIT, "GIC distributor did not settle");
    distributor_enabled = 1;
}

void wit_platform_timer_start(void)
{
    WitU32 spin = 0;
    require(mapped && !wit_arch_interrupts_enabled(), "Timer start needs the GIC mapped and interrupts disabled");
    interval = wit_virt_counter_frequency() / TIMER_HZ;
    require(interval != 0, "Timer interval is zero");

    distributor_enable();

    *gic_register(WIT_VIRT_GICR_BASE, GICR_WAKER) &= ~GICR_WAKER_PROCESSOR_SLEEP;
    for (spin = 0; (*gic_register(WIT_VIRT_GICR_BASE, GICR_WAKER) & GICR_WAKER_CHILDREN_ASLEEP) && spin < POLL_LIMIT;
        ++spin) {
    }
    require(spin < POLL_LIMIT, "GIC redistributor did not wake");

    *gic_register(WIT_VIRT_GICR_SGI_BASE, GICR_IGROUPR0) |= 1U << TIMER_INTERRUPT;
    ((volatile WitU8 *)(WIT_VIRT_GICR_SGI_BASE + GICR_IPRIORITYR))[TIMER_INTERRUPT] = 0x80;
    *gic_register(WIT_VIRT_GICR_SGI_BASE, GICR_ISENABLER0) = 1U << TIMER_INTERRUPT;

    wit_virt_gic_cpu_enable();
    wit_virt_timer_arm(interval);
}

void wit_platform_timer_stop(void)
{
    wit_arch_disable_interrupts();
    wit_virt_timer_disable();
    *gic_register(WIT_VIRT_GICR_SGI_BASE, GICR_ICENABLER0) = 1U << TIMER_INTERRUPT;
}

int wit_platform_interrupt_claim(WitU32 *line)
{
    const WitU64 interrupt = wit_virt_gic_acknowledge();
    *line = 0;
    if (interrupt == TIMER_INTERRUPT) {
        return 1; /* Completed by wit_platform_timer_acknowledge. */
    }
    if (interrupt >= SPI_FIRST && interrupt < SPURIOUS_INTERRUPT_FIRST) {
        *line = (WitU32)interrupt; /* The kernel masks and completes it. */
        return 2;
    }
    if (interrupt < SGI_COUNT) {
        *line = (WitU32)interrupt; /* An inter-processor interrupt (K7.2), completed by wit_platform_ipi_complete. */
        return 3;
    }
    if (interrupt < SPURIOUS_INTERRUPT_FIRST) {
        wit_virt_gic_complete(interrupt);
    }
    return 0;
}

/* Device lines are the shared peripheral interrupts of the distributor (plan step K3.2). */
int wit_platform_line_valid(WitU32 line)
{
    return line >= SPI_FIRST && line < SPURIOUS_INTERRUPT_FIRST;
}

static void wait_settled(void)
{
    WitU32 spin = 0;
    while ((*gic_register(WIT_VIRT_GICD_BASE, GICD_CTLR) & GICD_CTLR_RWP) && ++spin < POLL_LIMIT) {
    }
    require(spin < POLL_LIMIT, "GIC distributor did not settle");
}

void wit_platform_line_unmask(WitU32 line)
{
    require(mapped && wit_platform_line_valid(line), "Unmasking an invalid GIC line");
    const WitU32 word = (line / 32) * 4, bit = 1U << (line % 32);
    *gic_register(WIT_VIRT_GICD_BASE, GICD_IGROUPR + word) |= bit;
    ((volatile WitU8 *)(WIT_VIRT_GICD_BASE + GICD_IPRIORITYR))[line] = 0x80;
    *(volatile WitU64 *)(WIT_VIRT_GICD_BASE + GICD_IROUTER + 8ULL * line) = GICD_IROUTER_ANY;
    *gic_register(WIT_VIRT_GICD_BASE, GICD_ISENABLER + word) = bit;
    wait_settled();
}

void wit_platform_line_mask(WitU32 line)
{
    require(mapped && wit_platform_line_valid(line), "Masking an invalid GIC line");
    *gic_register(WIT_VIRT_GICD_BASE, GICD_ICENABLER + (line / 32) * 4) = 1U << (line % 32);
    wait_settled();
}

void wit_platform_line_complete(WitU32 line)
{
    wit_virt_gic_complete(line);
}

void wit_platform_timer_acknowledge(void)
{
    wit_virt_timer_arm(interval);
    wit_virt_gic_complete(TIMER_INTERRUPT);
}

/* Secondary processors (plan step K7.2): the redistributor of processor n lies WIT_VIRT_GICR_STRIDE beyond the boot
 * processor's; the boot processor maps both frames before the start, and the started processor wakes its
 * redistributor, enables the two software-generated interrupts the kernel uses and its CPU interface. An
 * inter-processor interrupt is SGI 0 (fence) or 1 (invalidate) to one processor by its affinity. */
static WitU64 redistributor(WitU32 index)
{
    return WIT_VIRT_GICR_BASE + (WitU64)index * WIT_VIRT_GICR_STRIDE;
}

void wit_platform_processor_prepare(const WitBootInfo *boot, WitU32 index, WitU64 hardware_id)
{
    /* The device tree's reg is Aff3 in bits 39:32 and Aff2:Aff1:Aff0 in bits 23:0; GICR_TYPER packs the four bytes. */
    const WitU64 affinity = (((hardware_id >> 32) & 0xFFULL) << 24) | (hardware_id & 0xFFFFFFULL);
    require(index != 0 && index < WIT_PROCESSOR_CAPACITY, "Secondary processor index out of range");
    distributor_enable(); /* the boot processor's software-generated interrupts need affinity routing on */
    wit_virt_gic_cpu_enable(); /* and its own interface through the system registers; nothing is enabled for it yet */
    wit_arch_map_device_page(boot, redistributor(index));
    wit_arch_map_device_page(boot, redistributor(index) + WIT_VIRT_GICR_SGI_OFFSET);
    require((*(volatile WitU64 *)(redistributor(index) + GICR_TYPER) >> 32) == affinity,
        "Redistributor frame does not belong to the processor");
}

void wit_platform_processor_interrupts_enable(WitU32 index, WitU64 hardware_id)
{
    const WitU64 rd = redistributor(index), sgi = rd + WIT_VIRT_GICR_SGI_OFFSET;
    WitU32 spin = 0;
    (void)hardware_id;
    *gic_register(rd, GICR_WAKER) &= ~GICR_WAKER_PROCESSOR_SLEEP;
    for (; (*gic_register(rd, GICR_WAKER) & GICR_WAKER_CHILDREN_ASLEEP) && spin < POLL_LIMIT; ++spin) {
    }
    require(spin < POLL_LIMIT, "Secondary redistributor did not wake");
    *gic_register(sgi, GICR_IGROUPR0) |= (1U << SGI_FENCE) | (1U << SGI_INVALIDATE);
    ((volatile WitU8 *)(sgi + GICR_IPRIORITYR))[SGI_FENCE] = 0x80;
    ((volatile WitU8 *)(sgi + GICR_IPRIORITYR))[SGI_INVALIDATE] = 0x80;
    *gic_register(sgi, GICR_ISENABLER0) = (1U << SGI_FENCE) | (1U << SGI_INVALIDATE);
    wit_virt_gic_cpu_enable();
}

void wit_platform_ipi(WitU64 hardware_id, WitU32 kind)
{
    const WitU64 intid = kind == WIT_IPI_FENCE ? SGI_FENCE : SGI_INVALIDATE;
    const WitU64 aff0 = hardware_id & 0xFF, aff1 = (hardware_id >> 8) & 0xFF, aff2 = (hardware_id >> 16) & 0xFF,
                 aff3 = (hardware_id >> 32) & 0xFF;
    require(kind == WIT_IPI_FENCE || kind == WIT_IPI_INVALIDATE, "Unknown inter-processor interrupt kind");
    require(aff0 < 16, "Affinity level 0 beyond the target list");
    wit_virt_gic_send_sgi((aff3 << 48) | (aff2 << 32) | (intid << 24) | (aff1 << 16) | (1ULL << aff0));
}

void wit_platform_ipi_complete(WitU32 number)
{
    require(number < SGI_COUNT, "Completing a line that is no inter-processor interrupt");
    wit_virt_gic_complete(number);
}
