#include "pal.witos.h"

void PalFlushProcessWriteBuffers()
{
    if (!wit_pal_result(wit_native_call(WIT_CALL_PROCESS_WRITE_BARRIER, 0, 0, 0, nullptr))) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
}

static bool current(WitUserThreadInfo *info)
{
    return wit_native_thread_info(info) &&
        info->NativeId &&
        !info->Reserved &&
        info->ProcessId &&
        info->ProcessorCount == 1 &&
        info->StackLow &&
        info->StackLow < info->StackHigh &&
        !(info->StackLow & 4095) &&
        !(info->StackHigh & 4095) &&
        info->RawTls &&
        !(info->RawTls & 4095) &&
        !(info->CompilerTls & 4095);
}

uint64_t PalGetCurrentOSThreadId()
{
    WitUserThreadInfo info;
    if (!wit_native_thread_info(&info) || !info.NativeId) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    return info.NativeId;
}

bool PalGetMaximumStackBounds(void **low, void **high)
{
    WitUserThreadInfo info;
    if (!low || !high || low == high) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return false;
    }
    if (!current(&info)) {
        SetLastError(ERROR_GEN_FAILURE);
        return false;
    }
    *low = (void *)(uintptr_t)info.StackLow;
    *high = (void *)(uintptr_t)info.StackHigh;
    return true;
}

uint32_t PalGetCurrentProcessId()
{
    WitUserThreadInfo info;
    if (!current(&info)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    return info.ProcessId;
}

int32_t PalGetProcessCpuCount()
{
    WitUserThreadInfo info;
    if (!current(&info)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    return (int32_t)info.ProcessorCount;
}
