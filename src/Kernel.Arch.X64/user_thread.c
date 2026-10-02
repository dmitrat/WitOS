#include "user.h"
#include "witos/platform.h"
#define NO_THREAD WIT_USER_THREAD_CAPACITY
_Static_assert(WIT_COMPILER_TLS_DATA_OFFSET + WIT_PE_TLS_MAX_BYTES <= 4096, "Compiler TLS page bound");

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static WitU64 next_native_id = 1;

static int take_native_id(WitU64 *next, WitU32 *output)
{
    if (!*next || *next > 0xFFFFFFFFULL) {
        return 0;
    }
    *output = (WitU32)*next;
    ++*next;
    return 1;
}

void wit_user_native_id_self_test(void)
{
    WitU64 cursor = 1;
    WitU32 value = 0;
    require(take_native_id(&cursor, &value) && value == 1 && cursor == 2, "Native ID initial allocation failed");
    cursor = 0xFFFFFFFFULL;
    require(take_native_id(&cursor, &value) && value == 0xFFFFFFFFU && cursor == 0x100000000ULL,
        "Native ID final allocation failed");
    value = 17;
    require(
        !take_native_id(&cursor, &value) && value == 17 && cursor == 0x100000000ULL, "Native ID wrapped on exhaustion");
    cursor = 0;
    require(!take_native_id(&cursor, &value) && value == 17, "Native ID accepted zero cursor");
    wit_console_write("[TEST-PASS] User.NativeThreadIdExhaustion\n");
}

/* Capture the relocated initial template before publishing the component.
 * Later user writes to its PE image cannot change the seed for future threads. */
int wit_user_capture_tls(WitUserProcess *process, const WitPeImage *image)
{
    const WitU32 index = 0; /* One static module per component. */
    process->TlsBytes = 0;
    if (!image || !image->TlsSize) {
        return 1;
    }
    if (!wit_user_copy_from(
            &process->Space, process->ImageBase + image->TlsTemplateRva, process->TlsTemplate, image->TlsInitialized) ||
        !wit_user_copy_to(
            &process->Space, process->ImageBase + image->TlsIndexRva, (const WitU8 *)&index, sizeof(index))) {
        return 0;
    }
    process->TlsBytes = image->TlsInitialized + image->TlsZeroFill;
    for (WitU32 i = image->TlsInitialized; i < process->TlsBytes; ++i) {
        process->TlsTemplate[i] = 0;
    }
    return 1;
}

