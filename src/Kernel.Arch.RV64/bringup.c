#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "witos/rv64_csr.h"

/* The RISC-V bring-up (plan step V1.1). After ExitBootServices the loader enters here in S-mode over SBI, with the
 * firmware's translation tables still active and its UART reachable through them. This slice proves the path the toolchain needs
 * on this ISA — the ELF the tool converted to PE32+, loaded and relocated by the firmware; C running at S-mode; the
 * board's console — and ends the machine through the test device with the success code. The kernel's own
 * initialization (paging, traps, the clock, user mode) is V1.2 and later. */

WIT_NORETURN void wit_arch_enter(const struct WitBootInfo *boot)
{
    /* The firmware's translation (an identity mapping of the memory and the devices it drove) stays active after
     * ExitBootServices and maps nothing else — not the board's test finisher, whose write took a page fault under it.
     * The bring-up leaves it for the bare physical space, which that identity mapping equals, until V1.2 installs the
     * kernel's own tables. */
    wit_rv64_write_satp(0);
    wit_rv64_fence_vma();
    wit_console_write("[RV64] entered the kernel after ExitBootServices: ");
    wit_console_write_u64(boot->MemoryRegionCount);
    wit_console_write(" memory regions, image at ");
    wit_console_write_hex(boot->ImageBase);
    wit_console_write(" of ");
    wit_console_write_u64(boot->ImageSize);
    wit_console_write(" bytes\n");
    wit_platform_finish(0x10); /* the success code every board maps to the host's exit 33 */
}

void wit_arch_disable_interrupts(void)
{
    wit_rv64_clear_sstatus(WIT_RV64_SSTATUS_SIE);
}

int wit_arch_interrupts_enabled(void)
{
    return (wit_rv64_read_sstatus() & WIT_RV64_SSTATUS_SIE) != 0;
}

WIT_NORETURN void wit_arch_halt(void)
{
    wit_arch_disable_interrupts();
    for (;;) {
        wit_rv64_wait_for_interrupt();
    }
}
