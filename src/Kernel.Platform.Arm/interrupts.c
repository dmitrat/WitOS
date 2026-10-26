#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "witos/cpu.h"
#include "witos/processor.h"
#include "arm.h"

/* The scheduler tick, the device lines and the doorbell through the GIC the device tree names (H7.1): a GICv3 with a
 * single security state, its CPU interface through system registers and each processor's redistributor, or a GICv2
 * (QEMU's virt with gic-version=2, the Pi's GIC-400), its distributor and its banked CPU interface by memory. The tick
 * is the EL1 virtual timer's PPI, which the tree names too. The pages are mapped on demand as device pages.
 *
 * A GICv2 keeps the groups as the firmware left them and enables group 0 or 1 alone (bit 0 of GICD_CTLR and GICC_CTLR):
 * without the security extensions (QEMU) every interrupt stays in group 0, signalled as IRQ while GICC_CTLR.FIQEn is
 * clear; with them (a Pi's firmware puts every interrupt in group 1), a non-secure access sees group 1 alone. */

#define GICD_CTLR 0x0000U
#define GICD_CTLR_RWP (1U << 31)
#define GICD_CTLR_ENABLE_GROUP1 (1U << 1)
#define GICD_CTLR_ARE (1U << 4)
#define GICD_CTLR_ENABLE (1U << 0) /* GICv2: group 0, or group 1 seen from the non-secure side */
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
#define GICD_ITARGETSR 0x0800U /* GICv2: a byte of CPU interfaces per interrupt; the first eight words are banked */
#define GICD_SGIR 0x0F00U
#define GICD_IROUTER 0x6100U
#define GICD_IROUTER_AFFINITY 0xFF00FFFFFFULL /* Aff3 in 39:32, Aff2:Aff1:Aff0 in 23:0, as in MPIDR_EL1 */
#define GICC_CTLR 0x0000U
#define GICC_CTLR_ENABLE (1U << 0)
#define GICC_PMR 0x0004U
#define GICC_BPR 0x0008U
#define GICC_IAR 0x000CU
#define GICC_EOIR 0x0010U
#define GICC_INTID 0x3FFU /* GICC_IAR's interrupt; an SGI's bits 12:10 name its sender, which GICC_EOIR names again */
#define SPI_FIRST 32U
#define SGI_COUNT 16U
#define SGI_FENCE 0U
#define SGI_INVALIDATE 1U

#define SPURIOUS_INTERRUPT_FIRST 1020U
#define TIMER_HZ 100U
#define POLL_LIMIT 1000000U

static int mapped, distributor_enabled;
static WitU32 version, timer_interrupt;
static WitU64 distributor, cpu_interface, redistributors;
static WitU64 interval;
static WitU64 boot_affinity; /* GICv3: the boot processor's, to which every device line is routed (P1.2) */
/* GICv2: each processor's CPU interface, its bit in a target list, by hardware identity and index, as the processor
 * read it from the banked GICD_ITARGETSR0 (zero in a GIC of one interface, whose targets are fixed); and the
 * acknowledgement of the SGI each processor took last, whose sender the end of the interrupt names again. */
static WitU64 interface_ids[WIT_PROCESSOR_CAPACITY];
static WitU8 interface_masks[WIT_PROCESSOR_CAPACITY];
static WitU32 interface_known[WIT_PROCESSOR_CAPACITY];
static WitU32 acknowledged[WIT_PROCESSOR_CAPACITY];

static volatile WitU32 *gic_register(WitU64 base, WitU32 offset)
{
    return (volatile WitU32 *)(base + offset);
}

static volatile WitU8 *gic_byte(WitU64 base, WitU32 offset)
{
    return (volatile WitU8 *)(base + offset);
}

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

/* GICv2: the calling processor's CPU interface, recorded for the doorbells others send it. */
static void record_interface(WitU32 index, WitU64 hardware_id)
{
    require(index < WIT_PROCESSOR_CAPACITY, "Processor index out of range");
    interface_ids[index] = hardware_id & GICD_IROUTER_AFFINITY;
    interface_masks[index] = *gic_byte(distributor, GICD_ITARGETSR);
    interface_known[index] = 1;
}

