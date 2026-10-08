#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/cpu.h"
#include "witos/memory.h"
#include "witos/platform.h"
#include "witos/processor.h"
#include "user.h"
#include "x64.h"

/* Secondary processors of x64 (plan step K7.2) through the local APIC. The boot processor enables its own APIC in
 * xAPIC mode with the PIC's line kept open (LINT0 as ExtINT, so the PIT tick still arrives), claims one page of
 * usable memory below 1 MiB for the trampoline a processor starts in (real mode at the page, protected mode, long
 * mode with the kernel's root table, then the 64-bit entry of secondary_entry.asm with the kernel's number), and
 * sends INIT and two startup interrupts. A started processor loads the kernel's tables without a task register,
 * copies the boot processor's control registers, enables its APIC with every local vector masked, reports itself
 * ready and halts with interrupts enabled; the two inter-processor vectors (0xF0 fence, 0xF1 invalidate) wake it. */

unsigned __int64 __readmsr(unsigned long);
void __writemsr(unsigned long, unsigned __int64);
unsigned __int64 __readcr0(void);
unsigned __int64 __readcr4(void);
void __writecr0(unsigned __int64);
void __writecr4(unsigned __int64);
void __invlpg(void *);
void __halt(void);
void _enable(void);
#pragma intrinsic(__readmsr, __writemsr, __readcr0, __readcr4, __writecr0, __writecr4, __invlpg, __halt, _enable)

#define APIC_BASE_MSR 0x1BU
#define APIC_BASE_ENABLE (1ULL << 11)
#define APIC_BASE_X2APIC (1ULL << 10)
#define APIC_BASE_ADDRESS 0xFFFFFF000ULL
#define APIC_ID 0x020U
#define APIC_EOI 0x0B0U
#define APIC_SPURIOUS 0x0F0U
#define APIC_ICR_LOW 0x300U
#define APIC_ICR_HIGH 0x310U
#define APIC_LVT_TIMER 0x320U
#define APIC_LVT_LINT0 0x350U
#define APIC_LVT_LINT1 0x360U
#define APIC_LVT_ERROR 0x370U
#define APIC_TASK_PRIORITY 0x080U
#define LVT_MASKED 0x10000U
#define LVT_EXTINT 0x700U
#define LVT_NMI 0x400U
#define ICR_PENDING (1U << 12)
#define ICR_INIT 0x4500U /* INIT, level asserted */
#define ICR_STARTUP 0x4600U /* start-up, plus the vector (the page number of the trampoline) */
#define ICR_FIXED 0x4000U /* fixed delivery, level asserted */
#define IPI_FENCE_VECTOR 0xF0U
#define IPI_INVALIDATE_VECTOR 0xF1U
#define TRAMPOLINE_LIMIT 0x100000ULL
#define POLL_LIMIT 1000000U

/* The trampoline (hand-encoded: the toolset assembles 64-bit code only). Offsets of the pieces the copy patches. */
#define T_LGDT_DISP 9U /* the disp16 of lgdt [disp16] */
#define T_FAR32 23U /* the 32-bit target of the jump into protected mode */
#define T_PM32 29U /* protected-mode code */
#define T_CR3 (T_PM32 + 20U) /* the moffs32 of mov eax, [CR3] */
#define T_FAR64 (T_PM32 + 53U) /* the 32-bit target of the jump into long mode */
#define T_LM64 (T_PM32 + 59U) /* long-mode code */
#define T_STACK_ADDRESS (T_LM64 + 2U) /* the moffs64 of mov rax, [STACK] */
#define T_INDEX_ADDRESS (T_LM64 + 15U)
#define T_ENTRY_ADDRESS (T_LM64 + 27U)
#define T_GDT 0x100U
#define T_GDT_POINTER 0x120U
#define T_CR3_VALUE 0x128U
#define T_STACK_VALUE 0x130U
#define T_INDEX_VALUE 0x138U
#define T_ENTRY_VALUE 0x140U