WitU64 wit_user_prepare_thread(WitUserProcess *process, WitU32 index, WitU64 entry, WitU64 argument, WitU64 flags)
{
    const WitU64 admission = wit_user_library_thread_admission(process, flags);
    if (admission != WIT_STATUS_OK) {
        return admission;
    }
    WitUserThread *thread = &process->Threads[index];
    WitU64 handle, physical, *tls;
    WitU32 mapped = 0;
    int raw_mapped = 0;
    WitInterruptContext *context;
    const WitU64 bottom = WIT_USER_STACK_BOTTOM + index * WIT_USER_THREAD_STRIDE;
    const WitU64 top = WIT_USER_STACK_TOP + index * WIT_USER_THREAD_STRIDE;
    const WitU64 tls_address = WIT_USER_TLS + index * WIT_USER_THREAD_STRIDE;
    thread->LibraryNotifications = (flags & WIT_THREAD_LIBRARY_NOTIFICATIONS) != 0;
    thread->LibraryPhase = thread->LibraryNotifications ? 1U : 0U;
    thread->LibraryRequired = 0;
    if (thread->LibraryNotifications) {
        for (WitU32 n = 0; n < WIT_LIBRARY_CAPACITY; ++n) {
            if (process->Libraries[n].Token && process->Libraries[n].EntryRva) {
                thread->LibraryRequired = 1;
            }
        }
    }
    thread->NativeId = 0;
    thread->SuspendCount = 0;
    wit_user_exception_clear(thread);
    wit_user_thread_name_clear(thread);
    if (next_native_id > 0xFFFFFFFFULL) {
        return WIT_STATUS_NO_MEMORY;
    }
    thread->LibraryNotificationPage = 0;
    thread->LibraryNotificationHandles[0] = thread->LibraryNotificationHandles[1] = 0;
    thread->CompilerTls = 0;
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        thread->LibraryTls[i] = 0;
    }
    thread->Detached = 0;
    handle = wit_handle_grant(&process->Handles, WIT_HANDLE_THREAD, flags & WIT_THREAD_DETACHED ? 0 : WIT_RIGHT_JOIN);
    if (!handle) {
        return WIT_STATUS_NO_MEMORY;
    }
    for (WitU64 page = bottom; page < top; page += 4096) {
        if (!wit_user_space_map(&process->Space, page, 1, 0)) {
            goto failed;
        }
        ++mapped;
    }
    if (!wit_user_space_map(&process->Space, tls_address, 1, 0)) {
        goto failed;
    }
    raw_mapped = 1;
    if (process->TlsBytes) {
        const WitU64 address = tls_address + 4096;
        if (!wit_user_space_map(&process->Space, address, 1, 0)) {
            goto failed;
        }
        physical = wit_user_space_physical(&process->Space, address, 1, 0);
        ((WitU64 *)physical)[0x58 / 8] = address + 0x80;
        ((WitU64 *)physical)[0x80 / 8] = address + WIT_COMPILER_TLS_DATA_OFFSET;
        for (WitU32 i = 0; i < process->TlsBytes; ++i) {
            ((WitU8 *)physical)[WIT_COMPILER_TLS_DATA_OFFSET + i] = process->TlsTemplate[i];
        }
        thread->CompilerTls = address;
    }
    if (!wit_user_library_tls_create_thread(process, index)) {
        goto failed;
    }
    if (thread->LibraryRequired) {
        const WitU64 address = WIT_USER_TLS + index * WIT_USER_THREAD_STRIDE + (WIT_LIBRARY_CAPACITY + 2) * 4096;
        if (!wit_user_space_map(&process->Space, address, 0, 0)) {
            goto failed;
        }
        thread->LibraryNotificationPage = address;
        for (WitU32 n = 0; n < 2; ++n) {
            thread->LibraryNotificationHandles[n] =
                wit_handle_grant(&process->Handles, WIT_HANDLE_LIBRARY_LIFECYCLE, WIT_RIGHT_READ);
            if (!thread->LibraryNotificationHandles[n]) {
                goto failed;
            }
        }
    }
    physical = wit_user_space_physical(&process->Space, tls_address, 1, 0);
    tls = (WitU64 *)physical;
    tls[0] = tls_address;
    tls[1] = handle;
    tls[2] = argument;
    context = (WitInterruptContext *)(((WitU64)wit_x64_user_kernel_stacks[process->Slot][index] + 4096) +
        WIT_KERNEL_STACK_SIZE -
        4096);
    for (WitU32 i = 0; i < sizeof(*context); ++i) {
        ((WitU8 *)context)[i] = 0;
    }
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
    require(take_native_id(&next_native_id, &thread->NativeId), "Serialized native ID allocation failed");
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
    thread->WaitCount = 0;
    thread->WaitAll = 0;
    thread->WaitAlertable = 0;
    wit_user_apc_initialize(thread);
    for (WitU32 w = 0; w < WIT_WAIT_ANY_CAPACITY; ++w) {
        thread->WaitHandles[w] = 0;
    }
    thread->Deadline = WIT_WAIT_INFINITE;
    thread->WaitOrder = 0;
    thread->Detached = (flags & WIT_THREAD_DETACHED) != 0;
    thread->State = WitThreadReady;
    if (thread->Detached) {
        ++process->DetachedCreates;
    }
    ++process->ThreadCreates;
    return WIT_STATUS_OK;
