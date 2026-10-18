#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/cpu.h"
#include "witos/platform.h"
#include "witos/rv64_csr.h"
#include "witos/rv64_sbi.h"
#include "witos/virtual.h"
#include "virt_rv.h"

/* QEMU virt (riscv64) scheduler timer and device lines (plan step V1.2). The timer is the supervisor timer interrupt:
 * armed through the sstc compare register (stimecmp) where the boot hart's ISA names the extension, through the SBI
 * timer extension otherwise, which the firmware serves from machine mode; either way the interrupt is pending while
 * the time counter has passed the value and ends when the next value is set. Device lines are the sources of the
 * platform-level interrupt controller (PLIC), which signals the hart's supervisor context as the external interrupt:
 * a source is enabled per context, carries a priority (zero never fires), and is claimed and completed through the
 * context's claim register. The three PLIC pages the boot hart's context needs are mapped on demand as device pages. A
 * secondary hart (V1.4) gets its context's pages mapped and its threshold masking every priority, since no device line
 * reaches it until phase P; the inter-processor interrupts are the supervisor software interrupt, which the firmware
 * raises for SBI send_ipi and the hart clears in sip, with the kind the sender left for it in memory. */

#define PLIC_PRIORITY 0x0U /* 4 bytes a source */
#define PLIC_ENABLE 0x2000U /* a bit a source, 0x80 bytes a context */
#define PLIC_ENABLE_STRIDE 0x80U
#define PLIC_CONTEXT 0x200000U /* threshold then claim/complete, 0x1000 bytes a context */
#define PLIC_CONTEXT_STRIDE 0x1000U
#define PLIC_THRESHOLD 0x0U
#define PLIC_CLAIM 0x4U
#define PLIC_SOURCES 96U /* the board's riscv,ndev is 95: sources 1 to 95 */
#define PLIC_THRESHOLD_ALL 7U /* the highest threshold: no priority passes */
#define PLIC_HARTS 32U /* the contexts this board maps: two a hart */
#define TIMER_HZ 100U
#define TIMER_NEVER (~0ULL)

static int mapped, sstc;
static WitU32 context;
static WitU64 interval;
static volatile WitU32 pending_ipi[PLIC_HARTS]; /* the kind of the inter-processor interrupt sent to each hart */

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static volatile WitU32 *plic(WitU64 offset)
{
    return (volatile WitU32 *)(WIT_VIRT_RV_PLIC_BASE + offset);
}

static WitU64 context_base_of(WitU32 which)
{
    return PLIC_CONTEXT + (WitU64)which * PLIC_CONTEXT_STRIDE;
}

static WitU64 context_base(void)
{
    return context_base_of(context);
}

/* The supervisor context of a hart is 2 * hart + 1; the enables of the first 32 contexts share one page. */
static WitU32 context_of(WitU64 hart)
{
    return (WitU32)(2 * hart + 1);
}

static void map_once(const WitBootInfo *boot, WitU64 page)
{
    if (wit_virtual_view(page) == 0) {
        wit_arch_map_device_page(boot, page);
    }
}

static volatile WitU32 *enable_word(WitU32 line)
{
    return plic(PLIC_ENABLE + (WitU64)context * PLIC_ENABLE_STRIDE + (line / 32) * 4);
}

/* The boot hart's supervisor context is 2 * hart + 1 (its machine context is 2 * hart). */
void wit_virt_rv_interrupts_map(const WitBootInfo *boot)
{
    const WitVirtRvTree *tree = wit_virt_rv_tree(boot);
    const WitU64 hart = wit_arch_processor_id();
    require(hart < 32, "Boot hart beyond the PLIC contexts this board maps");
    context = (WitU32)(2 * hart + 1);
    for (WitU32 i = 0; i < tree->HartCount; ++i) {
        if (tree->Harts[i].Id == hart) {
            sstc = tree->Harts[i].Sstc != 0;
        }
    }
    wit_arch_map_device_page(boot, WIT_VIRT_RV_PLIC_BASE + PLIC_PRIORITY);
    wit_arch_map_device_page(
        boot, (WIT_VIRT_RV_PLIC_BASE + PLIC_ENABLE + (WitU64)context * PLIC_ENABLE_STRIDE) & ~4095ULL);
    wit_arch_map_device_page(boot, WIT_VIRT_RV_PLIC_BASE + context_base());
    mapped = 1;
    wit_console_write(sstc ? "Timer: stimecmp (sstc), PLIC context " : "Timer: SBI set_timer, PLIC context ");
    wit_console_write_u64(context);
    wit_console_write("\n");
}

static void arm_timer(WitU64 at)
{
    if (sstc) {
        wit_rv64_write_stimecmp(at);
    } else {
        require(wit_rv64_sbi(WIT_RV64_SBI_TIME, WIT_RV64_SBI_TIME_SET, at, 0, 0, 0) == 0, "SBI set_timer failed");
    }
}

void wit_platform_timer_start(void)
{
    require(mapped && !wit_arch_interrupts_enabled(), "Timer start needs the PLIC mapped and interrupts disabled");
    interval = wit_platform_monotonic_frequency() / TIMER_HZ;
    require(interval != 0, "Timer interval is zero");
    *plic(context_base() + PLIC_THRESHOLD) = 0; /* every enabled source's priority passes */
    arm_timer(wit_rv64_read_time() + interval);
    wit_rv64_set_sie(WIT_RV64_SIE_TIMER | WIT_RV64_SIE_EXTERNAL);
}

