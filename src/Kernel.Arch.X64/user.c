#include "user.h"
#include "witos/platform.h"

unsigned __int64 __readcr3(void);
unsigned __int64 __readmsr(unsigned long);
void __writemsr(unsigned long, unsigned __int64);
#pragma intrinsic(__readcr3, __readmsr, __writemsr)

#define NO_THREAD WIT_USER_THREAD_CAPACITY
#define FS_BASE 0xC0000100UL

static WitUserProcess *current_user;
static WitUserProcess *slot_owners[2];
static WitU32 next_id = 1;
static volatile WitU32 user_idle;

static void require(int condition, const char *message)
{
    if (!condition) wit_panic(message);
}

static WitU64 stack_low(const WitUserProcess *process, WitU32 thread)
{
    return (WitU64)wit_x64_user_kernel_stacks[process->Slot][thread] + 4096;
}

static int frame_inside_kernel_stack(const void *frame, WitU64 size, WitU32 thread)
{
    const WitU64 low = stack_low(current_user, thread);
    return (WitU64)frame >= low && (WitU64)frame <= low + WIT_KERNEL_STACK_SIZE - size;
}

static WIT_NORETURN void finish(WitUserState state, WitU64 code)
{
    require(current_user != 0 && current_user->State == WitUserRunning, "No current user component");
    wit_x64_timer_stop();
    current_user->State = state;
    current_user->ExitCode = code;
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        if (current_user->Threads[i].State != WitThreadEmpty) current_user->Threads[i].State = WitThreadExited;
        current_user->Threads[i].WaitingOn = NO_THREAD;
        current_user->Threads[i].Joiner = NO_THREAD;
        current_user->Threads[i].WaitKind = WitWaitNone;
        current_user->Threads[i].WaitHandle = 0;
        current_user->Threads[i].Deadline = WIT_WAIT_INFINITE;
    }
    wit_handles_close_all(&current_user->Handles);
    wit_events_initialize(&current_user->Events);
    user_idle = 0;
    __writemsr(FS_BASE, 0); /* Kernel C has no segment-based TLS. */
    current_user = 0;
    wit_x64_leave_user();
}

static void validate_return(WitInterruptContext *context, WitU32 index, int syscall)
{
    const WitUserThread *thread = &current_user->Threads[index];
    require(frame_inside_kernel_stack(context, sizeof(*context), index) &&
        ((WitU64)context & 15) == 0, "User trap outside owning kernel stack");
    if (context->Cs != WIT_USER_CS || context->Ss != WIT_USER_SS ||
        context->Rsp < thread->StackBottom || context->Rsp >= thread->StackTop ||
        !wit_user_space_physical(&current_user->Space, context->Rsp, 1, 0) ||
        !wit_user_space_physical(&current_user->Space, context->Rip, 0, 1)) {
        finish(WitUserBadReturn, 0);
    }
    context->Rflags = syscall ? 0x202 : (context->Rflags & 0x200CD5ULL) | 0x202;
}

static WitU64 prepare_thread(WitUserProcess *process, WitU32 index, WitU64 entry, WitU64 argument)
{
    WitUserThread *thread = &process->Threads[index];
    WitU64 handle, physical, *tls;
    WitU32 mapped = 0;
    WitInterruptContext *context;
    const WitU64 bottom = WIT_USER_STACK_BOTTOM + index * WIT_USER_THREAD_STRIDE;
    const WitU64 top = WIT_USER_STACK_TOP + index * WIT_USER_THREAD_STRIDE;
    const WitU64 tls_address = WIT_USER_TLS + index * WIT_USER_THREAD_STRIDE;
    handle = wit_handle_grant(&process->Handles, WIT_HANDLE_THREAD, WIT_RIGHT_JOIN);
    if (!handle) return WIT_STATUS_NO_MEMORY;
    for (WitU64 page = bottom; page < top; page += 4096) {
        if (!wit_user_space_map(&process->Space, page, 1, 0)) goto failed;
        ++mapped;
    }
    if (!wit_user_space_map(&process->Space, tls_address, 1, 0)) goto failed;
    physical = wit_user_space_physical(&process->Space, tls_address, 1, 0);
    tls = (WitU64 *)physical;
    tls[0] = tls_address;
    tls[1] = handle;
    tls[2] = argument;
    context = (WitInterruptContext *)(stack_low(process, index) + WIT_KERNEL_STACK_SIZE - 4096);
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
    thread->State = WitThreadReady;
    ++process->ThreadCreates;
    return WIT_STATUS_OK;
failed:
    while (mapped) {
        --mapped;
        require(wit_user_space_unmap_fixed(&process->Space, bottom + mapped * 4096ULL),
            "Thread creation rollback lost a stack page");
    }
    require(wit_handle_close(&process->Handles, handle) == WIT_STATUS_OK, "Thread handle rollback failed");
    return WIT_STATUS_NO_MEMORY;
}

