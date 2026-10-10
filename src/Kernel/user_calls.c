#include "user.h"
#include "witos/platform.h"
#include "witos/random.h"
#include "witos/clock.h"
#include "witos/cpu.h"

/* System call table of the running component: ABI-1 of RFC 0011 v3 section 7 in the layout of user_abi.h. Each
 * handler receives the call and its arguments, writes the status and value of the caller's frame and returns 0
 * to finish through the common path of wit_user_syscall, or the frame to resume as it is: a restored context, a
 * dispatched thread or the caller after a yield. */

#define CALL_COUNT (WIT_CALL_PROCESS_WRITE_BARRIER + 1U)

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
        wit_arch_set_user_tls(caller(call)->Tls);
        return call->Context;
    }
    *call->Status = status;
    return 0;
}

static WitArchFrame *query(WitUserCall *call)
{
    *call->Value = WIT_ABI_VERSION | ((WitU64)WIT_ABI_FEATURES << 32);
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
    *call->Status =
        wit_user_memory_reserve(&call->Process->Space, call->Argument0, call->Argument1, call->Argument2, call->Value);
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
    *call->Status = wit_user_memory_unmap(call->Process, call->Argument0, call->Argument1);
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
    *call->Status = call->Argument2
        ? WIT_STATUS_INVALID_ARGUMENT
        : wit_user_thread_create(call->Process, call->Argument0, call->Argument1, call->Value);
    return 0;
}

static WitArchFrame *thread_yield(WitUserCall *call)
{
    WitArchFrame *next = wit_user_yield();
    *call->Value = next != call->Context ? 1 : 0;
    return next;
}

/* THREAD_EXIT: the one exit. Whether a runtime's thread detached from its runtime first is layer 2's lifecycle. */
static WitArchFrame *thread_exit(WitUserCall *call)
{
    WitU64 base = 0, bytes = 0;
    WitThreadExitRequest exit = {0};
    if (call->Argument2) {
        /* The exit request (S2.1): the word and the event the kernel serves after the thread stopped, validated
         * whole. */
        if (!wit_user_copy_from(&call->Process->Space, call->Argument2, (WitU8 *)&exit, sizeof(exit))) {
            *call->Status = WIT_STATUS_BAD_ADDRESS;
            return 0;
        }
        if (exit.Version != WIT_THREAD_EXIT_VERSION) {
            *call->Status = WIT_STATUS_UNSUPPORTED;
            return 0;
        }
        if (exit.Size != sizeof(exit) || (exit.ClearAddress & 3)) {
            *call->Status = WIT_STATUS_INVALID_ARGUMENT;
            return 0;
        }
        if (exit.ClearAddress && !wit_user_buffer_writable(&call->Process->Space, exit.ClearAddress, 4)) {
            *call->Status = WIT_STATUS_BAD_ADDRESS;
            return 0;
        }
        if (exit.Event) {
            const WitU64 checked =
                wit_handle_check(&call->Process->Handles, exit.Event, WIT_HANDLE_EVENT, WIT_RIGHT_SIGNAL);
            if (checked != WIT_STATUS_OK) {
                *call->Status = checked;
                return 0;
            }
        }
    }
    if (call->Argument1) {
        /* The stack's reservation to release after the exit (K5.2a): exactly a reservation base of the caller's. */
        if (caller(call)->OwnsStack) {
            *call->Status = WIT_STATUS_INVALID_ARGUMENT;
            return 0;
        }
        if (!wit_user_space_reservation_bounds(&call->Process->Space, call->Argument1, &base, &bytes) ||
            base != call->Argument1) {
            *call->Status = WIT_STATUS_NOT_RESERVED;
            return 0;
        }
    }
    ++call->Process->ThreadExits;
    return wit_user_exit_thread(call->Argument0, call->Argument1, exit.ClearAddress, exit.Event);
}