static const WitU8 trampoline[] = {
    /* 16-bit, CS = page >> 4, IP = 0 */
    0xFA, /* cli */
    0x8C, 0xC8, /* mov ax, cs */
    0x8E, 0xD8, /* mov ds, ax */
    0x66, 0x0F, 0x01, 0x16, 0x20, 0x01, /* lgdt dword ptr ds:[T_GDT_POINTER] (disp16 patched: offset 9) */
    0x0F, 0x20, 0xC0, /* mov eax, cr0 */
    0x66, 0x83, 0xC8, 0x01, /* or eax, 1 */
    0x0F, 0x22, 0xC0, /* mov cr0, eax */
    0x66, 0xEA, 0, 0, 0, 0, 0x08, 0x00, /* jmp far 0x08:PM32 (target patched: offset 23) */
    /* 32-bit, offset 29 */
    0x66, 0xB8, 0x10, 0x00, /* mov ax, 0x10 */
    0x8E, 0xD8, /* mov ds, ax */
    0x8E, 0xD0, /* mov ss, ax */
    0x8E, 0xC0, /* mov es, ax */
    0x0F, 0x20, 0xE0, /* mov eax, cr4 */
    0x83, 0xC8, 0x20, /* or eax, 0x20 (PAE) */
    0x0F, 0x22, 0xE0, /* mov cr4, eax */
    0xA1, 0, 0, 0, 0, /* mov eax, [CR3] (moffs32 patched: offset 49) */
    0x0F, 0x22, 0xD8, /* mov cr3, eax */
    0xB9, 0x80, 0x00, 0x00, 0xC0, /* mov ecx, 0xC0000080 (EFER) */
    0x0F, 0x32, /* rdmsr */
    0x0D, 0x00, 0x09, 0x00, 0x00, /* or eax, 0x900 (LME | NXE) */
    0x0F, 0x30, /* wrmsr */
    0x0F, 0x20, 0xC0, /* mov eax, cr0 */
    0x0D, 0x00, 0x00, 0x01, 0x80, /* or eax, 0x80010000 (PG | WP) */
    0x0F, 0x22, 0xC0, /* mov cr0, eax */
    0xEA, 0, 0, 0, 0, 0x18, 0x00, /* jmp far 0x18:LM64 (target patched: offset 82) */
    /* 64-bit, offset 88 */
    0x48, 0xA1, 0, 0, 0, 0, 0, 0, 0, 0, /* mov rax, [STACK] (moffs64 patched: offset 90) */
    0x48, 0x89, 0xC4, /* mov rsp, rax */
    0x48, 0xA1, 0, 0, 0, 0, 0, 0, 0, 0, /* mov rax, [INDEX] (patched: offset 103) */
    0x89, 0xC1, /* mov ecx, eax */
    0x48, 0xA1, 0, 0, 0, 0, 0, 0, 0, 0, /* mov rax, [ENTRY] (patched: offset 115) */
    0xFF, 0xE0, /* jmp rax */
};

WIT_STATIC_ASSERT(sizeof(trampoline) == T_LM64 + 37U, "Trampoline layout");
WIT_STATIC_ASSERT(T_CR3 == 49U && T_FAR64 == 82U && T_LM64 == 88U, "Trampoline offsets");
WIT_STATIC_ASSERT(T_STACK_ADDRESS == 90U && T_INDEX_ADDRESS == 103U && T_ENTRY_ADDRESS == 115U, "Trampoline data refs");

WitU64 wit_x64_boot_cr0, wit_x64_boot_cr4;
WitU32 wit_x64_ipi_vector; /* the vector of the inter-processor interrupt in flight: one at a time, the sender waits */
__declspec(align(4096)) static WitU8 stacks[WIT_PROCESSOR_CAPACITY][WIT_KERNEL_STACK_SIZE];
static WitU64 apic_base, trampoline_page;
static int apic_enabled;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static volatile WitU32 *apic(WitU32 offset)
{
    return (volatile WitU32 *)(apic_base + offset);
}

