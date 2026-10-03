#include "user.h"
#include "witos/platform.h"
#include "witos/random.h"

/* System call table of the running component. Each handler receives the call and its arguments, writes the
 * status and value of the caller's frame and returns 0 to finish through the common path of wit_user_syscall,
 * or the frame to resume as it is: a restored context, a dispatched thread or the caller after a yield. */

#define CALL_COUNT (WIT_CALL_LIBRARY + 1U)

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static WitUserThread *caller(WitUserCall *call)
{
    return &call->Process->Threads[call->Process->CurrentThread];
}

static int has_arguments(const WitUserCall *call)
{
    return call->Argument0 || call->Argument1 || call->Argument2;
}

/* A call that restored the caller's context resumes it with its own registers, not a call result. */
static WitArchFrame *resume_restored(WitUserCall *call, WitU64 status)
{
    if (status == WIT_STATUS_OK) {
        wit_arch_set_user_tls(caller(call)->Tls, caller(call)->CompilerTls);
        return call->Context;
    }
    *call->Status = status;
    return 0;
}

static WitArchFrame *query(WitUserCall *call)
{
    *call->Value = WIT_ABI_VERSION;
    return 0;
}

static WitArchFrame *write(WitUserCall *call)
{
    WitU8 buffer[WIT_ABI_MAX_WRITE];
    WitUserProcess *process = call->Process;
    const WitU64 status = wit_handle_check(&process->Handles, call->Argument0, WIT_HANDLE_CONSOLE, WIT_RIGHT_WRITE);
    if (status != WIT_STATUS_OK) {
        *call->Status = status;
        return 0;
    }
    if (call->Argument2 > WIT_ABI_MAX_WRITE) {
        *call->Status = WIT_STATUS_TOO_LARGE;
        return 0;
    }
    if (!wit_user_copy_from(&process->Space, call->Argument1, buffer, (WitU32)call->Argument2)) {
        *call->Status = WIT_STATUS_BAD_ADDRESS;
        return 0;
    }
    if (call->Argument2) {
        wit_console_write("[USER] ");
        wit_console_write_buffer(buffer, (WitU32)call->Argument2);
        ++process->Writes;
    }
    *call->Value = call->Argument2;
    return 0;
}

static WitArchFrame *process_exit(WitUserCall *call)
{
    wit_user_finish(WitUserExited, call->Argument0);
}

static WitArchFrame *close(WitUserCall *call)
{
    *call->Status = wit_user_close_handle(call->Argument0);
    return 0;
}

static WitArchFrame *memory_reserve(WitUserCall *call)
{
    *call->Status = wit_user_memory_reserve(&call->Process->Space, call->Argument0, call->Argument1, call->Value);
    return 0;
}

static WitArchFrame *memory_commit(WitUserCall *call)
{
    WitUserSpace *space = &call->Process->Space;
    const WitU32 owned = space->OwnedCount;
    const WitU64 free = wit_pages_free_count(space->Allocator);
    *call->Status = wit_user_memory_commit(space, call->Argument0, call->Argument1, call->Argument2);
    if (*call->Status == WIT_STATUS_NO_MEMORY) {
        ++call->Process->MemoryCommitFailures;
        require(space->OwnedCount == owned && wit_pages_free_count(space->Allocator) == free,
            "Failed user commit changed settled ownership/accounting");
    }
    return 0;
}

static WitArchFrame *memory_decommit(WitUserCall *call)
{
    *call->Status = wit_user_memory_decommit(&call->Process->Space, call->Argument0, call->Argument1);
    return 0;
}