WitU64 wit_user_thread_create(WitUserProcess *process, WitU64 entry, WitU64 argument, WitU64 *result)
{
    *result = 0;
    if (!wit_user_space_physical(&process->Space, entry, 0, 1)) return WIT_STATUS_BAD_ADDRESS;
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        WitU64 status;
        if (process->Threads[i].State != WitThreadEmpty) continue;
        status = prepare_thread(process, i, entry, argument);
        if (status == WIT_STATUS_OK) *result = process->Threads[i].Handle;
        return status;
    }
    return WIT_STATUS_NO_MEMORY;
}

static void reap(WitU32 index)
{
    WitUserThread *thread = &current_user->Threads[index];
    require(thread->State == WitThreadExited && thread->Joiner == NO_THREAD, "Reaping live/joined thread");
    for (WitU64 p = thread->StackBottom; p < thread->StackTop; p += 4096)
        require(wit_user_space_unmap_fixed(&current_user->Space, p), "Thread stack ownership lost");
    require(wit_user_space_unmap_fixed(&current_user->Space, thread->Tls), "Thread TLS ownership lost");
    require(wit_handle_close(&current_user->Handles, thread->Handle) == WIT_STATUS_OK, "Thread handle lost");
    thread->Handle = 0;
    thread->State = WitThreadEmpty;
    ++current_user->ThreadReaps;
}

static WitU32 thread_index(WitU64 handle)
{
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i)
        if (current_user->Threads[i].State != WitThreadEmpty && current_user->Threads[i].Handle == handle)
            return i;
    wit_panic("Live thread handle without thread");
}

static WitInterruptContext *dispatch(int timer, WitU64 last_exit)
{
    const WitU32 previous = current_user->CurrentThread;
    for (;;) {
        int waiting = 0;
        wit_user_wait_expire(current_user, wit_x64_clock_ticks());
        for (WitU32 offset = 1; offset <= WIT_USER_THREAD_CAPACITY; ++offset) {
            const WitU32 index = (previous + offset) % WIT_USER_THREAD_CAPACITY;
            WitUserThread *thread = &current_user->Threads[index];
            if (thread->State == WitThreadWaiting) waiting = 1;
            if (thread->State != WitThreadReady) continue;
            validate_return(thread->Context, index, 0);
            if (index != previous) {
                ++current_user->ThreadSwitches;
                if (timer) ++current_user->ThreadTimerSwitches;
            }
            current_user->CurrentThread = index;
            thread->State = WitThreadRunning;
            wit_x64_set_kernel_stack(stack_low(current_user, index) + WIT_KERNEL_STACK_SIZE);
            wit_x64_set_user_tls(thread->Tls);
            return thread->Context;
        }
        if (!waiting) finish(WitUserExited, last_exit);
        /* Remain on this kernel stack until an IRQ returns to the instruction
         * after HLT. The nested IRQ never replaces any saved user context. */
        require((wit_x64_read_flags() & 0x200) == 0, "Idle entered with interrupts enabled");
        __writemsr(FS_BASE, 0);
        user_idle = 1;
        ++current_user->IdleHalts;
        wit_x64_idle_once();
        user_idle = 0;
    }
}

static WitInterruptContext *exit_thread(WitU64 code)
{
    const WitU32 index = current_user->CurrentThread;
    WitUserThread *thread = &current_user->Threads[index];
    thread->State = WitThreadExited;
    thread->ExitCode = code;
    if (thread->Joiner != NO_THREAD) {
        WitUserThread *waiter = &current_user->Threads[thread->Joiner];
        require(waiter->State == WitThreadWaiting && waiter->WaitingOn == index, "Invalid thread waiter");
        waiter->Context->Rax = WIT_STATUS_OK;
        waiter->Context->Rdx = code;
        waiter->WaitingOn = NO_THREAD;
        waiter->WaitKind = WitWaitNone;
        waiter->State = WitThreadReady;
        thread->Joiner = NO_THREAD;
        ++current_user->ThreadJoins;
        reap(index);
    }
    return dispatch(0, code);
}

