#include "witos/platform.h"
#include "self_test.h"

/* RISC-V CPU fault injection scenarios; each WITOS_TEST_* build expects one fatal kernel exception, named as the ISA
 * names its causes: a breakpoint, an illegal instruction and a load page fault. */

/* Above the physical address range of QEMU virt; the translation tables leave it unmapped. */
#define LOAD_FAULT_PROBE 0x0000400000000000ULL

void wit_rv64_trigger_breakpoint(void);
void wit_rv64_trigger_illegal(void);
void wit_rv64_trigger_load_fault(WitU64 address);

void wit_arch_fault_self_test(void)
{
#if defined(WITOS_TEST_BREAKPOINT)
    wit_console_write("[TEST-BEGIN] Cpu.Breakpoint\n");
    wit_rv64_trigger_breakpoint();
#elif defined(WITOS_TEST_UNDEFINED_INSTRUCTION)
    wit_console_write("[TEST-BEGIN] Cpu.IllegalInstruction\n");
    wit_rv64_trigger_illegal();
#elif defined(WITOS_TEST_DATA_ABORT)
    wit_console_write("[TEST-BEGIN] Cpu.LoadPageFault\n");
    wit_rv64_trigger_load_fault(LOAD_FAULT_PROBE);
#endif
#if defined(WITOS_TEST_BREAKPOINT) || defined(WITOS_TEST_UNDEFINED_INSTRUCTION) || defined(WITOS_TEST_DATA_ABORT)
    wit_panic("Fault injection unexpectedly returned");
#endif
}