static WitArchFrame *thread_set_tls(WitUserCall *call)
{
    *call->Status = wit_user_thread_set_tls(call->Process, call->Argument0, call->Argument1, call->Argument2);
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

static WitArchFrame *handle_duplicate(WitUserCall *call)
{
    *call->Status = wit_user_handle_duplicate(call->Process, call->Argument0, call->Argument1, call->Argument2);
    if (*call->Status == WIT_STATUS_OK) {
        *call->Value = sizeof(WitU64);
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

static WitArchFrame *context_profile(WitUserCall *call)
{
    *call->Status = wit_user_cpu_context_query(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *sleep_until(WitUserCall *call)
{
    *call->Status = (call->Argument1 || call->Argument2)
        ? WIT_STATUS_INVALID_ARGUMENT
        : wit_user_sleep_until(call->Process, call->Argument0, wit_platform_monotonic_read());
    return 0;
}

/* Rights 0 grant WAIT and SIGNAL. A failure of a runtime-profile component is reported for its tests. */
static WitArchFrame *event_create(WitUserCall *call)
{
    WitUserProcess *process = call->Process;
    const WitU32 rights = call->Argument1 ? (WitU32)call->Argument1 : (WIT_RIGHT_WAIT | WIT_RIGHT_SIGNAL);
    *call->Status = (call->Argument2 || call->Argument1 > 0xFFFFFFFFULL)
        ? WIT_STATUS_INVALID_ARGUMENT
        : wit_event_create(&process->Events, &process->Handles, call->Argument0, rights, call->Value);
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

/* The monotonic clock; UTC arrives with plan step K6. */
/* CLOCK_READ and CLOCK_FREQUENCY: the monotonic domain of the platform, or UTC (K6) where the board has a real-time
 * clock; an unknown clock is INVALID_ARGUMENT, UTC without a clock UNSUPPORTED. */
static WitArchFrame *clock_read(WitUserCall *call)
{
    if (call->Argument1 || call->Argument2) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
    } else if (call->Argument0 == WIT_CLOCK_MONOTONIC) {
        *call->Value = wit_platform_monotonic_read();
    } else if (call->Argument0 == WIT_CLOCK_UTC && wit_clock_utc_available()) {
        *call->Value = wit_clock_utc_read();
    } else {
        *call->Status = call->Argument0 == WIT_CLOCK_UTC ? WIT_STATUS_UNSUPPORTED : WIT_STATUS_INVALID_ARGUMENT;
    }
    return 0;
}

static WitArchFrame *clock_frequency(WitUserCall *call)
{
    if (call->Argument1 || call->Argument2) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
    } else if (call->Argument0 == WIT_CLOCK_MONOTONIC) {
        *call->Value = wit_platform_monotonic_frequency();
    } else if (call->Argument0 == WIT_CLOCK_UTC && wit_clock_utc_available()) {
        *call->Value = WIT_CLOCK_UTC_FREQUENCY;
    } else {
        *call->Status = call->Argument0 == WIT_CLOCK_UTC ? WIT_STATUS_UNSUPPORTED : WIT_STATUS_INVALID_ARGUMENT;
    }
    return 0;
}

/* CLOCK_SET (K6): the clock capability with WRITE, UTC alone, a value within the range and after the boot. */
static WitArchFrame *clock_set(WitUserCall *call)
{
    const WitU64 status = wit_handle_check(&call->Process->Handles, call->Argument0, WIT_HANDLE_CLOCK, WIT_RIGHT_WRITE);
    if (status != WIT_STATUS_OK) {
        *call->Status = status;
    } else if (call->Argument1 != WIT_CLOCK_UTC || !wit_clock_utc_set(call->Argument2)) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
    }
    return 0;
}

static WitArchFrame *debug_write(WitUserCall *call)
{
    *call->Status = wit_user_debug_write(call->Process, call->Argument0, call->Argument1, call->Argument2, call->Value);
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

static WitArchFrame *object_wait(WitUserCall *call)
{
    *call->Status = wit_user_object_wait(
        call->Process, call->Argument0, call->Argument1, call->Argument2, wit_platform_monotonic_read(), call->Value);
    return 0;
}

static WitArchFrame *memory_object_create(WitUserCall *call)
{
    *call->Status =
        wit_user_memory_object_create(call->Process, call->Argument0, call->Argument1, call->Argument2, call->Value);
    return 0;
}

static WitArchFrame *memory_object_map(WitUserCall *call)
{
    *call->Status =
        wit_user_memory_object_map(call->Process, call->Argument0, call->Argument1, call->Argument2, call->Value);
    return 0;
}

static WitArchFrame *code_publish(WitUserCall *call)
{
    *call->Status = call->Argument2 ? WIT_STATUS_INVALID_ARGUMENT
                                    : wit_user_code_publish(&call->Process->Space, call->Argument0, call->Argument1);
    return 0;
}

static WitArchFrame *interrupt_bind(WitUserCall *call)
{
    *call->Status =
        wit_user_interrupt_bind(call->Process, call->Argument0, call->Argument1, call->Argument2, call->Value);
    return 0;
}

static WitArchFrame *interrupt_ack(WitUserCall *call)
{
    *call->Status = wit_user_interrupt_ack(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *dma_pin(WitUserCall *call)
{
    *call->Status = wit_user_dma_pin(call->Process, call->Argument0, call->Argument1, call->Argument2, call->Value);
    return 0;
}

static WitArchFrame *dma_unpin(WitUserCall *call)
{
    *call->Status = wit_user_dma_unpin(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *process_create(WitUserCall *call)
{
    *call->Status =
        wit_user_process_create(call->Process, call->Argument0, call->Argument1, call->Argument2, call->Value);
    return 0;
}

static WitArchFrame *process_kill(WitUserCall *call)
{
    *call->Status = wit_user_process_kill(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *process_query(WitUserCall *call)
{
    *call->Status = wit_user_process_query(call->Process, call->Argument0, call->Argument1, call->Argument2);
    if (*call->Status == WIT_STATUS_OK) {
        *call->Value = call->Argument2;
    }
    return 0;
}

static WitArchFrame *device_acquire(WitUserCall *call)
{
    *call->Status =
        wit_user_device_acquire(call->Process, call->Argument0, call->Argument1, call->Argument2, call->Value);
    return 0;
}

static WitArchFrame *device_memory(WitUserCall *call)
{
    *call->Status =
        wit_user_device_memory(call->Process, call->Argument0, call->Argument1, call->Argument2, call->Value);
    return 0;
}

static WitArchFrame *channel_create(WitUserCall *call)
{
    *call->Status = wit_user_channel_create(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *channel_send(WitUserCall *call)
{
    *call->Status = wit_user_channel_send(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *channel_receive(WitUserCall *call)
{
    *call->Status =
        wit_user_channel_receive(call->Process, call->Argument0, call->Argument1, call->Argument2, call->Value);
    return 0;
}

static WitArchFrame *thread_activate(WitUserCall *call)
{
    *call->Status = wit_user_thread_activate(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

/* PROCESS_WRITE_BARRIER (RFC 0011 section 7.9): a fence on every online processor — this one, and the others through
 * the inter-processor interrupt the boot processor waits for (K7.2). */
static WitArchFrame *process_write_barrier(WitUserCall *call)
{
    if (has_arguments(call)) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
    } else {
        wit_arch_process_write_barrier();
        wit_cpus_fence_all();
        ++call->Process->ProcessWriteBarriers;
    }
    return 0;
}

/* PROCESSOR_QUERY (RFC 0011 section 7.9): the 4-byte form of the frozen line (the current processor as {group:u16,
 * number:u8, reserved:u8}), or the record form (K7.1) with the caller's Version and Size: the processor table, the
 * boot processor first. The whole destination is validated before it is written. */
static WitArchFrame *processor_query(WitUserCall *call)
{
    WitUserSpace *space = &call->Process->Space;
    WitU32 header[2];
    WitProcessorInfo info;
    if (call->Argument2) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
        return 0;
    }
    if (call->Argument1 == 4) {
        const WitU32 processor = wit_processors_current(); /* group 0, the kernel's number */
        if (!wit_user_copy_to(space, call->Argument0, (const WitU8 *)&processor, sizeof(processor))) {
            *call->Status = WIT_STATUS_BAD_ADDRESS;
        } else {
            *call->Value = sizeof(processor);
        }
        return 0;
    }
    if (call->Argument1 != sizeof(info)) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
        return 0;
    }
    if (!wit_user_buffer_writable(space, call->Argument0, sizeof(info)) ||
        !wit_user_copy_from(space, call->Argument0, (WitU8 *)header, sizeof(header))) {
        *call->Status = WIT_STATUS_BAD_ADDRESS;
        return 0;
    }
    if (header[0] != WIT_PROCESSOR_INFO_VERSION) {
        *call->Status = WIT_STATUS_UNSUPPORTED;
        return 0;
    }
    if (header[1] != sizeof(info)) {
        *call->Status = WIT_STATUS_INVALID_ARGUMENT;
        return 0;
    }
    for (WitU32 i = 0; i < sizeof(info); ++i) {
        ((WitU8 *)&info)[i] = 0;
    }
    info.Version = WIT_PROCESSOR_INFO_VERSION;
    info.Size = sizeof(info);
    info.Current = wit_processors_current();
    info.Count = wit_processors_count();
    info.Online = wit_processors_online();
    for (WitU32 i = 0; i < info.Count && i < WIT_PROCESSOR_CAPACITY; ++i) {
        require(wit_processors_record(i, &info.Processors[i]), "Processor table shrank under its count");
    }
    require(wit_user_copy_to(space, call->Argument0, (const WitU8 *)&info, sizeof(info)),
        "Validated processor info changed");
    *call->Value = sizeof(info);
    return 0;
}

static WitArchFrame *thread_affinity(WitUserCall *call)
{
    *call->Status = wit_user_thread_affinity(call->Process, call->Argument0, call->Argument1, call->Argument2);
    return 0;
}

static WitArchFrame *thread_stack_alternate(WitUserCall *call)
{
    *call->Status = wit_user_thread_alternate_stack(
        call->Process, call->Context, call->Argument0, call->Argument1, call->Argument2);
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

static WitArchFrame *exception_continue(WitUserCall *call)
{
    return resume_restored(
        call, wit_user_exception_continue(call->Process, call->Argument0, call->Argument1, call->Argument2));
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
    (void)wit_user_exception_trap(call->Context, ~0ULL, 0, 0); // The trap reports the pending original fault.
    wit_panic("Rejected exception unexpectedly resumed");
}

static WitArchFrame *(*const handlers[CALL_COUNT])(WitUserCall *) = {
    [WIT_CALL_QUERY] = query,
    [WIT_CALL_PROCESS_EXIT] = process_exit,
    [WIT_CALL_HANDLE_CLOSE] = close,
    [WIT_CALL_HANDLE_DUPLICATE] = handle_duplicate,
    [WIT_CALL_DEBUG_WRITE] = debug_write,
    [WIT_CALL_MEMORY_RESERVE] = memory_reserve,
    [WIT_CALL_MEMORY_COMMIT] = memory_commit,
    [WIT_CALL_MEMORY_DECOMMIT] = memory_decommit,
    [WIT_CALL_MEMORY_PROTECT] = memory_protect,
    [WIT_CALL_MEMORY_RELEASE] = memory_release,
    [WIT_CALL_MEMORY_RESET] = memory_reset,
    [WIT_CALL_MEMORY_QUERY] = memory_query,
    [WIT_CALL_MEMORY_PRESSURE_EVENT] = memory_pressure_event,
    [WIT_CALL_MEMORY_OBJECT_CREATE] = memory_object_create,
    [WIT_CALL_MEMORY_OBJECT_MAP] = memory_object_map,
    [WIT_CALL_CODE_PUBLISH] = code_publish,
    [WIT_CALL_THREAD_CREATE] = thread_create,
    [WIT_CALL_THREAD_EXIT] = thread_exit,
    [WIT_CALL_THREAD_YIELD] = thread_yield,
    [WIT_CALL_THREAD_SET_TLS] = thread_set_tls,
    [WIT_CALL_THREAD_QUERY] = thread_query,
    [WIT_CALL_THREAD_SUSPEND] = thread_suspend,
    [WIT_CALL_THREAD_RESUME] = thread_suspend,
    [WIT_CALL_THREAD_CONTEXT_GET] = thread_context_get,
    [WIT_CALL_THREAD_CONTEXT_SET] = thread_context_set,
    [WIT_CALL_CONTEXT_PROFILE] = context_profile,
    [WIT_CALL_THREAD_ACTIVATE] = thread_activate,
    [WIT_CALL_THREAD_AFFINITY] = thread_affinity,
    [WIT_CALL_THREAD_STACK_ALTERNATE] = thread_stack_alternate,
    [WIT_CALL_CHANNEL_CREATE] = channel_create,
    [WIT_CALL_CHANNEL_SEND] = channel_send,
    [WIT_CALL_CHANNEL_RECEIVE] = channel_receive,
    [WIT_CALL_DEVICE_ACQUIRE] = device_acquire,
    [WIT_CALL_DEVICE_MEMORY] = device_memory,
    [WIT_CALL_INTERRUPT_BIND] = interrupt_bind,
    [WIT_CALL_INTERRUPT_ACK] = interrupt_ack,
    [WIT_CALL_DMA_PIN] = dma_pin,
    [WIT_CALL_DMA_UNPIN] = dma_unpin,
    [WIT_CALL_PROCESS_CREATE] = process_create,
    [WIT_CALL_PROCESS_KILL] = process_kill,
    [WIT_CALL_PROCESS_QUERY] = process_query,
    [WIT_CALL_EVENT_CREATE] = event_create,
    [WIT_CALL_EVENT_SET] = event_set,
    [WIT_CALL_EVENT_RESET] = event_reset,
    [WIT_CALL_OBJECT_WAIT] = object_wait,
    [WIT_CALL_SLEEP_UNTIL] = sleep_until,
    [WIT_CALL_CLOCK_READ] = clock_read,
    [WIT_CALL_CLOCK_FREQUENCY] = clock_frequency,
    [WIT_CALL_CLOCK_SET] = clock_set,
    [WIT_CALL_RANDOM] = random,
    [WIT_CALL_EXCEPTION_REGISTER] = exception_register,
    [WIT_CALL_EXCEPTION_QUERY] = exception_query,
    [WIT_CALL_EXCEPTION_CONTINUE] = exception_continue,
    [WIT_CALL_EXCEPTION_REJECT] = exception_reject,
    [WIT_CALL_PROCESSOR_QUERY] = processor_query,
    [WIT_CALL_PROCESS_WRITE_BARRIER] = process_write_barrier,
};

WitArchFrame *wit_user_call(WitUserCall *call)
{
    if (call->Number >= CALL_COUNT || !handlers[call->Number]) {
        *call->Status = WIT_STATUS_UNSUPPORTED;
        return 0;
    }
    return handlers[call->Number](call);
}