static void delay_microseconds(WitU64 microseconds)
{
    const WitU64 frequency = wit_platform_monotonic_frequency();
    const WitU64 until = wit_platform_monotonic_read() + (frequency * microseconds) / 1000000ULL + 1;
    while (wit_platform_monotonic_read() < until) {
    }
}

/* The local APIC of the running processor in xAPIC mode: every local vector masked but the PIC's line through LINT0
 * on the boot processor (virtual wire, so the PIT tick still arrives); the task priority accepts everything. */
static void enable_local_apic(int boot_processor)
{
    const WitU64 base = __readmsr(APIC_BASE_MSR);
    require((base & APIC_BASE_X2APIC) == 0, "x2APIC mode is not the profile's");
    __writemsr(APIC_BASE_MSR, base | APIC_BASE_ENABLE);
    *apic(APIC_SPURIOUS) = 0x1FFU; /* software enabled, spurious vector 0xFF */
    *apic(APIC_LVT_TIMER) = LVT_MASKED;
    *apic(APIC_LVT_ERROR) = LVT_MASKED;
    *apic(APIC_LVT_LINT0) = boot_processor ? LVT_EXTINT : LVT_MASKED;
    *apic(APIC_LVT_LINT1) = LVT_NMI | LVT_MASKED;
    *apic(APIC_TASK_PRIORITY) = 0;
}

static void write_icr(WitU32 destination, WitU32 low)
{
    WitU32 spin = 0;
    *apic(APIC_ICR_HIGH) = destination << 24;
    *apic(APIC_ICR_LOW) = low;
    while ((*apic(APIC_ICR_LOW) & ICR_PENDING) && ++spin < POLL_LIMIT) {
    }
    require(spin < POLL_LIMIT, "Interrupt command did not complete");
}

/* One page of usable memory below 1 MiB for the trampoline: the lowest free one, taken from the allocator for good. */
static WitU64 claim_trampoline_page(const WitBootInfo *boot)
{
    for (WitU32 i = 0; i < boot->MemoryRegionCount; ++i) {
        const WitMemoryRegion *r = &boot->MemoryRegions[i];
        if (r->Kind != WIT_MEMORY_USABLE) {
            continue;
        }
        for (WitU64 page = r->Base; page < r->Base + r->Length && page < TRAMPOLINE_LIMIT; page += 4096) {
            if (page >= 0x10000 && wit_page_claim(wit_physical_pages(), page)) {
                return page;
            }
        }
    }
    wit_panic("No usable page below 1 MiB for the start of secondary processors");
}

static void write32(WitU64 address, WitU32 value)
{
    *(WitU32 *)address = value;
}

static void write64(WitU64 address, WitU64 value)
{
    *(WitU64 *)address = value;
}

