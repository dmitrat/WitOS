#include "user.h"
#include "witos/platform.h"
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

#if defined(WITOS_SELFTEST)
/* Native ID exhaustion check; take_native_id is private to this file. */
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
#endif

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

/* Resets a free thread slot for a new thread. Library notifications are required when a loaded library has an
 * entry point. Fails when native thread identifiers are exhausted. */
static WitU64 reset_thread(WitUserProcess *process, WitUserThread *thread, WitU64 flags)
{
    thread->LibraryNotifications = (flags & WIT_THREAD_LIBRARY_NOTIFICATIONS) != 0;
    thread->LibraryPhase = thread->LibraryNotifications ? 1U : 0U;
    thread->LibraryRequired = 0;
    if (thread->LibraryNotifications) {
        for (WitU32 n = 0; n < WIT_LIBRARY_CAPACITY; ++n) {
            if (process->Libraries[n].Token && wit_user_library_participates(&process->Libraries[n])) {
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
    return WIT_STATUS_OK;
}

/* Pages of one thread under construction, for rollback. */
typedef struct ThreadPages {
    WitU64 Bottom, Top, Tls;
    WitU32 Mapped;
    int RawMapped;
} ThreadPages;

/* The compiler TLS page: the TEB-style self pointer at 0x58, the TLS array at 0x80 and the image template. */
static int map_compiler_tls(WitUserProcess *process, WitUserThread *thread, WitU64 address)
{
    if (!wit_user_space_map(&process->Space, address, 1, 0)) {
        return 0;
    }
    const WitU64 physical = wit_user_space_physical(&process->Space, address, 1, 0);
    ((WitU64 *)physical)[0x58 / 8] = address + 0x80;
    ((WitU64 *)physical)[0x80 / 8] = address + WIT_COMPILER_TLS_DATA_OFFSET;
    for (WitU32 i = 0; i < process->TlsBytes; ++i) {
        ((WitU8 *)physical)[WIT_COMPILER_TLS_DATA_OFFSET + i] = process->TlsTemplate[i];
    }
    thread->CompilerTls = address;
    return 1;
}

/* Maps the library notification page and grants the lifecycle handles the thread still needs: the attach handle only
 * before its library enter. */
static int reserve_notifications(WitUserProcess *process, WitU32 index, WitUserThread *thread)
{
    const WitU64 address = WIT_USER_TLS + index * WIT_USER_THREAD_STRIDE + (WIT_LIBRARY_CAPACITY + 2) * 4096;
    if (!wit_user_space_map(&process->Space, address, 0, 0)) {
        return 0;
    }
    thread->LibraryNotificationPage = address;
    for (WitU32 n = thread->LibraryPhase == 1 ? 0U : 1U; n < 2; ++n) {
        thread->LibraryNotificationHandles[n] =
            wit_handle_grant(&process->Handles, WIT_HANDLE_LIBRARY_LIFECYCLE, WIT_RIGHT_READ);
        if (!thread->LibraryNotificationHandles[n]) {
            return 0;
        }
    }
    return 1;
}

static void release_notifications(WitUserProcess *process, WitUserThread *thread)
{
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
}

WitU64 wit_user_thread_require_notifications(WitUserProcess *process, WitU32 *reserved)
{
    *reserved = 0;
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        WitUserThread *thread = &process->Threads[i];
        if (thread->State == WitThreadEmpty ||
            thread->State == WitThreadExited ||
            !thread->LibraryNotifications ||
            thread->LibraryRequired ||
            (thread->LibraryPhase != 1 && thread->LibraryPhase != 2)) {
            continue;
        }
        require(!thread->LibraryNotificationPage, "Unrequired thread owns notification resources");
        if (!reserve_notifications(process, i, thread)) {
            release_notifications(process, thread);
            wit_user_thread_release_notifications(process, *reserved);
            *reserved = 0;
            return WIT_STATUS_NO_MEMORY;
        }
        thread->LibraryRequired = 1;
        *reserved |= 1U << i;
    }
    return WIT_STATUS_OK;
}

void wit_user_thread_release_notifications(WitUserProcess *process, WitU32 reserved)
{
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        if (reserved & (1U << i)) {
            release_notifications(process, &process->Threads[i]);
            process->Threads[i].LibraryRequired = 0;
        }
    }
}

/* Maps the stack, raw TLS, compiler TLS, library TLS and, when required, the library notification page and its
 * two lifecycle handles. */
static int map_thread(WitUserProcess *process, WitU32 index, WitUserThread *thread, ThreadPages *pages)
{
    for (WitU64 page = pages->Bottom; page < pages->Top; page += 4096) {
        if (!wit_user_space_map(&process->Space, page, 1, 0)) {
            return 0;
        }
        ++pages->Mapped;
    }
    if (!wit_user_space_map(&process->Space, pages->Tls, 1, 0)) {
        return 0;
    }
    pages->RawMapped = 1;
    if (process->TlsBytes && !map_compiler_tls(process, thread, pages->Tls + 4096)) {
        return 0;
    }
    if (!wit_user_library_tls_create_thread(process, index)) {
        return 0;
    }
    return !thread->LibraryRequired || reserve_notifications(process, index, thread);
}

static void unmap_thread(
    WitUserProcess *process, WitU32 index, WitUserThread *thread, const ThreadPages *pages, WitU64 handle)
{
    release_notifications(process, thread);
    wit_user_library_tls_reap_thread(process, index);
    if (thread->CompilerTls) {
        require(wit_user_space_unmap_fixed(&process->Space, thread->CompilerTls), "Compiler TLS rollback lost page");
        thread->CompilerTls = 0;
    }
    if (pages->RawMapped) {
        require(wit_user_space_unmap_fixed(&process->Space, pages->Tls), "Raw TLS rollback failed");
    }
    for (WitU32 mapped = pages->Mapped; mapped;) {
        --mapped;
        require(wit_user_space_unmap_fixed(&process->Space, pages->Bottom + mapped * 4096ULL),
            "Thread creation rollback lost a stack page");
    }
    require(wit_handle_close(&process->Handles, handle) == WIT_STATUS_OK, "Thread handle rollback failed");
}

/* Fills the raw TLS block, creates the initial frame and makes the thread Ready. */
static void start_thread(WitUserProcess *process, WitU32 index, WitUserThread *thread, const ThreadPages *pages,
    WitU64 handle, WitU64 entry, WitU64 argument)
{
    WitU64 *tls = (WitU64 *)wit_user_space_physical(&process->Space, pages->Tls, 1, 0);
    tls[0] = pages->Tls;
    tls[1] = handle;
    tls[2] = argument;
    WitArchFrame *context = wit_arch_frame_create(process->Slot, index, entry, argument, pages->Top);
    require(take_native_id(&next_native_id, &thread->NativeId), "Serialized native ID allocation failed");
    thread->Handle = handle;
    thread->StackBottom = pages->Bottom;
    thread->StackTop = pages->Top;
    thread->Tls = pages->Tls;
    thread->OwnsStack = 1;
    thread->ExitReservation = 0;
    thread->ExitCode = 0;
    thread->Context = context;
    thread->WaitKind = WitWaitNone;
    thread->WaitHandle = 0;
    thread->WaitCount = 0;
    thread->WaitAll = 0;
    wit_user_activations_clear(thread);
    for (WitU32 w = 0; w < WIT_WAIT_ANY_CAPACITY; ++w) {
        thread->WaitHandles[w] = 0;
    }
    thread->Deadline = WIT_WAIT_INFINITE;
    thread->WaitOrder = 0;
    thread->State = WitThreadReady;
    ++process->ThreadCreates;
}

/* Maps and starts a thread in a free slot. Its private identity is a handle-table entry user space never receives as
 * a capability (closing it is BUSY); the thread's stack and TLS are reclaimed at its exit. */
WitU64 wit_user_prepare_thread(WitUserProcess *process, WitU32 index, WitU64 entry, WitU64 argument, WitU64 flags)
{
    const WitU64 admission = wit_user_library_thread_admission(process, flags);
    if (admission != WIT_STATUS_OK) {
        return admission;
    }
    WitUserThread *thread = &process->Threads[index];
    ThreadPages pages = {WIT_USER_STACK_BOTTOM + index * WIT_USER_THREAD_STRIDE,
        WIT_USER_STACK_TOP + index * WIT_USER_THREAD_STRIDE, WIT_USER_TLS + index * WIT_USER_THREAD_STRIDE, 0, 0};
    const WitU64 reset = reset_thread(process, thread, flags);
    if (reset != WIT_STATUS_OK) {
        return reset;
    }
    const WitU64 handle = wit_handle_grant(&process->Handles, WIT_HANDLE_THREAD_IDENTITY, 0);
    if (!handle) {
        return WIT_STATUS_NO_MEMORY;
    }
    if (!map_thread(process, index, thread, &pages)) {
        unmap_thread(process, index, thread, &pages, handle);
        return WIT_STATUS_NO_MEMORY;
    }
    start_thread(process, index, thread, &pages, handle, entry, argument);
    return WIT_STATUS_OK;
}

/* The one form (version 2, K5.2a): a thread on the caller's stack with the caller's TLS base; the kernel maps nothing
 * for it. The stack pointer must be 16-byte aligned inside a committed writable reservation, whose bounds become the
 * thread's; the TLS base is a user address or zero. */
static WitU64 create_on_caller_stack(WitUserProcess *p, const WitThreadCreateRequest2 *request, WitU64 *result)
{
    WitU64 base = 0, bytes = 0;
    if (request->Size != sizeof(*request) || request->Reserved || (request->Flags & ~WIT_THREAD_START_SUSPENDED)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if ((request->StackPointer & 15) || request->TlsBase >= 0x0000800000000000ULL) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_space_physical(&p->Space, request->Entry, 0, 1)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (!wit_user_space_reservation_bounds(&p->Space, request->StackPointer, &base, &bytes) ||
        request->StackPointer <= base ||
        !wit_user_space_physical(&p->Space, request->StackPointer - 8, 1, 0)) {
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
    if (!reference || wit_handles_free_count(&p->Handles) < 2) {
        return WIT_STATUS_NO_MEMORY;
    }
    WitUserThread *thread = &p->Threads[index];
    const WitU64 reset = reset_thread(p, thread, 0);
    if (reset != WIT_STATUS_OK) {
        return reset;
    }
    const WitU64 handle = wit_handle_grant(&p->Handles, WIT_HANDLE_THREAD_REFERENCE, WIT_RIGHT_THREAD_ALL);
    const WitU64 identity = wit_handle_grant(&p->Handles, WIT_HANDLE_THREAD_IDENTITY, 0);
    require(handle != 0 && identity != 0, "Thread handles failed after the free slot check");
    WitArchFrame *context =
        wit_arch_frame_create_at(p->Slot, index, request->Entry, request->Argument, request->StackPointer);
    require(take_native_id(&next_native_id, &thread->NativeId), "Serialized native ID allocation failed");
    thread->Handle = identity;
    thread->StackBottom = base;
    thread->StackTop = base + bytes;
    thread->Tls = request->TlsBase;
    thread->CompilerTls = 0;
    thread->OwnsStack = 0;
    thread->ExitReservation = 0;
    thread->ExitCode = 0;
    thread->Context = context;
    thread->WaitKind = WitWaitNone;
    thread->WaitHandle = 0;
    thread->WaitCount = 0;
    thread->WaitAll = 0;
    wit_user_activations_clear(thread);
    for (WitU32 w = 0; w < WIT_WAIT_ANY_CAPACITY; ++w) {
        thread->WaitHandles[w] = 0;
    }
    thread->Deadline = WIT_WAIT_INFINITE;
    thread->WaitOrder = 0;
    thread->SuspendCount = (request->Flags & WIT_THREAD_START_SUSPENDED) ? 1U : 0U;
    thread->State = WitThreadReady;
    ++p->ThreadCreates;
    reference->Handle = handle;
    reference->ThreadId = identity;
    reference->ExitCode = 0;
    reference->Rights = WIT_RIGHT_THREAD_ALL;
    reference->Exited = 0;
    *result = handle;
    return WIT_STATUS_OK;
}

/* THREAD_SET_TLS: the raw TLS base of the current thread, applied at its next return to user mode. */
WitU64 wit_user_thread_set_tls(WitUserProcess *p, WitU64 base, WitU64 reserved0, WitU64 reserved1)
{
    if (reserved0 || reserved1 || base >= 0x0000800000000000ULL) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_arch_user_tls_settable()) {
        return WIT_STATUS_UNSUPPORTED;
    }
    p->Threads[p->CurrentThread].Tls = base;
    return WIT_STATUS_OK;
}

/* THREAD_CREATE: version 1 maps the kernel's stack and TLS (the frozen line), version 2 is the one form. The request
 * is validated as a whole, the thread handle is reserved first, and nothing is published before the identity,
 * stacks, TLS and initial suspend state all exist. */
WitU64 wit_user_thread_create(WitUserProcess *p, WitU64 input, WitU64 size, WitU64 *result)
{
    *result = 0;
    if (size != sizeof(WitThreadCreateRequest)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitThreadCreateRequest request;
    if (!wit_user_copy_from(&p->Space, input, (WitU8 *)&request, sizeof(request))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (request.Version == WIT_THREAD_CREATE_VERSION_2) {
        return create_on_caller_stack(p, (const WitThreadCreateRequest2 *)&request, result);
    }
    if (request.Version != WIT_THREAD_CREATE_VERSION) {
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
    const WitU64 handle = wit_handle_grant(&p->Handles, WIT_HANDLE_THREAD_REFERENCE, WIT_RIGHT_THREAD_ALL);
    if (!handle) {
        return WIT_STATUS_NO_MEMORY;
    }
    const WitU64 status = wit_user_prepare_thread(
        p, index, request.Entry, request.Argument, request.Flags & WIT_THREAD_LIBRARY_NOTIFICATIONS);
    if (status != WIT_STATUS_OK) {
        if (wit_handle_close(&p->Handles, handle) != WIT_STATUS_OK) {
            wit_panic("Thread creation rollback failed");
        }
        return status;
    }
    WitUserThread *thread = &p->Threads[index];
    thread->SuspendCount = (request.Flags & WIT_THREAD_START_SUSPENDED) ? 1U : 0U;
    reference->Handle = handle;
    reference->ThreadId = thread->Handle;
    reference->ExitCode = 0;
    reference->Rights = WIT_RIGHT_THREAD_ALL;
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
