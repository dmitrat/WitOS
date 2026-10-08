#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "virt.h"

/* QEMU virt scheduler timer: the EL1 virtual timer (PPI 27) through a GICv3 with a single security state. The
 * distributor and the boot processor's redistributor are mapped on demand as device pages. */

#define GICD_CTLR 0x0000U
#define GICD_CTLR_RWP (1U << 31)
#define GICD_CTLR_ENABLE_GROUP1 (1U << 1)
#define GICD_CTLR_ARE (1U << 4)
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

#define TIMER_INTERRUPT 27U
#define SPURIOUS_INTERRUPT_FIRST 1020U
#define TIMER_HZ 100U
#define POLL_LIMIT 1000000U

static int mapped;
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

void wit_platform_timer_start(void)
{
    WitU32 spin = 0;
    require(mapped && !wit_arch_interrupts_enabled(), "Timer start needs the GIC mapped and interrupts disabled");
    interval = wit_virt_counter_frequency() / TIMER_HZ;
    require(interval != 0, "Timer interval is zero");

    *gic_register(WIT_VIRT_GICD_BASE, GICD_CTLR) = GICD_CTLR_ARE | GICD_CTLR_ENABLE_GROUP1;
    while ((*gic_register(WIT_VIRT_GICD_BASE, GICD_CTLR) & GICD_CTLR_RWP) && ++spin < POLL_LIMIT) {
    }
    require(spin < POLL_LIMIT, "GIC distributor did not settle");

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
