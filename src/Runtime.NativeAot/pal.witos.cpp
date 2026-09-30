#include "pal.witos.h"

void PalFlushProcessWriteBuffers()
{
    if (!wit_pal_result(wit_native_call(WIT_CALL_PROCESS_WRITE_BARRIER, 0, 0, 0, nullptr)))
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}
static bool current(WitUserThreadInfo* info)
{
    WitU64 copied = 0;
    return wit_native_call(WIT_CALL_THREAD_QUERY, (uintptr_t)info, sizeof(*info), WIT_THREAD_INFO_VERSION, &copied) == WIT_STATUS_OK &&
        copied == sizeof(*info) && info->Version == WIT_THREAD_INFO_VERSION && info->Size == sizeof(*info) &&
        info->ThreadId && info->NativeId && !info->Reserved && info->ProcessId && info->ProcessorCount == 1 && info->StackLow &&
        info->StackLow < info->StackHigh && !(info->StackLow & 4095) && !(info->StackHigh & 4095) &&
        info->RawTls && !(info->RawTls & 4095) && !(info->CompilerTls & 4095);
}
uint64_t PalGetCurrentOSThreadId()
{
    WitU64 identity = 0;
    if (wit_native_call(WIT_CALL_THREAD_NATIVE_ID, 0, 0, 0, &identity) != WIT_STATUS_OK || !identity || identity > UINT32_MAX)
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    return identity;
}
bool PalGetMaximumStackBounds(void** low, void** high)
{
    WitUserThreadInfo info;
    if (!low || !high || low == high) { SetLastError(ERROR_INVALID_PARAMETER); return false; }
    if (!current(&info)) { SetLastError(ERROR_GEN_FAILURE); return false; }
    *low = (void*)(uintptr_t)info.StackLow;
    *high = (void*)(uintptr_t)info.StackHigh;
    return true;
}
uint32_t PalGetCurrentProcessId()
{
    WitUserThreadInfo info;
    if (!current(&info)) wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    return info.ProcessId;
}
int32_t PalGetProcessCpuCount()
{
    WitUserThreadInfo info;
    if (!current(&info)) wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    return (int32_t)info.ProcessorCount;
}
