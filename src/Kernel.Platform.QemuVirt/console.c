#include "witos/arch.h"
#include "witos/platform.h"
#include "virt.h"

/* QEMU virt console on the PL011 UART and the test exit through Arm semihosting. */

#define PL011_DATA 0x000U
#define PL011_FLAGS 0x018U
#define PL011_TRANSMIT_FULL 0x20U
#define SERIAL_POLL_LIMIT 1000000U

#define SEMIHOSTING_EXIT 0x18U
#define ADP_STOPPED_APPLICATION_EXIT 0x20026U

static volatile WitU32 *pl011(WitU32 offset)
{
    return (volatile WitU32 *)(WIT_VIRT_PL011_BASE + offset);
}

void wit_console_initialize(void)
{
    /* The firmware has enabled the UART for its own console; QEMU ignores the line settings. */
}

void wit_platform_console_put(WitU8 value)
{
    for (WitU32 spin = 0; spin < SERIAL_POLL_LIMIT; ++spin) {
        if ((*pl011(PL011_FLAGS) & PL011_TRANSMIT_FULL) == 0) {
            *pl011(PL011_DATA) = value;
            return;
        }
    }
    /* A missing/broken serial device must not hang panic or shutdown. */
}

WitU32 wit_platform_boot_devices(WitU64 *pages, WitU32 capacity)
{
    /* The console stays usable across the switch to the kernel's translation tables. */
    if (capacity == 0) {
        return 0;
    }
    pages[0] = WIT_VIRT_PL011_BASE;
    return 1;
}

WIT_NORETURN void wit_platform_finish(WitU32 code)
{
    /* The host sees the same status as q35's isa-debug-exit: guest 0x10 -> 33, guest 0x11 -> 35.
     * Without semihosting the call traps or returns, and the processor stays halted. */
    const WitU64 block[2] = {ADP_STOPPED_APPLICATION_EXIT, ((WitU64)code << 1) | 1};
    wit_virt_semihosting(SEMIHOSTING_EXIT, block);
    wit_arch_halt();
}
