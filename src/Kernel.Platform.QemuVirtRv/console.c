#include "witos/arch.h"
#include "witos/platform.h"
#include "witos/rv64_sbi.h"
#include "virt_rv.h"

/* QEMU virt (riscv64) console on the board's 16550 UART and the test exit through the sifive test finisher (plan
 * step V1.1). */

#define UART_TRANSMIT 0x0U /* the transmit holding register */
#define UART_LINE_STATUS 0x5U
#define UART_TRANSMIT_EMPTY 0x20U
#define SERIAL_POLL_LIMIT 1000000U

static volatile WitU8 *uart(WitU32 offset)
{
    return (volatile WitU8 *)(WIT_VIRT_RV_UART_BASE + offset);
}

void wit_console_initialize(void)
{
    /* The firmware has set the UART up for its own console; QEMU ignores the line settings. */
}

void wit_platform_console_put(WitU8 value)
{
    for (WitU32 spin = 0; spin < SERIAL_POLL_LIMIT; ++spin) {
        if ((*uart(UART_LINE_STATUS) & UART_TRANSMIT_EMPTY) != 0) {
            *uart(UART_TRANSMIT) = value;
            return;
        }
    }
    /* A missing/broken serial device must not hang panic or shutdown. */
}

WitU32 wit_platform_boot_devices(WitU64 *pages, WitU32 capacity)
{
    /* The console and the test finisher stay usable across the switch to the kernel's translation tables: a panic
     * ends the machine through the finisher (V1.2). */
    if (capacity < 2) {
        return 0;
    }
    pages[0] = WIT_VIRT_RV_UART_BASE;
    pages[1] = WIT_VIRT_RV_TEST_BASE;
    return 2;
}

WIT_NORETURN void wit_platform_finish(WitU32 code)
{
    /* The host sees the same status as q35's isa-debug-exit and virt's semihosting: guest 0x10 -> 33, 0x11 -> 35. The
     * finisher's "fail" form exits QEMU with the value's upper half; its "pass" form could only say 0. */
    *(volatile WitU32 *)WIT_VIRT_RV_TEST_BASE = ((((WitU32)code << 1) | 1U) << 16) | WIT_VIRT_RV_TEST_FAIL;
    if (code == 0x10) {
        /* Past the test exit, a success turns the board off through SBI's system reset extension (ADR 0029). */
        wit_console_write("[POWER] off through SBI system reset\n");
        wit_rv64_sbi(WIT_RV64_SBI_SRST, WIT_RV64_SBI_SRST_RESET, WIT_RV64_SBI_SRST_SHUTDOWN, 0, 0, 0);
        wit_console_write("[POWER] SBI system reset returned; halted\n");
    }
    wit_arch_halt();
}

void wit_platform_reset(void)
{
    wit_console_write("[POWER] reset through SBI system reset\n");
    wit_rv64_sbi(WIT_RV64_SBI_SRST, WIT_RV64_SBI_SRST_RESET, WIT_RV64_SBI_SRST_COLD_REBOOT, 0, 0, 0);
}
