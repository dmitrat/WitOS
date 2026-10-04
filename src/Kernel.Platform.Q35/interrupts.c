#include "witos/platform.h"

/* q35 legacy interrupt routing: the 8259 PIC pair and the 8254 PIT as the scheduler tick source. */

void __outbyte(unsigned short, unsigned char);
unsigned __int64 __readmsr(unsigned long);
void __writemsr(unsigned long, unsigned __int64);
void _disable(void);
#pragma intrinsic(__outbyte, __readmsr, __writemsr, _disable)

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static void io_wait(void)
{
    __outbyte(0x80, 0);
}

void wit_platform_timer_start(void)
{
    const WitU64 apic = __readmsr(0x1B);
    require((apic & (1ULL << 10)) == 0, "x2APIC is unsupported by the bootstrap timer");
    /* The controlled one-CPU PC backend uses the legacy PIC/PIT path.
     * Disable local APIC delivery so it does not retain firmware routing. */
    __writemsr(0x1B, apic & ~(1ULL << 11));
    __outbyte(0x21, 0xFF);
    __outbyte(0xA1, 0xFF);
    __outbyte(0x20, 0x11);
    io_wait();
    __outbyte(0xA0, 0x11);
    io_wait();
    __outbyte(0x21, 0x20);
    io_wait();
    __outbyte(0xA1, 0x28);
    io_wait();
    __outbyte(0x21, 0x04);
    io_wait();
    __outbyte(0xA1, 0x02);
    io_wait();
    __outbyte(0x21, 0x01);
    io_wait();
    __outbyte(0xA1, 0x01);
    io_wait();
    __outbyte(0x21, 0xFF);
    __outbyte(0xA1, 0xFF);
    /* Channel 0, low/high count, mode 2; approximately 100 Hz. */
    __outbyte(0x43, 0x34);
    __outbyte(0x40, (WitU8)(11932 & 255));
    __outbyte(0x40, (WitU8)(11932 >> 8));
    __outbyte(0x21, 0xFE); /* Only IRQ0. */
}

void wit_platform_timer_stop(void)
{
    _disable();
    __outbyte(0x21, 0xFF);
    __outbyte(0xA1, 0xFF);
}

void wit_platform_timer_acknowledge(void)
{
    __outbyte(0x20, 0x20); /* Non-specific EOI to the master PIC. */
}