static WitInterruptContext *join_thread(WitInterruptContext *context, WitU64 handle)
{
    WitU32 target, walk;
    WitUserThread *thread, *caller;
    context->Rax = wit_handle_check(&current_user->Handles, handle, WIT_HANDLE_THREAD, WIT_RIGHT_JOIN);
    if (context->Rax != WIT_STATUS_OK) return context;
    target = thread_index(handle);
    thread = &current_user->Threads[target];
    walk = target;
    /* Edges and wakeup publication are serialized under the interrupt gate.
     * Reject self-join and any cycle before installing a wait edge. */
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        if (walk == current_user->CurrentThread) {
            ++current_user->ThreadDeadlocks;
            context->Rax = WIT_STATUS_DEADLOCK;
            return context;
        }
        if (current_user->Threads[walk].State != WitThreadWaiting ||
            current_user->Threads[walk].WaitKind != WitWaitJoin) break;
        walk = current_user->Threads[walk].WaitingOn;
        require(walk < WIT_USER_THREAD_CAPACITY, "Invalid join edge");
    }
    if (thread->Joiner != NO_THREAD) { context->Rax = WIT_STATUS_BUSY; return context; }
    if (thread->State == WitThreadExited) {
        context->Rdx = thread->ExitCode;
        ++current_user->ThreadJoins;
        reap(target);
        return context;
    }
    caller = &current_user->Threads[current_user->CurrentThread];
    caller->State = WitThreadWaiting;
    caller->WaitingOn = target;
    caller->WaitKind = WitWaitJoin;
    caller->Deadline = WIT_WAIT_INFINITE;
    thread->Joiner = current_user->CurrentThread;
    return dispatch(0, 0);
}

static WitU64 close_handle(WitU64 handle)
{
    const WitU64 status = wit_handle_check(&current_user->Handles, handle, WIT_HANDLE_THREAD, 0);
    WitU32 index;
    if (status == WIT_STATUS_WRONG_TYPE) {
        const WitU64 event_status = wit_user_event_close(current_user, handle);
        return event_status == WIT_STATUS_WRONG_TYPE ?
            wit_handle_close(&current_user->Handles, handle) : event_status;
    }
    if (status != WIT_STATUS_OK) return status;
    index = thread_index(handle);
    if (current_user->Threads[index].State != WitThreadExited || current_user->Threads[index].Joiner != NO_THREAD)
        return WIT_STATUS_BUSY;
    reap(index);
    return WIT_STATUS_OK;
}

static int can_create(const WitUserProcess *process, WitU32 slot)
{
    return process && !current_user && slot < 2 && !slot_owners[slot] && next_id &&
        slot_owners[0] != process && slot_owners[1] != process && (wit_x64_read_flags() & 0x200) == 0;
}

