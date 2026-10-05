#include <windows.h>
#include <errno.h>
#include "crt.h"

/* The errno of a Windows error (P6.4.i2), shared by both platforms: UCRT's table of the errors its functions report,
 * the write-protect to sharing-buffer range as EACCES, the executable-format range as ENOEXEC and EINVAL for the rest.
 * The differential compares it through the file functions, including a name Windows rejects (EINVAL, not ENOENT). */
namespace WitCrt {

namespace {

struct Mapping {
    unsigned long Error;
    int Errno;
};

constexpr Mapping TABLE[] = {{ERROR_INVALID_FUNCTION, EINVAL}, {ERROR_FILE_NOT_FOUND, ENOENT},
    {ERROR_PATH_NOT_FOUND, ENOENT}, {ERROR_TOO_MANY_OPEN_FILES, EMFILE}, {ERROR_ACCESS_DENIED, EACCES},
    {ERROR_INVALID_HANDLE, EBADF}, {ERROR_ARENA_TRASHED, ENOMEM}, {ERROR_NOT_ENOUGH_MEMORY, ENOMEM},
    {ERROR_INVALID_BLOCK, ENOMEM}, {ERROR_BAD_ENVIRONMENT, E2BIG}, {ERROR_BAD_FORMAT, ENOEXEC},
    {ERROR_INVALID_ACCESS, EINVAL}, {ERROR_INVALID_DATA, EINVAL}, {ERROR_INVALID_DRIVE, ENOENT},
    {ERROR_CURRENT_DIRECTORY, EACCES}, {ERROR_NOT_SAME_DEVICE, EXDEV}, {ERROR_NO_MORE_FILES, ENOENT},
    {ERROR_LOCK_VIOLATION, EACCES}, {ERROR_BAD_NETPATH, ENOENT}, {ERROR_NETWORK_ACCESS_DENIED, EACCES},
    {ERROR_BAD_NET_NAME, ENOENT}, {ERROR_FILE_EXISTS, EEXIST}, {ERROR_CANNOT_MAKE, EACCES}, {ERROR_FAIL_I24, EACCES},
    {ERROR_INVALID_PARAMETER, EINVAL}, {ERROR_NO_PROC_SLOTS, EAGAIN}, {ERROR_DRIVE_LOCKED, EACCES},
    {ERROR_BROKEN_PIPE, EPIPE}, {ERROR_DISK_FULL, ENOSPC}, {ERROR_INVALID_TARGET_HANDLE, EBADF},
    {ERROR_WAIT_NO_CHILDREN, ECHILD}, {ERROR_CHILD_NOT_COMPLETE, ECHILD}, {ERROR_DIRECT_ACCESS_HANDLE, EBADF},
    {ERROR_NEGATIVE_SEEK, EINVAL}, {ERROR_SEEK_ON_DEVICE, EACCES}, {ERROR_DIR_NOT_EMPTY, ENOTEMPTY},
    {ERROR_NOT_LOCKED, EACCES}, {ERROR_BAD_PATHNAME, ENOENT}, {ERROR_MAX_THRDS_REACHED, EAGAIN},
    {ERROR_LOCK_FAILED, EACCES}, {ERROR_ALREADY_EXISTS, EEXIST}, {ERROR_FILENAME_EXCED_RANGE, ENOENT},
    {ERROR_NESTING_NOT_ALLOWED, EAGAIN}, {ERROR_NOT_ENOUGH_QUOTA, ENOMEM}};

} // namespace

int ErrnoFromOs(unsigned long error)
{
    for (const Mapping &mapping : TABLE) {
        if (mapping.Error == error) {
            return mapping.Errno;
        }
    }
    if (error >= ERROR_WRITE_PROTECT && error <= ERROR_SHARING_BUFFER_EXCEEDED) {
        return EACCES;
    }
    if (error >= ERROR_INVALID_STARTING_CODESEG && error <= ERROR_INFLOOP_IN_RELOC_CHAIN) {
        return ENOEXEC;
    }
    return EINVAL;
}

} // namespace WitCrt
