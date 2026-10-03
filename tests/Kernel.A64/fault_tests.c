#include "witos/platform.h"
#include "self_test.h"

/* ARM64 CPU fault injection scenarios; each WITOS_TEST_* build expects one fatal kernel exception. */

/* Above the physical address range of QEMU virt; the translation tables leave it unmapped. */
#define DATA_ABORT_PROBE 0x0000400000000000ULL

void wit_a64_trigger_breakpoint(void);
void wit_a64_trigger_undefined(void);
void wit_a64_trigger_data_abort(WitU64 address);

void wit_arch_fault_self_test(void)
{
#if defined(WITOS_TEST_BREAKPOINT)
    wit_console_write("[TEST-BEGIN] Cpu.Breakpoint\n");
    wit_a64_trigger_breakpoint();
#elif defined(WITOS_TEST_UNDEFINED_INSTRUCTION)
    wit_console_write("[TEST-BEGIN] Cpu.UndefinedInstruction\n");
    wit_a64_trigger_undefined();
#elif defined(WITOS_TEST_DATA_ABORT)
    wit_console_write("[TEST-BEGIN] Cpu.DataAbort\n");
    wit_a64_trigger_data_abort(DATA_ABORT_PROBE);
#endif
#if defined(WITOS_TEST_BREAKPOINT) || defined(WITOS_TEST_UNDEFINED_INSTRUCTION) || defined(WITOS_TEST_DATA_ABORT)
    wit_panic("Fault injection unexpectedly returned");
#endif
}