static WitArchFrame *memory_protect(WitUserCall *call)
{
    *call->Status = wit_user_memory_protect(&call->Process->Space, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *memory_release(WitUserCall *call)
{
    *call->Status = wit_user_memory_release(&call->Process->Space, call->Argument0);
    return 0;
}

static WitArchFrame *memory_query(WitUserCall *call)
{
    *call->Status = wit_user_memory_query(&call->Process->Space, call->Argument0, call->Argument1, call->Argument2);
    if (*call->Status == WIT_STATUS_OK) {
        *call->Value = WIT_MEMORY_INFO_SIZE;
    }
    return 0;
}

static WitArchFrame *memory_reset(WitUserCall *call)
{
    *call->Status = call->Argument2 ? WIT_STATUS_INVALID_ARGUMENT
                                    : wit_user_memory_reset(&call->Process->Space, call->Argument0, call->Argument1);
    return 0;
}

static WitArchFrame *memory_pressure_event(WitUserCall *call)
{
    *call->Status =
        has_arguments(call) ? WIT_STATUS_INVALID_ARGUMENT : wit_user_pressure_create(call->Process, call->Value);
    return 0;
}

static WitArchFrame *thread_create(WitUserCall *call)
{
    *call->Status =
        wit_user_thread_create_flags(call->Process, call->Argument0, call->Argument1, call->Argument2, call->Value);
    return 0;
}

static WitArchFrame *thread_create_reference(WitUserCall *call)
{
    *call->Status = call->Argument2
        ? WIT_STATUS_INVALID_ARGUMENT
        : wit_user_thread_create_reference(call->Process, call->Argument0, call->Argument1, call->Value);
    return 0;
}

static WitArchFrame *thread_yield(WitUserCall *call)
{
    WitArchFrame *next = wit_user_yield();
    *call->Value = next != call->Context ? 1 : 0;
    return next;
}

/* A coordinated worker must never free its TLS and stack while peers' user-space runtime may still hold its
 * record or GC roots: a raw or premature exit ends the whole component. */
static WIT_NORETURN void exit_abruptly(WitUserCall *call)
{
    call->Process->AbruptThreadId = caller(call)->Handle;
    call->Process->AbruptThreadCode = call->Argument0;
    wit_user_finish(WitUserExited, WIT_PROCESS_ABRUPT_THREAD_EXIT);
}

static int owns_library_lifecycle(WitUserCall *call)
{
    return call->Process->LibraryLifecycle.Token && call->Process->LibraryLifecycle.Owner == caller(call)->Handle;
}

static WitArchFrame *thread_exit(WitUserCall *call)
{
    if (call->Process->RequireThreadCompletion || caller(call)->LibraryRequired || owns_library_lifecycle(call)) {
        exit_abruptly(call);
    }
    return wit_user_exit_thread(call->Argument0);
}

static WitArchFrame *thread_complete(WitUserCall *call)
{
    if (call->Argument1 || call->Argument2) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
        return 0;
    }
    if ((caller(call)->LibraryRequired && caller(call)->LibraryPhase != 4) || owns_library_lifecycle(call)) {
        exit_abruptly(call);
    }
    ++call->Process->OrderlyThreadExits;
    return wit_user_exit_thread(call->Argument0);
}

static WitArchFrame *thread_join(WitUserCall *call)
{
    call->Context = wit_user_join_thread(call->Context, call->Argument0);
    return 0;
}

static WitArchFrame *thread_current(WitUserCall *call)
{
    if (has_arguments(call)) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
    } else {
        *call->Value = caller(call)->Handle;
    }
    return 0;
}

static WitArchFrame *thread_native_id(WitUserCall *call)
{
    if (has_arguments(call)) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
    } else {
        require(caller(call)->NativeId != 0, "Current thread has no native ID");
        *call->Value = caller(call)->NativeId;
    }
    return 0;
}

static WitArchFrame *thread_query(WitUserCall *call)
{
    *call->Status = wit_user_thread_query(call->Process, call->Argument0, call->Argument1, call->Argument2);
    if (*call->Status == WIT_STATUS_OK) {
        *call->Value = WIT_THREAD_INFO_SIZE;
    }
    return 0;
}

