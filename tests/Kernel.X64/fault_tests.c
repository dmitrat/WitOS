#include "x64.h"
#include "witos/platform.h"
#include "self_test.h"
#include "witos/x64_instructions.h"

/* CPU fault injection scenarios; each WITOS_TEST_* build expects one fatal kernel exception. */

void wit_arch_fault_self_test(void)
{
#if defined(WITOS_TEST_PAGE_FAULT) || defined(WITOS_TEST_DOUBLE_FAULT)
    /* The kernel's own page tables must leave the fault probe absent. */
    const WitU64 *pml4 = (const WitU64 *)(wit_x64_read_cr3() & 0x000FFFFFFFFFF000ULL);
    if ((pml4[(WIT_PAGE_FAULT_PROBE >> 39) & 511] & 1) != 0) {
        wit_panic("Page fault probe is mapped");
    }
#endif
#if defined(WITOS_TEST_BREAKPOINT)
    wit_console_write("[TEST-BEGIN] Cpu.Breakpoint\n");
    wit_x64_trigger_breakpoint();
#elif defined(WITOS_TEST_DIVIDE_ERROR)
    wit_console_write("[TEST-BEGIN] Cpu.DivideError\n");
    wit_x64_trigger_divide_error();
#elif defined(WITOS_TEST_INVALID_OPCODE)
    wit_console_write("[TEST-BEGIN] Cpu.InvalidOpcode\n");
    wit_x64_trigger_invalid_opcode();
#elif defined(WITOS_TEST_GENERAL_PROTECTION)
    wit_console_write("[TEST-BEGIN] Cpu.GeneralProtection\n");
    wit_x64_trigger_general_protection();
#elif defined(WITOS_TEST_PAGE_FAULT)
    wit_console_write("[TEST-BEGIN] Cpu.PageFault\n");
    wit_x64_trigger_page_fault();
#elif defined(WITOS_TEST_DOUBLE_FAULT)
    wit_console_write("[TEST-BEGIN] Cpu.DoubleFault\n");
    wit_x64_trigger_double_fault();
#endif
#if defined(WITOS_TEST_BREAKPOINT) || \
    defined(WITOS_TEST_DIVIDE_ERROR) || \
    defined(WITOS_TEST_INVALID_OPCODE) || \
    defined(WITOS_TEST_GENERAL_PROTECTION) || \
    defined(WITOS_TEST_PAGE_FAULT) || \
    defined(WITOS_TEST_DOUBLE_FAULT)
    wit_panic("Fault injection unexpectedly returned");
#endif
}