static WitPeStatus create_process(WitUserProcess *process, WitPageAllocator *allocator,
    WitU32 slot, const WitU8 *code, WitU32 code_size, const WitPeImage *image, WitU64 base)
{
    WitUserStartup *startup;
    WitU64 physical;
    if (!can_create(process, slot)) return WitPeBusy;
    process->Id = next_id++;
    process->Slot = slot;
    process->State = WitUserEmpty;
    process->Writes = 0;
    process->Ticks = 0;
    process->ExitCode = 0;
    process->ImageBase = image ? base : WIT_USER_CODE;
    process->ImageEntry = image ? base + image->EntryRva : WIT_USER_CODE;
    process->ImageSize = image ? image->ImageSize : code_size;
    process->FaultVector = 0;
    process->FaultError = 0;
    process->FaultAddress = 0;
    process->FaultRip = 0;
    process->FaultCs = 0;
    process->FaultSs = 0;
    process->CurrentThread = 0;
    process->FaultThread = NO_THREAD;
    process->ThreadCreates = 0;
    process->ThreadSwitches = 0;
    process->ThreadTimerSwitches = 0;
    process->ThreadJoins = 0;
    process->ThreadReaps = 0;
    process->ThreadDeadlocks = 0;
    process->NextWaitOrder = 0;
    process->EventParks = 0;
    process->EventWakes = 0;
    process->WaitTimeouts = 0;
    process->WaitCloses = 0;
    process->IdleHalts = 0;
    process->IdleTicks = 0;
    wit_events_initialize(&process->Events);
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) process->Threads[i].State = WitThreadEmpty;
    wit_handles_initialize(&process->Handles, process->Id);
    slot_owners[slot] = process;
    if (!wit_user_space_create(&process->Space, allocator)) goto failed;
    if (image) {
        if (!wit_user_image_map(&process->Space, code, image, base)) goto failed;
    } else if (!wit_user_space_map(&process->Space, WIT_USER_CODE, 0, 1)) goto failed;
    if (!wit_user_space_map(&process->Space, WIT_USER_INFO, 0, 0)) goto failed;
    for (WitU64 page = WIT_USER_DATA; page < WIT_USER_DATA_END; page += 4096)
        if (!wit_user_space_map(&process->Space, page, 1, 0)) goto failed;
    if (!image) {
        physical = wit_user_space_physical(&process->Space, WIT_USER_CODE, 0, 1);
        for (WitU32 i = 0; i < code_size; ++i) ((WitU8 *)physical)[i] = code[i];
    }
    startup = (WitUserStartup *)wit_user_space_physical(&process->Space, WIT_USER_INFO, 0, 0);
    startup->Version = WIT_ABI_VERSION;
    startup->Size = sizeof(*startup);
    startup->ImageInfo = 0;
    if (image) {
        WitUserImageInfo *info = (WitUserImageInfo *)((WitU8 *)startup + WIT_USER_IMAGE_INFO_OFFSET);
        info->Version = WIT_IMAGE_INFO_VERSION;
        info->Size = sizeof(*info);
        info->Base = process->ImageBase;
        info->Entry = process->ImageEntry;
        info->ImageSize = process->ImageSize;
        info->RangeCount = image->SectionCount;
        info->HeadersSize = image->HeadersSize;
        info->UnwindRva = image->UnwindRva;
        info->UnwindSize = image->UnwindSize;
        for (WitU32 i = 0; i < image->SectionCount; ++i) {
            const WitPeSection *s = &image->Sections[i];
            info->Ranges[i].Rva = s->Rva;
            info->Ranges[i].Size = s->VirtualSize;
            info->Ranges[i].InitializedSize = s->RawSize < s->VirtualSize ? s->RawSize : s->VirtualSize;
            info->Ranges[i].Flags = s->Flags;
        }
        startup->ImageInfo = WIT_USER_INFO + WIT_USER_IMAGE_INFO_OFFSET;
    }
    startup->ConsoleHandle = wit_handle_grant(&process->Handles, WIT_HANDLE_CONSOLE, WIT_RIGHT_WRITE);
    if (!startup->ConsoleHandle || prepare_thread(process, 0, process->ImageEntry, WIT_USER_INFO) != WIT_STATUS_OK)
        goto failed;
    process->State = WitUserReady;
    return WitPeOk;
failed:
    wit_user_destroy(process);
    return WitPeNoMemory;
}

int wit_user_create(WitUserProcess *process, WitPageAllocator *allocator,
    WitU32 slot, const WitU8 *code, WitU32 code_size)
{
    if (!code || !code_size || code_size > 4096) return 0;
    return create_process(process, allocator, slot, code, code_size, 0, 0) == WitPeOk;
}

WitPeStatus wit_user_create_pe(WitUserProcess *process, WitPageAllocator *allocator,
    WitU32 slot, const WitU8 *file, WitU32 size, WitU64 base)
{
    WitPeImage image;
    WitPeStatus status;
    if (!can_create(process, slot)) return WitPeBusy;
    status = wit_pe_validate(file, size, &image);
    if (status != WitPeOk) return status;
    if ((base & 65535) || base < WIT_USER_IMAGE_BASE || base >= WIT_USER_LIMIT ||
        image.ImageSize > WIT_USER_LIMIT - base) return WitPeBadBase;
    if (base != image.PreferredBase && !image.RelocSize) return WitPeUnsupportedImage;
    return create_process(process, allocator, slot, file, size, &image, base);
}

