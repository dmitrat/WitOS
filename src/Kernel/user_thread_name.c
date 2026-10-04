#include "user.h"

void wit_user_thread_name_clear(WitUserThread *thread)
{
    thread->NameLength = 0;
    for (WitU32 i = 0; i < WIT_THREAD_NAME_CAPACITY; ++i) {
        thread->Name[i] = 0;
    }
}

WitU64 wit_user_thread_name_set(WitUserProcess *process, WitU64 address, WitU64 length, WitU64 flags)
{
    WitU16 name[WIT_THREAD_NAME_CAPACITY] = {0};
    WitUserThread *thread = &process->Threads[process->CurrentThread];
    if (flags) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (length >= WIT_THREAD_NAME_CAPACITY) {
        return WIT_STATUS_TOO_LARGE;
    }
    if (length && !wit_user_copy_from(&process->Space, address, (WitU8 *)name, (WitU32)length * 2)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    for (WitU32 i = 0; i < length; ++i) {
        if (!name[i]) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
    }
    // Keep exact UTF-16 units, including isolated surrogates, as diagnostic data.
    // IF is clear through whole input validation and publication; no partial rename.
    for (WitU32 i = 0; i < WIT_THREAD_NAME_CAPACITY; ++i) {
        thread->Name[i] = name[i];
    }
    thread->NameLength = (WitU32)length;
    return WIT_STATUS_OK;
}

WitU64 wit_user_thread_name_query(WitUserProcess *process, WitU64 address, WitU64 size, WitU64 version)
{
    WitThreadNameInfo info = {0};
    const WitUserThread *thread = &process->Threads[process->CurrentThread];
    if (version != WIT_THREAD_NAME_VERSION) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (size != sizeof(info)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    info.Version = WIT_THREAD_NAME_VERSION;
    info.Size = sizeof(info);
    info.ThreadId = thread->Handle;
    info.Length = thread->NameLength;
    for (WitU32 i = 0; i < WIT_THREAD_NAME_CAPACITY; ++i) {
        info.Name[i] = thread->Name[i];
    }
    return wit_user_copy_to(&process->Space, address, (const WitU8 *)&info, sizeof(info)) ? WIT_STATUS_OK
                                                                                          : WIT_STATUS_BAD_ADDRESS;
}
