#include "witos/platform.h"

/* Board-independent console formatting on top of the board byte output; the serial log uses CRLF. */

static void put_char(char value)
{
    if (value == '\n') {
        wit_platform_console_put('\r');
    }
    wit_platform_console_put((WitU8)value);
}

void wit_console_write(const char *text)
{
    while (*text != '\0') {
        put_char(*text++);
    }
}

void wit_console_write_buffer(const WitU8 *data, WitU32 size)
{
    for (WitU32 i = 0; i < size; ++i) {
        put_char((char)data[i]);
    }
}

void wit_console_write_u64(WitU64 value)
{
    char digits[20];
    WitU32 count = 0;
    do {
        digits[count++] = (char)('0' + value % 10);
        value /= 10;
    } while (value != 0);
    while (count != 0) {
        put_char(digits[--count]);
    }
}

void wit_console_write_hex(WitU64 value)
{
    static const char digits[] = "0123456789ABCDEF";
    wit_console_write("0x");
    for (WitU32 digit = 0; digit < 16; ++digit) {
        put_char(digits[(value >> ((15 - digit) * 4)) & 15]);
    }
}