void wit_arm_interrupts_map(const WitBootInfo *boot)
{
    const WitArmBoard *board = wit_arm_board(boot);
    require(board->GicVersion == 2 || board->GicVersion == 3, "No GIC in the device tree");
    version = board->GicVersion;
    distributor = board->Distributor;
    cpu_interface = board->CpuInterface;
    redistributors = board->Redistributors;
    timer_interrupt = board->TimerInterrupt;
    require(timer_interrupt >= SGI_COUNT && timer_interrupt < SPI_FIRST, "The timer's interrupt is no PPI");
    require(!(distributor & 4095) && (version == 3 ? !(redistributors & 0xFFFF) : !(cpu_interface & 4095)),
        "The GIC's frames are not page-aligned");
    wit_arch_map_device_page(boot, distributor);
    if (version == 3) {
        /* The routing registers of the shared peripheral interrupts (GICD_IROUTER, K3.2) lie six to eight pages in. */
        wit_arch_map_device_page(boot, distributor + 0x6000);
        wit_arch_map_device_page(boot, distributor + 0x7000);
        wit_arch_map_device_page(boot, distributor + 0x8000);
        wit_arch_map_device_page(boot, redistributors);
        wit_arch_map_device_page(boot, redistributors + WIT_ARM_GICR_SGI_OFFSET);
        boot_affinity = wit_arch_processor_id() & GICD_IROUTER_AFFINITY;
        wit_console_write("Interrupt controller: GICv3, distributor ");
        wit_console_write_hex(distributor);
        wit_console_write(", redistributors ");
        wit_console_write_hex(redistributors);
    } else {
        /* The distributor's registers, the target bytes and GICD_SGIR among them, fill its first page, and the CPU
         * interface's first page holds every register the kernel uses. */
        wit_arch_map_device_page(boot, cpu_interface);
        record_interface(0, wit_arch_processor_id());
        wit_console_write("Interrupt controller: GICv2, distributor ");
        wit_console_write_hex(distributor);
        wit_console_write(", CPU interface ");
        wit_console_write_hex(cpu_interface);
    }
    wit_console_write(", timer interrupt ");
    wit_console_write_u64(timer_interrupt);
    wit_console_write("\n");
    mapped = 1;
}

/* Once, before the first interrupt the distributor must carry, which is the timer's or, with secondary processors
 * (K7.2), the first inter-processor interrupt: a GICv3's affinity routing with group 1 forwarded, a GICv2's forwarding.
 * Nothing is enabled for a processor until it asks, so enabling the distributor early delivers nothing. */
static void distributor_enable(void)
{
    WitU32 spin = 0;
    require(
        mapped && !wit_arch_interrupts_enabled(), "Distributor enable needs the GIC mapped and interrupts disabled");
    if (distributor_enabled) {
        return;
    }
    if (version == 2) {
        *gic_register(distributor, GICD_CTLR) = GICD_CTLR_ENABLE;
        distributor_enabled = 1;
        return;
    }
    *gic_register(distributor, GICD_CTLR) = GICD_CTLR_ARE | GICD_CTLR_ENABLE_GROUP1;
    while ((*gic_register(distributor, GICD_CTLR) & GICD_CTLR_RWP) && ++spin < POLL_LIMIT) {
    }
    require(spin < POLL_LIMIT, "GIC distributor did not settle");
    distributor_enabled = 1;
}

/* The calling processor's CPU interface: every priority unmasked, its group enabled. */
static void cpu_enable(void)
{
    if (version == 3) {
        wit_arm_gic3_cpu_enable();
        return;
    }
    *gic_register(cpu_interface, GICC_PMR) = 0xFF;
    *gic_register(cpu_interface, GICC_BPR) = 0;
    *gic_register(cpu_interface, GICC_CTLR) = GICC_CTLR_ENABLE;
}

static WitU64 acknowledge(void)
{
    if (version == 3) {
        return wit_arm_gic3_acknowledge();
    }
    const WitU32 raw = *gic_register(cpu_interface, GICC_IAR), interrupt = raw & GICC_INTID;
    if (interrupt < SGI_COUNT) {
        acknowledged[wit_processors_current()] = raw;
    }
    return interrupt;
}

static void complete(WitU64 interrupt)
{
    if (version == 3) {
        wit_arm_gic3_complete(interrupt);
        return;
    }
    *gic_register(cpu_interface, GICC_EOIR) = (WitU32)interrupt;
}

