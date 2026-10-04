#include "witos/arch.h"
#include "witos/platform.h"

/* q35 console on the COM1 UART and the QEMU isa-debug-exit device. */

/* Compiler intrinsics emit instructions directly; no CRT or Windows API. */
unsigned char __inbyte(unsigned short port);
void __outbyte(unsigned short port, unsigned char value);
void __outdword(unsigned short port, unsigned long value);
#pragma intrinsic(__inbyte, __outbyte, __outdword)

#define COM1 0x3F8
#define SERIAL_POLL_LIMIT 1000000U

void wit_console_initialize(void)
{
    __outbyte(COM1 + 1, 0x00); /* Disable UART interrupts. */
    __outbyte(COM1 + 3, 0x80); /* Divisor latch. */
    __outbyte(COM1, 0x01); /* 115200 baud. */
    __outbyte(COM1 + 1, 0x00);
    __outbyte(COM1 + 3, 0x03); /* 8 data bits, no parity, 1 stop bit. */
    __outbyte(COM1 + 2, 0xC7);
    __outbyte(COM1 + 4, 0x03);
}

void wit_platform_console_put(WitU8 value)
{
    for (WitU32 spin = 0; spin < SERIAL_POLL_LIMIT; ++spin) {
        if ((__inbyte(COM1 + 5) & 0x20) != 0) {
            __outbyte(COM1, value);
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
    __outdword(0xF4, code);
    wit_arch_halt();
}
