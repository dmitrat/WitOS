#include "pal.witos.h"

void wit_pal_set_status(WitU64 status)
{
    if (status == WIT_STATUS_OK) {
        return;
    }
    DWORD error;
    switch (status) {
    case WIT_STATUS_UNSUPPORTED:
        error = ERROR_NOT_SUPPORTED;
        break;
    case WIT_STATUS_BAD_HANDLE:
    case WIT_STATUS_WRONG_TYPE:
    case WIT_STATUS_CLOSED:
        error = ERROR_INVALID_HANDLE;
        break;
    case WIT_STATUS_DENIED:
        error = ERROR_ACCESS_DENIED;
        break;
    case WIT_STATUS_BAD_ADDRESS:
    case WIT_STATUS_NOT_RESERVED:
    case WIT_STATUS_NOT_COMMITTED:
        error = ERROR_INVALID_ADDRESS;
        break;
    case WIT_STATUS_INVALID_ARGUMENT:
    case WIT_STATUS_TOO_LARGE:
        error = ERROR_INVALID_PARAMETER;
        break;
    case WIT_STATUS_NO_MEMORY:
        error = ERROR_NOT_ENOUGH_MEMORY;
        break;
    case WIT_STATUS_BUSY:
        error = ERROR_BUSY;
        break;
    case WIT_STATUS_DEADLOCK:
        error = ERROR_POSSIBLE_DEADLOCK;
        break;
    case WIT_STATUS_TIMED_OUT:
        error = ERROR_TIMEOUT;
        break;
    default:
        error = ERROR_GEN_FAILURE;
        break;
    }
    SetLastError(error);
}

UInt32_BOOL wit_pal_result(WitU64 status)
{
    wit_pal_set_status(status);
    return status == WIT_STATUS_OK;
}
