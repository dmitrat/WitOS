#include "witos/arch.h"
#include "witos/platform.h"
#include "witos/x64_instructions.h"

/* q35 console on the COM1 UART and the QEMU isa-debug-exit device. */

/* Compiler intrinsics emit instructions directly; no CRT or Windows API. */

#define COM1 0x3F8
#define SERIAL_POLL_LIMIT 1000000U

void wit_console_initialize(void)
{
    wit_x64_out8(COM1 + 1, 0x00); /* Disable UART interrupts. */
    wit_x64_out8(COM1 + 3, 0x80); /* Divisor latch. */
    wit_x64_out8(COM1, 0x01); /* 115200 baud. */
    wit_x64_out8(COM1 + 1, 0x00);
    wit_x64_out8(COM1 + 3, 0x03); /* 8 data bits, no parity, 1 stop bit. */
    wit_x64_out8(COM1 + 2, 0xC7);
    wit_x64_out8(COM1 + 4, 0x03);
}

void wit_platform_console_put(WitU8 value)
{
    for (WitU32 spin = 0; spin < SERIAL_POLL_LIMIT; ++spin) {
        if ((wit_x64_in8(COM1 + 5) & 0x20) != 0) {
            wit_x64_out8(COM1, value);
            return;
        }
    }
    /* A missing/broken serial device must not hang panic or shutdown. */
}

WitU32 wit_platform_boot_devices(WitU64 *pages, WitU32 capacity)
{
    /* COM1 is port I/O and the HPET is mapped on demand; q35 needs no early device pages. */
    (void)pages;
    (void)capacity;
    return 0;
}

WIT_NORETURN void wit_platform_finish(WitU32 code)
{
    /* QEMU test device only: guest 0x10 -> host 33, guest 0x11 -> host 35.
     * On hardware without this device, remain halted. */
    wit_arch_disable_interrupts();
    wit_x64_out32(0xF4, code);
    wit_arch_halt();
}
