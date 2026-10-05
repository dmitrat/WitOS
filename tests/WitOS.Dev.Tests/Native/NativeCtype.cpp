#include <windows.h>
#include <stdio.h>
#include "native_ctype.witos.h"

/* The guest's CT_CTYPE1 classes of U+0000-U+00FF (src/Runtime.NativeAot/native_ctype.witos.h, P6.4.i3b) against
 * Windows' GetStringTypeW, character by character; U+0100 must have no class there. */
int main()
{
    int differences = 0;
    for (unsigned value = 0; value <= 0xFF; ++value) {
        const wchar_t character = wchar_t(value);
        WORD windows = 0;
        if (!GetStringTypeW(CT_CTYPE1, &character, 1, &windows) || windows != wit_native_ctype1(value)) {
            printf("FAIL U+%04X windows %04X witos %04X\n", value, windows, wit_native_ctype1(value));
            ++differences;
        }
    }
    if (wit_native_ctype1(0x100) != 0xFFFF) {
        printf("FAIL U+0100 has a class\n");
        ++differences;
    }
    printf(differences ? "FAIL: %d differences\n" : "PASS: 256 classes\n", differences);
    return differences ? 1 : 0;
}