/* The redistributor of processor n lies WIT_ARM_GICR_STRIDE beyond the boot processor's (K7.2). */
static WitU64 redistributor(WitU32 index)
{
    return redistributors + (WitU64)index * WIT_ARM_GICR_STRIDE;
}

static void redistributor_wake(WitU64 rd, const char *message)
{
    WitU32 spin = 0;
    *gic_register(rd, GICR_WAKER) &= ~GICR_WAKER_PROCESSOR_SLEEP;
    for (; (*gic_register(rd, GICR_WAKER) & GICR_WAKER_CHILDREN_ASLEEP) && spin < POLL_LIMIT; ++spin) {
    }
    require(spin < POLL_LIMIT, message);
}

/* Enables the calling processor's private interrupts the bits name: its redistributor's on a GICv3, the distributor's
 * banked first word on a GICv2. */
static void private_enable(WitU32 bits)
{
    if (version == 3) {
        const WitU64 sgi = redistributor(wit_processors_current()) + WIT_ARM_GICR_SGI_OFFSET;
        *gic_register(sgi, GICR_IGROUPR0) |= bits;
        for (WitU32 i = 0; i < SPI_FIRST; ++i) {
            if (bits & (1U << i)) {
                gic_byte(sgi, GICR_IPRIORITYR)[i] = 0x80;
            }
        }
        *gic_register(sgi, GICR_ISENABLER0) = bits;
        return;
    }
    for (WitU32 i = 0; i < SPI_FIRST; ++i) {
        if (bits & (1U << i)) {
            gic_byte(distributor, GICD_IPRIORITYR)[i] = 0x80;
        }
    }
    *gic_register(distributor, GICD_ISENABLER) = bits;
}

/* The tick of the calling processor (P1.3a): its own virtual timer, whose PPI it enables for itself. */
void wit_platform_timer_start(void)
{
    require(mapped && !wit_arch_interrupts_enabled(), "Timer start needs the GIC mapped and interrupts disabled");
    interval = wit_arm_counter_frequency() / TIMER_HZ;
    require(interval != 0, "Timer interval is zero");

    distributor_enable();
    if (version == 3) {
        redistributor_wake(redistributor(wit_processors_current()), "GIC redistributor did not wake");
    }
    private_enable(1U << timer_interrupt);
    cpu_enable();
    wit_arm_timer_arm(interval);
}

void wit_platform_timer_stop(void)
{
    wit_arch_disable_interrupts();
    wit_arm_timer_disable();
    if (version == 3) {
        *gic_register(redistributor(wit_processors_current()) + WIT_ARM_GICR_SGI_OFFSET, GICR_ICENABLER0) = 1U
            << timer_interrupt;
    } else {
        *gic_register(distributor, GICD_ICENABLER) = 1U << timer_interrupt;
    }
}