failed:
    for (WitU32 n = 0; n < 2; ++n) {
        if (thread->LibraryNotificationHandles[n]) {
            require(wit_handle_close(&process->Handles, thread->LibraryNotificationHandles[n]) == WIT_STATUS_OK,
                "Thread notification handle rollback failed");
            thread->LibraryNotificationHandles[n] = 0;
        }
    }
    if (thread->LibraryNotificationPage) {
        require(wit_user_space_unmap_fixed(&process->Space, thread->LibraryNotificationPage),
            "Thread notification page rollback failed");
        thread->LibraryNotificationPage = 0;
    }
    wit_user_library_tls_reap_thread(process, index);
    if (thread->CompilerTls) {
        require(wit_user_space_unmap_fixed(&process->Space, thread->CompilerTls), "Compiler TLS rollback lost page");
        thread->CompilerTls = 0;
    }
    if (raw_mapped) {
        require(wit_user_space_unmap_fixed(&process->Space, tls_address), "Raw TLS rollback failed");
    }
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
    if (version != WIT_THREAD_INFO_VERSION) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (size != sizeof(info)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    info.Version = WIT_THREAD_INFO_VERSION;
    info.Size = sizeof(info);
    info.ThreadId = thread->Handle;
    info.StackLow = thread->StackBottom;
    info.StackHigh = thread->StackTop;
    info.RawTls = thread->Tls;
    info.CompilerTls = process->TlsBytes ? thread->CompilerTls : 0;
    info.CompilerTlsHeader = thread->CompilerTls;
    info.ProcessId = process->Id;
    info.NativeId = thread->NativeId;
    info.Reserved = 0;
    info.ProcessorCount = WIT_USER_PROCESSOR_COUNT; // The supported backend brings up one processor.
    // IF remains clear through snapshot and whole-buffer validation/copy.
    return wit_user_copy_to(&process->Space, address, (const WitU8 *)&info, sizeof(info)) ? WIT_STATUS_OK
                                                                                          : WIT_STATUS_BAD_ADDRESS;
}

WitU64 wit_user_thread_create_reference(WitUserProcess *p, WitU64 input, WitU64 size, WitU64 *result)
{
    *result = 0;
    if (size != sizeof(WitThreadCreateRequest)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitThreadCreateRequest request;
    if (!wit_user_copy_from(&p->Space, input, (WitU8 *)&request, sizeof(request))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (request.Version != WIT_THREAD_CREATE_REFERENCE_VERSION) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (request.Size != sizeof(request) ||
        request.Reserved ||
        (request.Flags & ~(WIT_THREAD_START_SUSPENDED | WIT_THREAD_LIBRARY_NOTIFICATIONS))) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (request.StackBytes > WIT_USER_STACK_TOP - WIT_USER_STACK_BOTTOM) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (!wit_user_space_physical(&p->Space, request.Entry, 0, 1) ||
        (request.NativeIdOutput && !wit_user_buffer_writable(&p->Space, request.NativeIdOutput, sizeof(WitU32)))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    WitU32 index = 0;
    while (index < WIT_USER_THREAD_CAPACITY && p->Threads[index].State != WitThreadEmpty) {
        ++index;
    }
    if (index == WIT_USER_THREAD_CAPACITY) {
        ++p->ReferenceThreadCapacityFailures;
        return WIT_STATUS_NO_MEMORY;
    }
    WitUserThreadReference *reference = 0;
    for (WitU32 n = 0; n < p->Handles.Limit; ++n) {
        if (!p->ThreadReferences[n].Handle) {
            reference = &p->ThreadReferences[n];
            break;
        }
    }
    if (!reference) {
        return WIT_STATUS_NO_MEMORY;
    }
    // Reserve the observer first. No user-visible publication occurs before
    // the private thread identity, stacks/TLS and suspend state all exist.
    const WitU64 handle = wit_handle_grant(&p->Handles, WIT_HANDLE_THREAD_REFERENCE, WIT_THREAD_REFERENCE_ALL);
    if (!handle) {
        return WIT_STATUS_NO_MEMORY;
    }
    const WitU64 status = wit_user_prepare_thread(p, index, request.Entry, request.Argument,
        WIT_THREAD_DETACHED | (request.Flags & WIT_THREAD_LIBRARY_NOTIFICATIONS));
    if (status != WIT_STATUS_OK) {
        if (wit_handle_close(&p->Handles, handle) != WIT_STATUS_OK) {
            wit_panic("Create reference rollback failed");
        }
        return status;
    }
    WitUserThread *thread = &p->Threads[index];
    thread->SuspendCount = (request.Flags & WIT_THREAD_START_SUSPENDED) ? 1U : 0U;
    reference->Handle = handle;
    reference->ThreadId = thread->Handle;
    reference->ExitCode = 0;
    reference->Rights = WIT_THREAD_REFERENCE_ALL;
    reference->Exited = 0;
    // The syscall is serialized with IF clear. Allocation did not remove or
    // change the prevalidated destination's existing mapping.
    if (request.NativeIdOutput &&
        !wit_user_copy_to(
            &p->Space, request.NativeIdOutput, (const WitU8 *)&thread->NativeId, sizeof(thread->NativeId))) {
        wit_panic("Validated thread native ID output changed");
    }
    *result = handle;
    return WIT_STATUS_OK;
}
