#include "pal.witos.h"
extern "C" {
#include "path.h"
}

/* Win32 functions only the .NET host calls (P6.4.j2), apart from the NativeAOT archive and its default-profile probes:
 * the process identifier, debugger output and the directory calls of upstream pal.h's inline mkdir and rmdir. */
namespace {
[[noreturn]] void fatal()
{
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

/* Whether a UTF-16 name, resolved against the current directory, names an entry of the package; false for a name
 * that cannot be converted or resolved. */
bool exists(LPCWSTR name)
{
    char text[WIT_PATH_INPUT_MAX];
    const int bytes =
        WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, name, -1, text, sizeof(text), nullptr, nullptr);
    WitNativePath resolved;
    return bytes > 1 && wit_native_path_full(text, (WitU32)(bytes - 1), &resolved) == WIT_STATUS_OK;
}
} // namespace

/* The kernel's identifier of the calling thread's process, which every thread of the component shares. */
extern "C" DWORD WINAPI wit_native_process_id()
{
    WitUserThreadInfo info;
    if (!wit_native_thread_info(&info) || !info.ProcessId) {
        fatal();
    }
    return info.ProcessId;
}

/* No debugger attaches to a guest component (IsDebuggerPresent reports none). With neither a debugger nor a system
 * debugger, Windows discards the string, and so does the guest. */
extern "C" void WINAPI wit_native_debug_output(LPCWSTR) {}

/* The guest's file namespace is the immutable package: a directory can be neither created nor removed. As on
 * write-protected media, an existing name reports ERROR_ALREADY_EXISTS to CreateDirectoryW and every other name
 * ERROR_WRITE_PROTECT; RemoveDirectoryW reports a missing name as not found. */
extern "C" BOOL WINAPI wit_native_create_directory(LPCWSTR name, LPSECURITY_ATTRIBUTES)
{
    if (!name) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return FALSE;
    }
    SetLastError(exists(name) ? ERROR_ALREADY_EXISTS : ERROR_WRITE_PROTECT);
    return FALSE;
}

extern "C" BOOL WINAPI wit_native_remove_directory(LPCWSTR name)
{
    if (!name) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return FALSE;
    }
    SetLastError(exists(name) ? ERROR_WRITE_PROTECT : ERROR_FILE_NOT_FOUND);
    return FALSE;
}