int wit_platform_interrupt_claim(WitU32 *line)
{
    const WitU64 interrupt = acknowledge();
    *line = 0;
    if (interrupt == timer_interrupt) {
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
        complete(interrupt);
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
    if (version == 2) {
        return; /* a GICv2's distributor writes take effect without a pending bit */
    }
    while ((*gic_register(distributor, GICD_CTLR) & GICD_CTLR_RWP) && ++spin < POLL_LIMIT) {
    }
    require(spin < POLL_LIMIT, "GIC distributor did not settle");
}

void wit_platform_line_unmask(WitU32 line)
{
    require(mapped && wit_platform_line_valid(line), "Unmasking an invalid GIC line");
    const WitU32 word = (line / 32) * 4, bit = 1U << (line % 32);
    gic_byte(distributor, GICD_IPRIORITYR)[line] = 0x80;
    /* To the boot processor, which alone takes device lines in phase P (P1.2), never to any processor. */
    if (version == 3) {
        *gic_register(distributor, GICD_IGROUPR + word) |= bit;
        *(volatile WitU64 *)(distributor + GICD_IROUTER + 8ULL * line) = boot_affinity;
    } else {
        gic_byte(distributor, GICD_ITARGETSR)[line] = interface_masks[0];
    }
    *gic_register(distributor, GICD_ISENABLER + word) = bit;
    wait_settled();
}

void wit_platform_line_mask(WitU32 line)
{
    require(mapped && wit_platform_line_valid(line), "Masking an invalid GIC line");
    *gic_register(distributor, GICD_ICENABLER + (line / 32) * 4) = 1U << (line % 32);
    wait_settled();
}

void wit_platform_line_complete(WitU32 line)
{
    complete(line);
}

void wit_platform_timer_acknowledge(void)
{
    wit_arm_timer_arm(interval);
    complete(timer_interrupt);
}

/* Secondary processors (plan step K7.2): on a GICv3 the boot processor maps both frames of a processor's redistributor
 * before the start, and the started processor wakes its redistributor; on a GICv2 the distributor's banked registers
 * and the CPU interface lie at the same addresses for every processor, and the started processor records its CPU
 * interface. Either enables the software-generated interrupts the kernel uses and its CPU interface. The
 * inter-processor interrupt is SGI 0, the doorbell (P1.2), to one processor. */

void wit_platform_processor_prepare(const WitBootInfo *boot, WitU32 index, WitU64 hardware_id)
{
    require(index != 0 && index < WIT_PROCESSOR_CAPACITY, "Secondary processor index out of range");
    distributor_enable(); /* the boot processor's software-generated interrupts need the distributor on */
    cpu_enable(); /* and its own interface; nothing is enabled for it yet */
    if (version == 2) {
        return;
    }
    /* The device tree's reg is Aff3 in bits 39:32 and Aff2:Aff1:Aff0 in bits 23:0; GICR_TYPER packs the four bytes. */
    const WitU64 affinity = (((hardware_id >> 32) & 0xFFULL) << 24) | (hardware_id & 0xFFFFFFULL);
    wit_arch_map_device_page(boot, redistributor(index));
    wit_arch_map_device_page(boot, redistributor(index) + WIT_ARM_GICR_SGI_OFFSET);
    require((*(volatile WitU64 *)(redistributor(index) + GICR_TYPER) >> 32) == affinity,
        "Redistributor frame does not belong to the processor");
}

void wit_platform_processor_interrupts_enable(WitU32 index, WitU64 hardware_id)
{
    if (version == 3) {
        redistributor_wake(redistributor(index), "Secondary redistributor did not wake");
    } else {
        record_interface(index, hardware_id);
    }
    private_enable((1U << SGI_FENCE) | (1U << SGI_INVALIDATE));
    cpu_enable();
}

/* The doorbell of witos/cpu.h is SGI 0 (P1.2): by affinity through ICC_SGI1R_EL1 on a GICv3, by the target's CPU
 * interface through GICD_SGIR on a GICv2. */
void wit_platform_ipi(WitU64 hardware_id, WitU32 kind)
{
    const WitU64 intid = SGI_FENCE;
    require(kind == WIT_IPI_DOORBELL, "Unknown inter-processor interrupt kind");
    if (version == 2) {
        const WitU64 id = hardware_id & GICD_IROUTER_AFFINITY;
        for (WitU32 i = 0; i < WIT_PROCESSOR_CAPACITY; ++i) {
            if (interface_known[i] && interface_ids[i] == id) {
                require(interface_masks[i] != 0, "A doorbell in a GIC of one CPU interface");
                wit_arm_gic2_send_sgi(distributor + GICD_SGIR, ((WitU32)interface_masks[i] << 16) | (WitU32)intid);
                return;
            }
        }
        wit_panic("A doorbell to a processor whose CPU interface is unknown");
    }
    const WitU64 aff0 = hardware_id & 0xFF, aff1 = (hardware_id >> 8) & 0xFF, aff2 = (hardware_id >> 16) & 0xFF,
                 aff3 = (hardware_id >> 32) & 0xFF;
    require(aff0 < 16, "Affinity level 0 beyond the target list");
    wit_arm_gic3_send_sgi((aff3 << 48) | (aff2 << 32) | (intid << 24) | (aff1 << 16) | (1ULL << aff0));
}

void wit_platform_ipi_complete(WitU32 number)
{
    require(number < SGI_COUNT, "Completing a line that is no inter-processor interrupt");
    if (version == 2) {
        const WitU32 raw = acknowledged[wit_processors_current()];
        require((raw & GICC_INTID) == number, "Completing an inter-processor interrupt not taken");
        complete(raw);
        return;
    }
    complete(number);
}

/* The boot processor enables the doorbell's SGI as a secondary does (P1.2). */
void wit_platform_ipi_enable_boot(void)
{
    private_enable(1U << SGI_FENCE);
}