static WitArchFrame *thread_reference_duplicate(WitUserCall *call)
{
    *call->Status = wit_user_reference_duplicate(call->Process, call->Argument0, call->Argument1, call->Argument2);
    if (*call->Status == WIT_STATUS_OK) {
        *call->Value = sizeof(WitU64);
    }
    return 0;
}

static WitArchFrame *thread_reference_query(WitUserCall *call)
{
    *call->Status = wit_user_reference_query(call->Process, call->Argument0, call->Argument1, call->Argument2);
    if (*call->Status == WIT_STATUS_OK) {
        *call->Value = sizeof(WitThreadReferenceInfo);
    }
    return 0;
}

static WitArchFrame *thread_suspend(WitUserCall *call)
{
    *call->Status = (call->Argument1 || call->Argument2)
        ? WIT_STATUS_INVALID_ARGUMENT
        : wit_user_thread_suspend(call->Process, call->Argument0, call->Number == WIT_CALL_THREAD_RESUME, call->Value);
    return 0;
}

static WitArchFrame *thread_name_set(WitUserCall *call)
{
    *call->Status = wit_user_thread_name_set(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *thread_name_query(WitUserCall *call)
{
    *call->Status = wit_user_thread_name_query(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *thread_context_get(WitUserCall *call)
{
    *call->Status = wit_user_thread_context_get(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *thread_context_set(WitUserCall *call)
{
    *call->Status = wit_user_thread_context_set(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *thread_context_restore(WitUserCall *call)
{
    return resume_restored(
        call, wit_user_thread_context_restore(call->Process, call->Argument0, call->Argument1, call->Argument2));
}

static WitArchFrame *thread_context_metadata(WitUserCall *call)
{
    *call->Status = wit_user_thread_context_metadata(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *cpu_context_query(WitUserCall *call)
{
    *call->Status = wit_user_cpu_context_query(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *clock_read(WitUserCall *call)
{
    *call->Value = wit_arch_clock_ticks();
    return 0;
}

static WitArchFrame *clock_frequency(WitUserCall *call)
{
    *call->Value = WIT_CLOCK_FREQUENCY;
    return 0;
}

static WitArchFrame *thread_sleep(WitUserCall *call)
{
    *call->Status = wit_user_sleep(call->Process, call->Argument0, wit_arch_clock_ticks());
    return 0;
}

static WitArchFrame *monotonic_read(WitUserCall *call)
{
    *call->Value = wit_platform_monotonic_read();
    return 0;
}

static WitArchFrame *monotonic_frequency(WitUserCall *call)
{
    *call->Value = wit_platform_monotonic_frequency();
    return 0;
}

static WitArchFrame *monotonic_query(WitUserCall *call)
{
    if (call->Argument1 != sizeof(WitU64)) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
        return 0;
    }
    if (call->Argument2 > WIT_MONOTONIC_HZ) {
        *call->Status = WIT_STATUS_UNSUPPORTED;
        return 0;
    }
    const WitU64 sample =
        call->Argument2 == WIT_MONOTONIC_COUNTER ? wit_platform_monotonic_read() : wit_platform_monotonic_frequency();
    // The dispatcher keeps IF clear through sampling and whole-buffer copy.
    if (!wit_user_copy_to(&call->Process->Space, call->Argument0, (const WitU8 *)&sample, sizeof(sample))) {
        *call->Status = WIT_STATUS_BAD_ADDRESS;
    } else {
        *call->Value = sizeof(sample);
    }
    return 0;
}

static WitArchFrame *sleep_until(WitUserCall *call)
{
    *call->Status = (call->Argument1 || call->Argument2)
        ? WIT_STATUS_INVALID_ARGUMENT
        : wit_user_sleep_until(call->Process, call->Argument0, wit_platform_monotonic_read());
    return 0;
}

static WitArchFrame *event_create(WitUserCall *call)
{
    WitUserProcess *process = call->Process;
    *call->Status = wit_event_create(
        &process->Events, &process->Handles, call->Argument0, WIT_RIGHT_WAIT | WIT_RIGHT_SIGNAL, call->Value);
    if (*call->Status != WIT_STATUS_OK && process->Space.PageLimit > WIT_USER_PAGE_CAPACITY) {
        wit_console_write("[RUNTIME-RESOURCE] event failure status/events/handles: ");
        wit_console_write_u64(*call->Status);
        wit_console_write("/");
        wit_console_write_u64(process->Events.Count);
        wit_console_write("/");
        wit_console_write_u64(process->Handles.Count);
        wit_console_write("\n");
    }
    return 0;
}

static WitArchFrame *event_create_rights(WitUserCall *call)
{
    WitUserProcess *process = call->Process;
    *call->Status = (call->Argument2 || call->Argument1 > 0xFFFFFFFFULL)
        ? WIT_STATUS_INVALID_ARGUMENT
        : wit_event_create(&process->Events, &process->Handles, call->Argument0, (WitU32)call->Argument1, call->Value);
    return 0;
}

static WitArchFrame *event_set(WitUserCall *call)
{
    *call->Status = wit_user_event_set(call->Process, call->Argument0);
    return 0;
}

static WitArchFrame *event_reset(WitUserCall *call)
{
    *call->Status = wit_user_event_reset(call->Process, call->Argument0);
    return 0;
}

static WitArchFrame *event_wait(WitUserCall *call)
{
    *call->Status = wit_user_event_wait(call->Process, call->Argument0, call->Argument1, wit_arch_clock_ticks());
    return 0;
}

static WitArchFrame *event_wait_until(WitUserCall *call)
{
    *call->Status = call->Argument2
        ? WIT_STATUS_INVALID_ARGUMENT
        : wit_user_event_wait_until(call->Process, call->Argument0, call->Argument1, wit_platform_monotonic_read());
    return 0;
}

static WitArchFrame *event_wait_any_until(WitUserCall *call)
{
    *call->Status = wit_user_event_wait_any_until(
        call->Process, call->Argument0, call->Argument1, call->Argument2, wit_platform_monotonic_read(), call->Value);
    return 0;
}

static WitArchFrame *object_wait(WitUserCall *call)
{
    *call->Status = wit_user_object_wait(
        call->Process, call->Argument0, call->Argument1, call->Argument2, wit_platform_monotonic_read(), call->Value);
    return 0;
}

static WitArchFrame *apc_queue(WitUserCall *call)
{
    *call->Status = wit_user_apc_queue(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *apc_dequeue(WitUserCall *call)
{
    *call->Status = call->Argument2 ? WIT_STATUS_INVALID_ARGUMENT
                                    : wit_user_apc_dequeue(call->Process, call->Argument0, call->Argument1);
    if (*call->Status == WIT_STATUS_OK) {
        *call->Value = sizeof(WitUserApc);
    }
    return 0;
}

/* Processor services exist only for the sole online processor. */
static WitArchFrame *process_write_barrier(WitUserCall *call)
{
    if (has_arguments(call)) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
    } else if (WIT_USER_PROCESSOR_COUNT != 1) {
        *call->Status = WIT_STATUS_UNSUPPORTED;
    } else {
        wit_arch_process_write_barrier();
        ++call->Process->ProcessWriteBarriers;
    }
    return 0;
}

static WitArchFrame *cpu_cache_size(WitUserCall *call)
{
    if (has_arguments(call)) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
    } else if (WIT_USER_PROCESSOR_COUNT != 1) {
        *call->Status = WIT_STATUS_UNSUPPORTED;
    } else {
        *call->Value = wit_arch_cache_size();
        if (!*call->Value) {
            *call->Status = WIT_STATUS_UNSUPPORTED;
        }
    }
    return 0;
}

static WitArchFrame *processor_query(WitUserCall *call)
{
    const WitU32 processor = 0; // Sole online BSP: group 0, number 0, reserved 0.
    if (call->Argument1 != sizeof(processor) || call->Argument2) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
        return 0;
    }
    if (WIT_USER_PROCESSOR_COUNT != 1) {
        *call->Status = WIT_STATUS_UNSUPPORTED;
        return 0;
    }
    if (!wit_user_copy_to(&call->Process->Space, call->Argument0, (const WitU8 *)&processor, sizeof(processor))) {
        *call->Status = WIT_STATUS_BAD_ADDRESS;
    } else {
        *call->Value = sizeof(processor);
    }
    return 0;
}

static WitArchFrame *random(WitUserCall *call)
{
    WitU8 block[WIT_RANDOM_BLOCK_BYTES];
    WitUserProcess *process = call->Process;
    const WitU32 size = (WitU32)call->Argument1;
    if (call->Argument2) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
        return 0;
    }
    if (call->Argument1 > WIT_ABI_MAX_RANDOM) {
        *call->Status = WIT_STATUS_TOO_LARGE;
        return 0;
    }
    if (!wit_user_buffer_writable(&process->Space, call->Argument0, size)) {
        *call->Status = WIT_STATUS_BAD_ADDRESS;
        return 0;
    }
    for (WitU32 offset = 0; offset < size;) {
        WitU32 count = size - offset;
        if (count > sizeof(block)) {
            count = sizeof(block);
        }
        if (!wit_random_fill(block, count) ||
            !wit_user_copy_to(&process->Space, call->Argument0 + offset, block, count)) {
            wit_panic("Random copy invariant failed");
        }
        for (WitU32 i = 0; i < sizeof(block); ++i) {
            ((volatile WitU8 *)block)[i] = 0;
        }
        offset += count;
    }
    if (call->Argument1) {
        ++process->RandomRequests;
        process->RandomBytes += call->Argument1;
    }
    *call->Value = call->Argument1;
    return 0;
}

static WitArchFrame *console_write(WitUserCall *call)
{
    *call->Status = wit_user_console_write(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *stack_lease_acquire(WitUserCall *call)
{
    *call->Status = wit_user_stack_lease_acquire(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *stack_lease_query(WitUserCall *call)
{
    *call->Status = wit_user_stack_lease_query(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *stack_lease_release(WitUserCall *call)
{
    *call->Status = (call->Argument1 || call->Argument2) ? WIT_STATUS_INVALID_ARGUMENT
                                                         : wit_user_stack_lease_release(call->Process, call->Argument0);
    return 0;
}

static WitArchFrame *exception_register(WitUserCall *call)
{
    *call->Status = wit_user_exception_register(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *exception_query(WitUserCall *call)
{
    *call->Status = wit_user_exception_query(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *exception_begin(WitUserCall *call)
{
    *call->Status =
        wit_user_exception_begin(call->Process, call->Argument0, call->Argument1, call->Argument2, call->Value);
    return 0;
}

static WitArchFrame *exception_resume(WitUserCall *call)
{
    return resume_restored(call,
        call->Number == WIT_CALL_EXCEPTION_CONTINUE
            ? wit_user_exception_continue(call->Process, call->Argument0, call->Argument1, call->Argument2)
            : wit_user_exception_unwind(call->Process, call->Argument0, call->Argument1, call->Argument2));
}

static WitArchFrame *exception_reject(WitUserCall *call)
{
    if (call->Argument1 || call->Argument2) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
        return 0;
    }
    if (!call->Argument0 || caller(call)->Exception.Token != call->Argument0) {
        *call->Status = WIT_STATUS_BAD_HANDLE;
        return 0;
    }
    (void)wit_user_exception_trap(call->Context, ~0ULL, 0, 0); // Pending original fault is retained by the fatal path.
    wit_panic("Rejected exception unexpectedly resumed");
}

static WitArchFrame *fatal_arm(WitUserCall *call)
{
    WitUserProcess *process = call->Process;
    if (call->Argument0 > 0xFFFFFFFFULL || call->Argument1 || call->Argument2) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
        return 0;
    }
    if (process->FatalArmed) {
        *call->Status = WIT_STATUS_BUSY;
        return 0;
    }
    process->Fatal = (WitUserFatalInfo){0};
    process->Fatal.Version = WIT_FATAL_INFO_VERSION;
    process->Fatal.Size = sizeof(process->Fatal);
    process->Fatal.Code = (WitU32)call->Argument0;
    process->FatalOwner = caller(call)->Handle;
    process->FatalArmed = 1;
    return 0;
}

static WitArchFrame *fatal_report(WitUserCall *call)
{
    WitUserProcess *process = call->Process;
    WitUserFatalInfo info;
    if (!process->FatalArmed || process->FatalOwner != caller(call)->Handle) {
        *call->Status = WIT_STATUS_DENIED;
        return 0;
    }
    if (call->Argument1 != sizeof(WitUserFatalInfo) || call->Argument2 != WIT_FATAL_INFO_VERSION) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
        return 0;
    }
    if (!wit_user_copy_from(&process->Space, call->Argument0, (WitU8 *)&info, sizeof(info))) {
        *call->Status = WIT_STATUS_BAD_ADDRESS;
        return 0;
    }
    if (info.Version != WIT_FATAL_INFO_VERSION ||
        info.Size != sizeof(info) ||
        info.ParameterCount > WIT_FATAL_PARAMETER_CAPACITY) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
        return 0;
    }
    process->Fatal = info;
    return 0;
}

static WitArchFrame *code_memory(WitUserCall *call)
{
    *call->Status = wit_user_code_call(call->Process, call->Argument0, call->Argument1, call->Argument2, call->Value);
    return 0;
}

static WitArchFrame *file(WitUserCall *call)
{
    *call->Status = wit_user_file_call(call->Process, call->Argument0, call->Argument1, call->Argument2, call->Value);
    return 0;
}

static WitArchFrame *storage_query(WitUserCall *call)
{
    *call->Status =
        wit_user_storage_query(call->Process, call->Argument0, call->Argument1, call->Argument2, call->Value);
    return 0;
}

static WitArchFrame *library(WitUserCall *call)
{
    *call->Status =
        wit_user_library_call(call->Process, call->Argument0, call->Argument1, call->Argument2, call->Value);
    return 0;
}

static WitArchFrame *(*const handlers[CALL_COUNT])(WitUserCall *) = {
    [WIT_CALL_QUERY] = query,
    [WIT_CALL_WRITE] = write,
    [WIT_CALL_EXIT] = process_exit,
    [WIT_CALL_CLOSE] = close,
    [WIT_CALL_MEMORY_RESERVE] = memory_reserve,
    [WIT_CALL_MEMORY_COMMIT] = memory_commit,
    [WIT_CALL_MEMORY_DECOMMIT] = memory_decommit,
    [WIT_CALL_MEMORY_PROTECT] = memory_protect,
    [WIT_CALL_MEMORY_RELEASE] = memory_release,
    [WIT_CALL_THREAD_CREATE] = thread_create,
    [WIT_CALL_THREAD_YIELD] = thread_yield,
    [WIT_CALL_THREAD_EXIT] = thread_exit,
    [WIT_CALL_THREAD_JOIN] = thread_join,
    [WIT_CALL_CLOCK_READ] = clock_read,
    [WIT_CALL_CLOCK_FREQUENCY] = clock_frequency,
    [WIT_CALL_THREAD_SLEEP] = thread_sleep,
    [WIT_CALL_EVENT_CREATE] = event_create,
    [WIT_CALL_EVENT_SET] = event_set,
    [WIT_CALL_EVENT_RESET] = event_reset,
    [WIT_CALL_EVENT_WAIT] = event_wait,
    [WIT_CALL_MEMORY_QUERY] = memory_query,
    [WIT_CALL_MONOTONIC_READ] = monotonic_read,
    [WIT_CALL_MONOTONIC_FREQUENCY] = monotonic_frequency,
    [WIT_CALL_SLEEP_UNTIL] = sleep_until,
    [WIT_CALL_EVENT_WAIT_UNTIL] = event_wait_until,
    [WIT_CALL_THREAD_CURRENT] = thread_current,
    [WIT_CALL_MEMORY_RESET] = memory_reset,
    [WIT_CALL_THREAD_QUERY] = thread_query,
    [WIT_CALL_PROCESS_WRITE_BARRIER] = process_write_barrier,
    [WIT_CALL_CPU_CACHE_SIZE] = cpu_cache_size,
    [WIT_CALL_EVENT_WAIT_ANY_UNTIL] = event_wait_any_until,
    [WIT_CALL_MEMORY_PRESSURE_EVENT] = memory_pressure_event,
    [WIT_CALL_MONOTONIC_QUERY] = monotonic_query,
    [WIT_CALL_RANDOM] = random,
    [WIT_CALL_THREAD_REFERENCE_DUPLICATE] = thread_reference_duplicate,
    [WIT_CALL_THREAD_REFERENCE_QUERY] = thread_reference_query,
    [WIT_CALL_THREAD_NATIVE_ID] = thread_native_id,
    [WIT_CALL_OBJECT_WAIT] = object_wait,
    [WIT_CALL_APC_QUEUE] = apc_queue,
    [WIT_CALL_APC_DEQUEUE] = apc_dequeue,
    [WIT_CALL_EVENT_CREATE_RIGHTS] = event_create_rights,
    [WIT_CALL_CONSOLE_WRITE] = console_write,
    [WIT_CALL_PROCESSOR_QUERY] = processor_query,
    [WIT_CALL_THREAD_NAME_SET] = thread_name_set,
    [WIT_CALL_THREAD_NAME_QUERY] = thread_name_query,
    [WIT_CALL_CPU_CONTEXT_QUERY] = cpu_context_query,
    [WIT_CALL_THREAD_CONTEXT_GET] = thread_context_get,
    [WIT_CALL_THREAD_SUSPEND] = thread_suspend,
    [WIT_CALL_THREAD_RESUME] = thread_suspend,
    [WIT_CALL_THREAD_CONTEXT_SET] = thread_context_set,
    [WIT_CALL_THREAD_CONTEXT_RESTORE] = thread_context_restore,
    [WIT_CALL_THREAD_CONTEXT_METADATA] = thread_context_metadata,
    [WIT_CALL_STACK_LEASE_ACQUIRE] = stack_lease_acquire,
    [WIT_CALL_STACK_LEASE_QUERY] = stack_lease_query,
    [WIT_CALL_STACK_LEASE_RELEASE] = stack_lease_release,
    [WIT_CALL_EXCEPTION_REGISTER] = exception_register,
    [WIT_CALL_EXCEPTION_QUERY] = exception_query,
    [WIT_CALL_EXCEPTION_CONTINUE] = exception_resume,
    [WIT_CALL_EXCEPTION_REJECT] = exception_reject,
    [WIT_CALL_EXCEPTION_BEGIN] = exception_begin,
    [WIT_CALL_FATAL_ARM] = fatal_arm,
    [WIT_CALL_FATAL_REPORT] = fatal_report,
    [WIT_CALL_EXCEPTION_UNWIND] = exception_resume,
    [WIT_CALL_THREAD_COMPLETE] = thread_complete,
    [WIT_CALL_THREAD_CREATE_REFERENCE] = thread_create_reference,
    [WIT_CALL_CODE_MEMORY] = code_memory,
    [WIT_CALL_FILE] = file,
    [WIT_CALL_STORAGE_QUERY] = storage_query,
    [WIT_CALL_LIBRARY] = library,
};

WitArchFrame *wit_user_call(WitUserCall *call)
{
    if (call->Number >= CALL_COUNT || !handlers[call->Number]) {
        *call->Status = WIT_STATUS_UNSUPPORTED;
        return 0;
    }
    return handlers[call->Number](call);
}