void wit_user_run(WitUserProcess *process)
{
    require(!current_user && !user_idle && process->State == WitUserReady &&
        slot_owners[process->Slot] == process &&
        (wit_x64_read_flags() & 0x200) == 0 && __readmsr(FS_BASE) == 0, "Invalid user launch");
    current_user = process;
    process->State = WitUserRunning;
    process->Threads[0].State = WitThreadRunning;
    process->Ticks = 0;
    wit_x64_set_kernel_stack(stack_low(process, 0) + WIT_KERNEL_STACK_SIZE);
    wit_x64_timer_start();
    wit_x64_set_user_tls(process->Threads[0].Tls);
    wit_x64_run_user(process->Threads[0].Context, process->Space.Root);
    require(!current_user && process->State != WitUserRunning &&
        (__readcr3() & 0x000FFFFFFFFFF000ULL) == wit_virtual_kernel_root() &&
        (wit_x64_read_flags() & 0x200) == 0 && __readmsr(FS_BASE) == 0, "User return did not restore kernel state");
    wit_x64_set_kernel_stack((WitU64)wit_x64_kernel_stack + 4096 + WIT_KERNEL_STACK_SIZE);
}

void wit_user_destroy(WitUserProcess *process)
{
    require(current_user != process && process->State != WitUserRunning, "Destroying running component");
    wit_handles_close_all(&process->Handles);
    wit_events_initialize(&process->Events);
    wit_user_space_destroy(&process->Space);
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) process->Threads[i].State = WitThreadEmpty;
    if (process->Slot < 2 && slot_owners[process->Slot] == process) slot_owners[process->Slot] = 0;
    process->State = WitUserEmpty;
    process->ImageBase = 0;
    process->ImageEntry = 0;
    process->ImageSize = 0;
}

int wit_user_is_active(void) { return current_user != 0; }

WitInterruptContext *wit_user_timer_tick(WitInterruptContext *context)
{
    require(current_user != 0, "User timer without component");
    if (user_idle) {
        const WitU64 low = stack_low(current_user, current_user->CurrentThread);
        require(frame_inside_kernel_stack(context, sizeof(*context), current_user->CurrentThread) &&
            ((WitU64)context & 15) == 0 && context->Cs == 8 &&
            (context->Ss == 0 || context->Ss == 0x10) &&
            context->Rsp >= low && context->Rsp < low + WIT_KERNEL_STACK_SIZE &&
            context->Rip == (WitU64)wit_x64_idle_resume && __readmsr(FS_BASE) == 0,
            "Timer did not interrupt the kernel idle path");
        ++current_user->IdleTicks;
    } else {
        WitUserThread *thread = &current_user->Threads[current_user->CurrentThread];
        validate_return(context, current_user->CurrentThread, 0);
        thread->Context = context;
        thread->State = WitThreadReady;
    }
    wit_user_wait_expire(current_user, wit_x64_clock_ticks());
    if (++current_user->Ticks >= WIT_USER_TICK_BUDGET) finish(WitUserBudgetExpired, 0);
    if (user_idle) return context; /* Resume CLI/RET and recheck ready threads. */
    return dispatch(1, 0);
}

