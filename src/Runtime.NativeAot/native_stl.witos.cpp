#include "pal.witos.h"
#include "native_heap.witos.h"

/* Win32 functions the pinned microsoft/STL's separately compiled sources call beyond what the NativeAOT runtime uses
 * (P6.4.i), kept out of its archive and probe images. */
extern "C" DWORD WINAPI wit_native_format_message(DWORD, LPCVOID, DWORD, DWORD, LPWSTR, DWORD, va_list *);

namespace {
DWORD fail(DWORD error)
{
    SetLastError(error);
    return 0;
}
} // namespace

/* FormatMessageA over the wide catalogue, as Windows builds the ANSI form over the wide one: the wide call validates
 * the request in its own order and formats into a buffer it allocates, which this narrows. The catalogue is ASCII, so
 * narrowing each character is exact. */
extern "C" DWORD WINAPI wit_native_format_message_ansi(
    DWORD flags, LPCVOID source, DWORD id, DWORD language, LPSTR buffer, DWORD size, va_list *arguments)
{
    LPWSTR wide = nullptr;
    const DWORD length = wit_native_format_message(
        flags | FORMAT_MESSAGE_ALLOCATE_BUFFER, source, id, language, (LPWSTR)&wide, 0, arguments);
    if (!length) {
        return 0; // the wide call's error
    }
    char *output = buffer;
    DWORD error = ERROR_SUCCESS;
    if (!buffer) {
        error = ERROR_INVALID_PARAMETER;
    } else if (flags & FORMAT_MESSAGE_ALLOCATE_BUFFER) {
        output = (char *)wit_native_local_allocate(size > length + 1 ? size : length + 1);
        error = output ? ERROR_SUCCESS : ERROR_NOT_ENOUGH_MEMORY;
    } else if (size <= length) {
        error = ERROR_INSUFFICIENT_BUFFER;
    }
    if (error == ERROR_SUCCESS) {
        for (DWORD i = 0; i <= length; ++i) {
            output[i] = (char)wide[i];
        }
        // As in the wide form, the caller's pointer slot receives the buffer only once it holds the message.
        if (flags & FORMAT_MESSAGE_ALLOCATE_BUFFER) {
            *(char **)buffer = output;
        }
    }
    wit_native_local_release(wide);
    return error == ERROR_SUCCESS ? length : fail(error);
}

/* The guest has no locale database; the STL's system_category falls back to the neutral language without it. */
extern "C" int WINAPI wit_native_locale_info(LPCWSTR, LCTYPE, LPWSTR, int)
{
    return (int)fail(ERROR_NOT_SUPPORTED);
}
