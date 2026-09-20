#include "pal.witos.h"

static bool current(WitUserThreadInfo* info)
{
    WitU64 copied = 0;
    return wit_native_call(WIT_CALL_THREAD_QUERY, (uintptr_t)info, sizeof(*info), WIT_THREAD_INFO_VERSION, &copied) == WIT_STATUS_OK &&
        copied == sizeof(*info) && info->Version == WIT_THREAD_INFO_VERSION && info->Size == sizeof(*info) &&
        info->ThreadId && info->ProcessId && info->ProcessorCount == 1 && info->StackLow &&
        info->StackLow < info->StackHigh && !(info->StackLow & 4095) && !(info->StackHigh & 4095) &&
        info->RawTls && !(info->RawTls & 4095) && !(info->CompilerTls & 4095);
}
uint64_t PalGetCurrentOSThreadId()
{
    WitU64 identity = 0;
    if (wit_native_call(WIT_CALL_THREAD_CURRENT, 0, 0, 0, &identity) != WIT_STATUS_OK || !identity)
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    return identity;
}
bool PalGetMaximumStackBounds(void** low, void** high)
{
    WitUserThreadInfo info;
    if (!low || !high || low == high || !current(&info)) return false;
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