WitInterruptContext *wit_x64_user_syscall(WitInterruptContext *context)
{
    WitU8 buffer[WIT_ABI_MAX_WRITE];
    WitU64 status;
    WitU64 call, argument0, argument1, argument2;
    require(current_user != 0 && current_user->State == WitUserRunning, "Syscall without component");
    validate_return(context, current_user->CurrentThread, 1);
    wit_user_wait_expire(current_user, wit_x64_clock_ticks());
    current_user->Threads[current_user->CurrentThread].Context = context;
    call = context->Rax;
    argument0 = context->Rcx;
    argument1 = context->Rdx;
    argument2 = context->R8;
    context->Rax = WIT_STATUS_OK;
    context->Rdx = 0;
    switch (call) {
    case WIT_CALL_QUERY:
        context->Rdx = WIT_ABI_VERSION;
        break;
    case WIT_CALL_WRITE:
        status = wit_handle_check(&current_user->Handles, argument0, WIT_HANDLE_CONSOLE, WIT_RIGHT_WRITE);
        if (status != WIT_STATUS_OK) { context->Rax = status; break; }
        if (argument2 > WIT_ABI_MAX_WRITE) { context->Rax = WIT_STATUS_TOO_LARGE; break; }
        if (!wit_user_copy_from(&current_user->Space, argument1, buffer, (WitU32)argument2)) {
            context->Rax = WIT_STATUS_BAD_ADDRESS;
            break;
        }
        if (argument2) {
            wit_console_write("[USER] ");
            wit_console_write_buffer(buffer, (WitU32)argument2);
            ++current_user->Writes;
        }
        context->Rdx = argument2;
        break;
    case WIT_CALL_MEMORY_RESERVE:
        context->Rax = wit_user_memory_reserve(&current_user->Space, argument0, argument1, &context->Rdx);
        break;
    case WIT_CALL_MEMORY_COMMIT:
        context->Rax = wit_user_memory_commit(&current_user->Space, argument0, argument1, argument2);
        break;
    case WIT_CALL_MEMORY_DECOMMIT:
        context->Rax = wit_user_memory_decommit(&current_user->Space, argument0, argument1);
        break;
    case WIT_CALL_MEMORY_PROTECT:
        context->Rax = wit_user_memory_protect(&current_user->Space, argument0, argument1, argument2);
        break;
    case WIT_CALL_MEMORY_RELEASE:
        context->Rax = wit_user_memory_release(&current_user->Space, argument0);
        break;
    case WIT_CALL_MEMORY_QUERY:
        context->Rax = wit_user_memory_query(&current_user->Space, argument0, argument1, argument2);
        if (context->Rax == WIT_STATUS_OK) context->Rdx = WIT_MEMORY_INFO_SIZE;
        break;
    case WIT_CALL_CLOCK_READ:
        context->Rdx = wit_x64_clock_ticks();
        break;
    case WIT_CALL_CLOCK_FREQUENCY:
        context->Rdx = WIT_CLOCK_FREQUENCY;
        break;
    case WIT_CALL_THREAD_SLEEP:
        context->Rax = wit_user_sleep(current_user, argument0, wit_x64_clock_ticks());
        break;
    case WIT_CALL_EVENT_CREATE:
        context->Rax = wit_event_create(&current_user->Events, &current_user->Handles,
            argument0, WIT_RIGHT_WAIT | WIT_RIGHT_SIGNAL, &context->Rdx);
        break;
    case WIT_CALL_EVENT_SET:
        context->Rax = wit_user_event_set(current_user, argument0);
        break;
    case WIT_CALL_EVENT_RESET:
        context->Rax = wit_user_event_reset(current_user, argument0);
        break;
    case WIT_CALL_EVENT_WAIT:
        context->Rax = wit_user_event_wait(current_user, argument0, argument1, wit_x64_clock_ticks());
        break;
    case WIT_CALL_THREAD_CREATE:
        context->Rax = argument2 ? WIT_STATUS_INVALID_ARGUMENT :
            wit_user_thread_create(current_user, argument0, argument1, &context->Rdx);
        break;
    case WIT_CALL_THREAD_YIELD:
        current_user->Threads[current_user->CurrentThread].State = WitThreadReady;
        return dispatch(0, 0);
    case WIT_CALL_THREAD_EXIT:
        return exit_thread(argument0);
    case WIT_CALL_THREAD_JOIN:
        context = join_thread(context, argument0);
        break;
    case WIT_CALL_EXIT:
        finish(WitUserExited, argument0);
    case WIT_CALL_CLOSE:
        context->Rax = close_handle(argument0);
        break;
    default:
        context->Rax = WIT_STATUS_UNSUPPORTED;
        break;
    }
    if (current_user->Threads[current_user->CurrentThread].State == WitThreadWaiting)
        return dispatch(0, 0);
    /* Join may have switched current thread; nonblocking calls retain the caller. */
    wit_x64_set_user_tls(current_user->Threads[current_user->CurrentThread].Tls);
    return context;
}

WIT_NORETURN void wit_user_fault(const WitExceptionFrame *frame, WitU64 address)
{
    require(current_user != 0 && frame_inside_kernel_stack(frame, sizeof(*frame), current_user->CurrentThread) &&
        (frame->Cs & 3) == 3, "Invalid user fault frame");
    current_user->FaultThread = current_user->CurrentThread;
    current_user->FaultVector = frame->Vector;
    current_user->FaultError = frame->Error;
    current_user->FaultAddress = address;
    current_user->FaultRip = frame->Rip;
    current_user->FaultCs = frame->Cs;
    current_user->FaultSs = frame->Ss;
    wit_console_write("[USER-FAULT] id=");
    wit_console_write_u64(current_user->Id);
    wit_console_write(" vector=");
    wit_console_write_u64(frame->Vector);
    wit_console_write(" error=");
    wit_console_write_hex(frame->Error);
    wit_console_write(" address=");
    wit_console_write_hex(address);
    wit_console_write(" cs=");
    wit_console_write_hex(frame->Cs);
    wit_console_write("\n");
    finish(WitUserFaulted, 0);
}
