#include "user.h"
#include "witos/platform.h"
#define NO_THREAD WIT_USER_THREAD_CAPACITY
_Static_assert(0x100 + WIT_PE_TLS_MAX_BYTES <= 4096, "Compiler TLS page bound");
static void require(int condition, const char *message) { if (!condition) wit_panic(message); }

/* Capture the relocated initial template before publishing the component.
 * Later user writes to its PE image cannot change the seed for future threads. */
int wit_user_capture_tls(WitUserProcess *process, const WitPeImage *image)
{
    const WitU32 index = 0; /* One static module per component. */
    process->TlsBytes = 0;
    if (!image || !image->TlsSize) return 1;
    if (!wit_user_copy_from(&process->Space, process->ImageBase + image->TlsTemplateRva,
            process->TlsTemplate, image->TlsInitialized) ||
        !wit_user_copy_to(&process->Space, process->ImageBase + image->TlsIndexRva, (const WitU8 *)&index, sizeof(index))) return 0;
    process->TlsBytes = image->TlsInitialized + image->TlsZeroFill;
    for (WitU32 i = image->TlsInitialized; i < process->TlsBytes; ++i) process->TlsTemplate[i] = 0;
    return 1;
}

WitU64 wit_user_prepare_thread(WitUserProcess *process, WitU32 index, WitU64 entry, WitU64 argument, WitU64 flags)
{
    WitUserThread *thread = &process->Threads[index];
    WitU64 handle, physical, *tls;
    WitU32 mapped = 0;
    int raw_mapped = 0;
    WitInterruptContext *context;
    const WitU64 bottom = WIT_USER_STACK_BOTTOM + index * WIT_USER_THREAD_STRIDE;
    const WitU64 top = WIT_USER_STACK_TOP + index * WIT_USER_THREAD_STRIDE;
    const WitU64 tls_address = WIT_USER_TLS + index * WIT_USER_THREAD_STRIDE;
    thread->CompilerTls = 0;
    thread->Detached = 0;
    handle = wit_handle_grant(&process->Handles, WIT_HANDLE_THREAD, flags & WIT_THREAD_DETACHED ? 0 : WIT_RIGHT_JOIN);
    if (!handle) return WIT_STATUS_NO_MEMORY;
    for (WitU64 page = bottom; page < top; page += 4096) {
        if (!wit_user_space_map(&process->Space, page, 1, 0)) goto failed;
        ++mapped;
    }
    if (!wit_user_space_map(&process->Space, tls_address, 1, 0)) goto failed;
    raw_mapped = 1;
    if (process->TlsBytes) {
        const WitU64 address = tls_address + 4096;
        if (!wit_user_space_map(&process->Space, address, 1, 0)) goto failed;
        physical = wit_user_space_physical(&process->Space, address, 1, 0);
        ((WitU64 *)physical)[0x58 / 8] = address + 0x80;
        ((WitU64 *)physical)[0x80 / 8] = address + 0x100;
        for (WitU32 i = 0; i < process->TlsBytes; ++i) ((WitU8 *)physical)[0x100 + i] = process->TlsTemplate[i];
        thread->CompilerTls = address;
    }
    physical = wit_user_space_physical(&process->Space, tls_address, 1, 0);
    tls = (WitU64 *)physical;
    tls[0] = tls_address;
    tls[1] = handle;
    tls[2] = argument;
    context = (WitInterruptContext *)(((WitU64)wit_x64_user_kernel_stacks[process->Slot][index] + 4096) + WIT_KERNEL_STACK_SIZE - 4096);
    for (WitU32 i = 0; i < sizeof(*context); ++i) ((WitU8 *)context)[i] = 0;
    context->FxState[0] = 0x7F;
    context->FxState[1] = 0x03;
    context->FxState[24] = 0x80;
    context->FxState[25] = 0x1F;
    context->Rcx = argument;
    context->Rip = entry;
    context->Cs = WIT_USER_CS;
    context->Ss = WIT_USER_SS;
    context->Rflags = 0x202;
    context->Rsp = top - 40; /* Aligned ABI entry, zero return address traps accidental RET. */
    thread->Handle = handle;
    thread->StackBottom = bottom;
    thread->StackTop = top;
    thread->Tls = tls_address;
    thread->ExitCode = 0;
    thread->Context = context;
    thread->WaitingOn = NO_THREAD;
    thread->Joiner = NO_THREAD;
    thread->WaitKind = WitWaitNone;
    thread->WaitHandle = 0;
    thread->Deadline = WIT_WAIT_INFINITE;
    thread->WaitOrder = 0;
    thread->Detached = (flags & WIT_THREAD_DETACHED) != 0;
    thread->State = WitThreadReady;
    if (thread->Detached) ++process->DetachedCreates;
    ++process->ThreadCreates;
    return WIT_STATUS_OK;
failed:
    if (raw_mapped) require(wit_user_space_unmap_fixed(&process->Space, tls_address), "Raw TLS rollback failed");
    while (mapped) {
        --mapped;
        require(wit_user_space_unmap_fixed(&process->Space, bottom + mapped * 4096ULL),
            "Thread creation rollback lost a stack page");
    }
    require(wit_handle_close(&process->Handles, handle) == WIT_STATUS_OK, "Thread handle rollback failed");
    return WIT_STATUS_NO_MEMORY;
}


WitU64 wit_user_thread_query(const WitUserProcess *process, WitU64 address, WitU64 size, WitU64 version)
{
    const WitUserThread *thread = &process->Threads[process->CurrentThread];
    WitUserThreadInfo info;
    if (version != WIT_THREAD_INFO_VERSION) return WIT_STATUS_UNSUPPORTED;
    if (size != sizeof(info)) return WIT_STATUS_INVALID_ARGUMENT;
    info.Version = WIT_THREAD_INFO_VERSION;
    info.Size = sizeof(info);
    info.ThreadId = thread->Handle;
    info.StackLow = thread->StackBottom;
    info.StackHigh = thread->StackTop;
    info.RawTls = thread->Tls;
    info.CompilerTls = thread->CompilerTls;
    info.ProcessId = process->Id;
    info.ProcessorCount = 1; // The supported backend brings up one processor.
    // IF remains clear through snapshot and whole-buffer validation/copy.
    return wit_user_copy_to(&process->Space, address, (const WitU8 *)&info, sizeof(info)) ? WIT_STATUS_OK : WIT_STATUS_BAD_ADDRESS;
}