void wit_arch_secondary_prepare(const WitBootInfo *boot, WitU32 index, WitU64 hardware_id)
{
    require(index != 0 && index < WIT_PROCESSOR_CAPACITY, "Secondary processor index out of range");
    require(hardware_id < 256, "An APIC id beyond the xAPIC destination field");
    if (!apic_enabled) {
        apic_base = __readmsr(APIC_BASE_MSR) & APIC_BASE_ADDRESS;
        wit_arch_map_device_page(boot, apic_base);
        enable_local_apic(1);
        wit_x64_boot_cr0 = __readcr0();
        wit_x64_boot_cr4 = __readcr4();
        trampoline_page = claim_trampoline_page(boot);
        for (WitU32 i = 0; i < 4096; ++i) {
            ((WitU8 *)trampoline_page)[i] = i < sizeof(trampoline) ? trampoline[i] : 0;
        }
        *(WitU16 *)(trampoline_page + T_LGDT_DISP) = (WitU16)T_GDT_POINTER;
        write32(trampoline_page + T_FAR32, (WitU32)(trampoline_page + T_PM32));
        write32(trampoline_page + T_CR3, (WitU32)(trampoline_page + T_CR3_VALUE));
        write32(trampoline_page + T_FAR64, (WitU32)(trampoline_page + T_LM64));
        write64(trampoline_page + T_STACK_ADDRESS, trampoline_page + T_STACK_VALUE);
        write64(trampoline_page + T_INDEX_ADDRESS, trampoline_page + T_INDEX_VALUE);
        write64(trampoline_page + T_ENTRY_ADDRESS, trampoline_page + T_ENTRY_VALUE);
        write64(trampoline_page + T_GDT, 0);
        write64(trampoline_page + T_GDT + 8, 0x00CF9A000000FFFFULL); /* 0x08: 32-bit code */
        write64(trampoline_page + T_GDT + 16, 0x00CF92000000FFFFULL); /* 0x10: 32-bit data */
        write64(trampoline_page + T_GDT + 24, 0x00AF9A000000FFFFULL); /* 0x18: 64-bit code */
        *(WitU16 *)(trampoline_page + T_GDT_POINTER) = 31;
        write32(trampoline_page + T_GDT_POINTER + 2, (WitU32)(trampoline_page + T_GDT));
        require(wit_virtual_kernel_root() < 0x100000000ULL, "The kernel root must be below 4 GiB for the trampoline");
        write32(trampoline_page + T_CR3_VALUE, (WitU32)wit_virtual_kernel_root());
        write64(trampoline_page + T_ENTRY_VALUE, (WitU64)wit_x64_secondary_entry);
        wit_x64_page_executable(trampoline_page); /* the long-mode stage runs there with paging on */
        apic_enabled = 1;
    }
    write64(trampoline_page + T_STACK_VALUE, (WitU64)stacks[index] + WIT_KERNEL_STACK_SIZE);
    write64(trampoline_page + T_INDEX_VALUE, index);
    wit_platform_processor_prepare(boot, index, hardware_id);
}

int wit_arch_secondary_start(WitU32 index, WitU64 hardware_id)
{
    (void)index;
    require(apic_enabled, "Secondary start before the APIC was prepared");
    write_icr((WitU32)hardware_id, ICR_INIT);
    delay_microseconds(10000);
    write_icr((WitU32)hardware_id, ICR_STARTUP | (WitU32)(trampoline_page >> 12));
    delay_microseconds(200);
    write_icr((WitU32)hardware_id, ICR_STARTUP | (WitU32)(trampoline_page >> 12));
    return 1;
}

void wit_arch_ipi(WitU64 hardware_id, WitU32 kind)
{
    require(apic_enabled && hardware_id < 256, "Inter-processor interrupt without the APIC");
    write_icr((WitU32)hardware_id, ICR_FIXED | (kind == WIT_IPI_FENCE ? IPI_FENCE_VECTOR : IPI_INVALIDATE_VECTOR));
}

void wit_arch_invalidate_local(WitU64 address)
{
    __invlpg((void *)address);
}

int wit_x64_lapic_enabled(void)
{
    return apic_enabled;
}

/* A started processor, on its own stack with the kernel's tables and root: its control registers follow the boot
 * processor's, its APIC comes up masked, it reports its features and itself, and halts for interrupts. */
WIT_NORETURN void wit_x64_secondary_main(WitU32 index)
{
    __writecr4(wit_x64_boot_cr4);
    __writecr0(wit_x64_boot_cr0);
    enable_local_apic(0);
    wit_processors_set_features(index, wit_arch_processor_features(), wit_arch_cache_size());
    wit_cpus_secondary_ready(index);
    _enable();
    for (;;) {
        __halt();
    }
}

/* An inter-processor interrupt on a secondary processor: served, acknowledged to the kernel and to the APIC. */
WitInterruptContext *wit_x64_ipi_interrupt(WitInterruptContext *context)
{
    wit_cpus_ipi_received(wit_x64_ipi_vector == IPI_FENCE_VECTOR ? WIT_IPI_FENCE : WIT_IPI_INVALIDATE);
    *apic(APIC_EOI) = 0;
    return context;
}
