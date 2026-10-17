#include "witos/types.h"

/* The compiler helpers a freestanding riscv64 image needs (plan step V1.3): clang's RISC-V backend calls memset and
 * memcpy for aggregate initializations and copies it inlines on x64 and ARM64, and a fixture or the root task links
 * no libc. Byte loops, as the kernel's compiler.S has them; compiled into every riscv64 fixture and the root task. */

void *memset(void *destination, int value, unsigned long count)
{
    WitU8 *bytes = (WitU8 *)destination;
    for (unsigned long i = 0; i < count; ++i) {
        bytes[i] = (WitU8)value;
    }
    return destination;
}

void *memcpy(void *destination, const void *source, unsigned long count)
{
    WitU8 *to = (WitU8 *)destination;
    const WitU8 *from = (const WitU8 *)source;
    for (unsigned long i = 0; i < count; ++i) {
        to[i] = from[i];
    }
    return destination;
}

void *memmove(void *destination, const void *source, unsigned long count)
{
    WitU8 *to = (WitU8 *)destination;
    const WitU8 *from = (const WitU8 *)source;
    if (to < from) {
        for (unsigned long i = 0; i < count; ++i) {
            to[i] = from[i];
        }
    } else {
        for (unsigned long i = count; i > 0; --i) {
            to[i - 1] = from[i - 1];
        }
    }
    return destination;
}