void wit_platform_timer_stop(void)
{
    wit_arch_disable_interrupts();
    wit_rv64_clear_sie(WIT_RV64_SIE_TIMER);
    arm_timer(TIMER_NEVER);
}

void wit_platform_timer_acknowledge(void)
{
    arm_timer(wit_rv64_read_time() + interval);
}

/* The external interrupt: the context's claim register names the pending source of the highest priority and claims it,
 * or reads zero when nothing is pending. The kernel masks, completes and delivers a source (K3.2). */
int wit_platform_interrupt_claim(WitU32 *line)
{
    require(mapped, "Interrupt claim before the PLIC is mapped");
    if (wit_rv64_read_sip() & WIT_RV64_SIE_SOFTWARE) {
        /* An inter-processor interrupt on any hart (V1.4, P1.2): the kind the sender left for it, the doorbell, completed
         * by wit_platform_ipi_complete. */
        const WitU64 hart = wit_arch_processor_id();
        require(hart < PLIC_HARTS && pending_ipi[hart] != 0, "A software interrupt no sender left a kind for");
        *line = pending_ipi[hart];
        return 3;
    }
    const WitU32 source = *plic(context_base() + PLIC_CLAIM);
    *line = source;
    return source != 0 ? 2 : 0;
}

/* Device lines are the PLIC's sources (plan step K3.2's role). */
int wit_platform_line_valid(WitU32 line)
{
    return line >= 1 && line < PLIC_SOURCES;
}

/* The enable bit first, then the priority (V2.4): a source that asserted while it was masked is pending, and QEMU's
 * PLIC looks at what is pending again on a write of a priority, a threshold or the claim register, never on a write of
 * an enable word, so that a priority written first left such an interrupt waiting for the next line's activity. */
void wit_platform_line_unmask(WitU32 line)
{
    require(mapped && wit_platform_line_valid(line), "Unmasking an invalid PLIC line");
    *enable_word(line) |= 1U << (line % 32);
    *plic(PLIC_PRIORITY + (WitU64)line * 4) = 1;
}

void wit_platform_line_mask(WitU32 line)
{
    require(mapped && wit_platform_line_valid(line), "Masking an invalid PLIC line");
    *enable_word(line) &= ~(1U << (line % 32));
}

void wit_platform_line_complete(WitU32 line)
{
    require(mapped && wit_platform_line_valid(line), "Completing an invalid PLIC line");
    *plic(context_base() + PLIC_CLAIM) = line;
}

/* A secondary hart's context (V1.4): its enables' page, shared with the boot hart's context, and its own threshold and
 * claim page, mapped before the hart starts. */
void wit_platform_processor_prepare(const WitBootInfo *boot, WitU32 index, WitU64 hardware_id)
{
    require(index != 0 && index < WIT_PROCESSOR_CAPACITY && hardware_id < PLIC_HARTS,
        "Secondary hart beyond the PLIC contexts this board maps");
    const WitU32 which = context_of(hardware_id);
    map_once(boot, (WIT_VIRT_RV_PLIC_BASE + PLIC_ENABLE + (WitU64)which * PLIC_ENABLE_STRIDE) & ~4095ULL);
    map_once(boot, WIT_VIRT_RV_PLIC_BASE + context_base_of(which));
}

/* No device line reaches a secondary hart until phase P: its context's threshold masks every priority and its enables
 * stay clear; the inter-processor interrupt needs nothing of the PLIC. */
void wit_platform_processor_interrupts_enable(WitU32 index, WitU64 hardware_id)
{
    require(index != 0 && index < WIT_PROCESSOR_CAPACITY && hardware_id < PLIC_HARTS,
        "Secondary hart beyond the PLIC contexts");
    *plic(context_base_of(context_of(hardware_id)) + PLIC_THRESHOLD) = PLIC_THRESHOLD_ALL;
}

/* The kind for the hart, then the firmware's software interrupt to it: send_ipi(mask, base) raises the supervisor
 * software interrupt of each hart base + bit. */
void wit_platform_ipi(WitU64 hardware_id, WitU32 kind)
{
    require(kind == WIT_IPI_DOORBELL, "Unknown inter-processor interrupt kind");
    require(hardware_id < PLIC_HARTS, "An inter-processor interrupt to a hart beyond the board's");
    pending_ipi[hardware_id] = kind;
    wit_rv64_fence();
    require(wit_rv64_sbi(WIT_RV64_SBI_IPI, WIT_RV64_SBI_IPI_SEND, 1, hardware_id, 0, 0) == 0, "SBI send_ipi failed");
}

/* The boot hart takes the doorbell too, its supervisor software interrupt (P1.2). */
void wit_platform_ipi_enable_boot(void)
{
    wit_rv64_set_sie(WIT_RV64_SIE_SOFTWARE);
}

void wit_platform_ipi_complete(WitU32 number)
{
    require(number == WIT_IPI_DOORBELL, "Completing a line that is no inter-processor interrupt");
    wit_rv64_clear_sip(WIT_RV64_SIE_SOFTWARE); /* the same bit in sip */
}
