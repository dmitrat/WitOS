#include "user.h"
#include "witos/platform.h"

/* DEBUG_WRITE: the kernel's last-resort output through the board console. The handle and the complete source range
 * are validated before any byte is emitted; the kernel copies in bounded steps and never interprets the bytes. */
WitU64 wit_user_debug_write(WitUserProcess *process, WitU64 handle, WitU64 buffer, WitU64 length, WitU64 *written)
{
    WitU8 chunk[WIT_ABI_MAX_WRITE];
    *written = 0;
    const WitU64 status = wit_handle_check(&process->Handles, handle, WIT_HANDLE_CONSOLE, WIT_RIGHT_WRITE);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (length > WIT_DEBUG_WRITE_MAX) {
        return WIT_STATUS_TOO_LARGE;
    }
    if (!wit_user_buffer_readable(&process->Space, buffer, (WitU32)length)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (length) {
        wit_console_write("[USER] ");
        for (WitU32 offset = 0; offset < (WitU32)length;) {
            WitU32 count = (WitU32)length - offset;
            if (count > sizeof(chunk)) {
                count = sizeof(chunk);
            }
            if (!wit_user_copy_from(&process->Space, buffer + offset, chunk, count)) {
                wit_panic("Validated debug write source changed");
            }
            wit_console_write_buffer(chunk, count);
            offset += count;
        }
        ++process->Writes;
    }
    *written = length;
    return WIT_STATUS_OK;
}
