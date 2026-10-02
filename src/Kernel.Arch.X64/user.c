#include "user.h"
#include "cpu_cache.h"
#include "witos/random.h"
#include "witos/platform.h"

unsigned __int64 __readcr3(void);
unsigned __int64 __readmsr(unsigned long);
void __writemsr(unsigned long, unsigned __int64);
#pragma intrinsic(__readcr3, __readmsr, __writemsr)

#define NO_THREAD WIT_USER_THREAD_CAPACITY
#define FS_BASE 0xC0000100UL
#define GS_BASE 0xC0000101UL

static WitUserProcess *current_user;
static WitUserProcess *slot_owners[2];
static WitU32 next_id = 1;
static volatile WitU32 user_idle;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
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
    wit_arch_timer_stop();
#if defined(WITOS_TEST_RUNTIME_BOOT)
    if (state == WitUserBudgetExpired && current_user->RequireThreadCompletion) {
        wit_console_write("Runtime budget ticks/idle: ");
        wit_console_write_u64(current_user->Ticks);
        wit_console_write("/");
        wit_console_write_u64(current_user->IdleTicks);
        wit_console_write("\n");
        for (WitU32 n = 0; n < WIT_USER_THREAD_CAPACITY; ++n) {
            const WitUserThread *t = &current_user->Threads[n];
            if (t->State == WitThreadEmpty) {
                continue;
            }
            wit_console_write("Runtime budget thread/state/wait/suspend: ");
            wit_console_write_u64(n);
            wit_console_write("/");
            wit_console_write_u64(t->State);
            wit_console_write("/");
            wit_console_write_u64(t->WaitKind);
            wit_console_write("/");
            wit_console_write_u64(t->SuspendCount);
            wit_console_write("\n");
            const WitInterruptContext *c = t->Context;
            if (c) {
                wit_console_write("Runtime budget rip/rsp/rax/rcx/rdx/rbx/rsi/rdi: ");
                const WitU64 values[] = {c->Rip, c->Rsp, c->Rax, c->Rcx, c->Rdx, c->Rbx, c->Rsi, c->Rdi};
                for (WitU32 j = 0; j < 8; ++j) {
                    wit_console_write_hex(values[j]);
                    wit_console_write(j == 7 ? "\n" : "/");
                }
                WitU64 stack[16] = {0};
                if (c->Rsp >= t->StackBottom &&
                    c->Rsp <= t->StackTop - sizeof(stack) &&
                    wit_user_copy_from(&current_user->Space, c->Rsp, (WitU8 *)stack, sizeof(stack))) {
                    wit_console_write("Runtime budget stack: ");
                    for (WitU32 j = 0; j < 16; ++j) {
                        wit_console_write_hex(stack[j]);
                        wit_console_write(j == 15 ? "\n" : "/");
                    }
                }
            }
        }
    }
#endif
    if (current_user->FatalArmed) {
        state = WitUserExited;
        code = current_user->Fatal.Code;
    }
    current_user->FatalArmed = 0;
    current_user->FatalOwner = 0;
    current_user->State = state;
    current_user->ExitCode = code;
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        if (current_user->Threads[i].State != WitThreadEmpty) {
            current_user->Threads[i].State = WitThreadExited;
        }
        current_user->Threads[i].SuspendCount = 0;
        wit_user_thread_name_clear(&current_user->Threads[i]);
        current_user->Threads[i].WaitingOn = NO_THREAD;
        current_user->Threads[i].Joiner = NO_THREAD;
        current_user->Threads[i].WaitKind = WitWaitNone;
        current_user->Threads[i].WaitHandle = 0;
        current_user->Threads[i].WaitCount = 0;
        current_user->Threads[i].WaitAll = 0;
        current_user->Threads[i].WaitAlertable = 0;
        wit_user_apc_initialize(&current_user->Threads[i]);
        for (WitU32 w = 0; w < WIT_WAIT_ANY_CAPACITY; ++w) {
            current_user->Threads[i].WaitHandles[w] = 0;
        }
        current_user->Threads[i].Deadline = WIT_WAIT_INFINITE;
        current_user->Threads[i].MonotonicWait = 0;
    }
    wit_handles_close_all(&current_user->Handles);
    wit_files_initialize(&current_user->Files);
    wit_user_library_initialize(current_user);
    wit_user_references_initialize(current_user);
    wit_user_stack_leases_initialize(current_user);
    wit_user_exception_initialize(current_user);
    wit_events_initialize(&current_user->Events);
    user_idle = 0;
    __writemsr(FS_BASE, 0); /* Kernel C has no segment-based TLS. */
    __writemsr(GS_BASE, 0);
    current_user = 0;
    wit_arch_leave_user();
}

static void validate_return(WitInterruptContext *context, WitU32 index, int syscall)
{
    const WitUserThread *thread = &current_user->Threads[index];
    require(frame_inside_kernel_stack(context, sizeof(*context), index) && ((WitU64)context & 15) == 0,
        "User trap outside owning kernel stack");
    if (context->Cs != WIT_USER_CS ||
        context->Ss != WIT_USER_SS ||
        context->Rsp < thread->StackBottom ||
        context->Rsp >= thread->StackTop ||
        !wit_user_space_physical(&current_user->Space, context->Rsp, 1, 0) ||
        !wit_user_space_physical(&current_user->Space, context->Rip, 0, 1)) {
        finish(WitUserBadReturn, 0);
    }
    context->Rflags = syscall ? 0x202 : (context->Rflags & 0x200CD5ULL) | 0x202;
}

WitU64 wit_user_thread_create(WitUserProcess *process, WitU64 entry, WitU64 argument, WitU64 *result)
{
    return wit_user_thread_create_flags(process, entry, argument, 0, result);
}

WitU64 wit_user_thread_create_flags(
    WitUserProcess *process, WitU64 entry, WitU64 argument, WitU64 flags, WitU64 *result)
{
    *result = 0;
    if (flags & ~(WitU64)(WIT_THREAD_DETACHED | WIT_THREAD_LIBRARY_NOTIFICATIONS)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_space_physical(&process->Space, entry, 0, 1)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        WitU64 status;
        if (process->Threads[i].State != WitThreadEmpty) {
            continue;
        }
        status = wit_user_prepare_thread(process, i, entry, argument, flags);
        if (status == WIT_STATUS_OK && !(flags & WIT_THREAD_DETACHED)) {
            *result = process->Threads[i].Handle;
        }
        return status;
    }
    return WIT_STATUS_NO_MEMORY;
}

static void reap(WitU32 index)
{
    WitUserThread *thread = &current_user->Threads[index];
    require(thread->State == WitThreadExited && thread->Joiner == NO_THREAD, "Reaping live/joined thread");
    require(!wit_user_stack_leased(current_user, thread->Handle, 0), "Reaping leased stack");
    for (WitU64 p = thread->StackBottom; p < thread->StackTop; p += 4096) {
        require(wit_user_space_unmap_fixed(&current_user->Space, p), "Thread stack ownership lost");
    }
    require(wit_user_space_unmap_fixed(&current_user->Space, thread->Tls), "Thread TLS ownership lost");
    for (WitU32 n = 0; n < 2; ++n) {
        if (thread->LibraryNotificationHandles[n]) {
            require(wit_handle_close(&current_user->Handles, thread->LibraryNotificationHandles[n]) == WIT_STATUS_OK,
                "Thread notification handle lost");
            thread->LibraryNotificationHandles[n] = 0;
        }
    }
    if (thread->LibraryNotificationPage) {
        require(wit_user_space_unmap_fixed(&current_user->Space, thread->LibraryNotificationPage),
            "Thread notification page lost");
        thread->LibraryNotificationPage = 0;
    }
    wit_user_library_tls_reap_thread(current_user, index);
    if (thread->CompilerTls) {
        require(wit_user_space_unmap_fixed(&current_user->Space, thread->CompilerTls), "Compiler TLS ownership lost");
    }
    thread->CompilerTls = 0;
    require(wit_handle_close(&current_user->Handles, thread->Handle) == WIT_STATUS_OK, "Thread handle lost");
    thread->Handle = 0;
    thread->NativeId = 0;
    thread->SuspendCount = 0;
    wit_user_thread_name_clear(thread);
    if (thread->Detached) {
        ++current_user->DetachedReaps;
    }
    thread->Detached = 0;
    thread->State = WitThreadEmpty;
    ++current_user->ThreadReaps;
}

static WitU32 thread_index(WitU64 handle)
{
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        if (current_user->Threads[i].State != WitThreadEmpty && current_user->Threads[i].Handle == handle) {
            return i;
        }
    }
    wit_panic("Live thread handle without thread");
}

static void expire_waits(void)
{
    wit_user_wait_expire(current_user, wit_arch_clock_ticks());
    wit_user_wait_expire_time(current_user, wit_arch_monotonic_read());
    wit_user_pressure_update(current_user);
}

static WitInterruptContext *dispatch(int timer, WitU64 last_exit)
{
    const WitU32 previous = current_user->CurrentThread;
    for (;;) {
        int waiting = 0;
        expire_waits();
        for (WitU32 offset = 1; offset <= WIT_USER_THREAD_CAPACITY; ++offset) {
            const WitU32 index = (previous + offset) % WIT_USER_THREAD_CAPACITY;
            WitUserThread *thread = &current_user->Threads[index];
            if (thread->State == WitThreadWaiting || thread->SuspendCount) {
                waiting = 1;
            }
            if (thread->State != WitThreadReady || thread->SuspendCount) {
                continue;
            }
            validate_return(thread->Context, index, 0);
            if (index != previous) {
                ++current_user->ThreadSwitches;
                if (timer) {
                    ++current_user->ThreadTimerSwitches;
                }
            }
            current_user->CurrentThread = index;
            thread->State = WitThreadRunning;
            wit_arch_set_kernel_stack(stack_low(current_user, index) + WIT_KERNEL_STACK_SIZE);
            wit_arch_set_user_tls(thread->Tls, thread->CompilerTls);
            return thread->Context;
        }
        if (!waiting) {
            finish(WitUserExited, last_exit);
        }
        /* Remain on this kernel stack until an IRQ returns to the instruction
         * after HLT. The nested IRQ never replaces any saved user context. */
        require((wit_x64_read_flags() & 0x200) == 0, "Idle entered with interrupts enabled");
        __writemsr(FS_BASE, 0);
        __writemsr(GS_BASE, 0);
        user_idle = 1;
        ++current_user->IdleHalts;
        wit_arch_idle_once();
        user_idle = 0;
    }
}

static WitInterruptContext *exit_thread(WitU64 code)
{
    const WitU32 index = current_user->CurrentThread;
    WitUserThread *thread = &current_user->Threads[index];
    wit_user_exception_clear(thread);
    wit_user_stack_leases_exit(current_user, thread->Handle);
    require(!wit_user_stack_leased(current_user, thread->Handle, 0), "Exiting foreign-leased stack");
    wit_user_thread_name_clear(thread);
    thread->State = WitThreadExited;
    thread->ExitCode = code;
    thread->WaitAll = 0;
    thread->WaitAlertable = 0;
    wit_user_apc_initialize(thread);
    wit_user_references_exit(current_user, thread->Handle, code);
    if (thread->Detached) {
        require(thread->Joiner == NO_THREAD, "Detached thread acquired a joiner");
        reap(index);
    } else if (thread->Joiner != NO_THREAD) {
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
    if (context->Rax != WIT_STATUS_OK) {
        return context;
    }
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
            current_user->Threads[walk].WaitKind != WitWaitJoin) {
            break;
        }
        walk = current_user->Threads[walk].WaitingOn;
        require(walk < WIT_USER_THREAD_CAPACITY, "Invalid join edge");
    }
    if (thread->Joiner != NO_THREAD) {
        context->Rax = WIT_STATUS_BUSY;
        return context;
    }
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
    if (wit_handle_check(&current_user->Handles, handle, WIT_HANDLE_LIBRARY, 0) == WIT_STATUS_OK ||
        wit_handle_check(&current_user->Handles, handle, WIT_HANDLE_LIBRARY_READER, 0) == WIT_STATUS_OK ||
        wit_handle_check(&current_user->Handles, handle, WIT_HANDLE_LIBRARY_LIFECYCLE, 0) == WIT_STATUS_OK) {
        return WIT_STATUS_WRONG_TYPE;
    }
    const WitU64 file = wit_file_close(&current_user->Files, &current_user->Handles, handle);
    if (file != WIT_STATUS_WRONG_TYPE) {
        return file;
    }
    const WitU64 reference = wit_user_reference_close(current_user, handle);
    if (reference != WIT_STATUS_WRONG_TYPE) {
        return reference;
    }
    const WitU64 status = wit_handle_check(&current_user->Handles, handle, WIT_HANDLE_THREAD, 0);
    WitU32 index;
    if (status == WIT_STATUS_WRONG_TYPE) {
        const WitU64 event_status = wit_user_event_close(current_user, handle);
        return event_status == WIT_STATUS_WRONG_TYPE ? wit_handle_close(&current_user->Handles, handle) : event_status;
    }
    if (status != WIT_STATUS_OK) {
        return status;
    }
    index = thread_index(handle);
    if (current_user->Threads[index].State != WitThreadExited || current_user->Threads[index].Joiner != NO_THREAD) {
        return WIT_STATUS_BUSY;
    }
    reap(index);
    return WIT_STATUS_OK;
}

static int can_create(const WitUserProcess *process, WitU32 slot)
{
    return process &&
        !current_user &&
        slot < 2 &&
        !slot_owners[slot] &&
        next_id &&
        slot_owners[0] != process &&
        slot_owners[1] != process &&
        (wit_x64_read_flags() & 0x200) == 0;
}

static WitPeStatus create_process(WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot, const WitU8 *code,
    WitU32 code_size, const WitPeImage *image, WitU64 base, const WitU16 *resource, WitU32 resource_length)
{
    WitUserStartup *startup;
    WitU64 physical;
    if (!can_create(process, slot)) {
        return WitPeBusy;
    }
    wit_files_initialize(&process->Files);
    wit_user_library_initialize(process);
    process->Id = next_id++;
    process->Slot = slot;
    process->State = WitUserEmpty;
    process->FatalArmed = 0;
    process->FatalOwner = 0;
    for (WitU32 i = 0; i < sizeof(process->Fatal); ++i) {
        ((WitU8 *)&process->Fatal)[i] = 0;
    }
    process->Writes = 0;
    process->RandomRequests = 0;
    process->RandomBytes = 0;
    process->RequireThreadCompletion = image && (image->Profile & WIT_PE_RUNTIME_FULL);
    process->AbruptThreadId = 0;
    process->AbruptThreadCode = 0;
    process->OrderlyThreadExits = 0;
    process->MemoryCommitFailures = 0;
    process->ForeignObjectWaitSuspends = 0;
    process->ReferenceThreadCapacityFailures = 0;
    process->HardwareNullReads = 0;
    process->HardwareNullWrites = 0;
    process->HardwareDivideFaults = 0;
    process->HardwareIllegalFaults = 0;
    process->ExceptionContinuations = 0;
    process->Ticks = 0;
    process->TickLimit =
        image && (image->Profile & WIT_PE_RUNTIME_FULL) ? WIT_RUNTIME_TICK_BUDGET : WIT_USER_TICK_BUDGET;
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
    process->ProcessWriteBarriers = 0;
    process->ThreadCreates = 0;
    process->ThreadSwitches = 0;
    process->ThreadTimerSwitches = 0;
    process->ThreadJoins = 0;
    process->ThreadReaps = 0;
    process->DetachedCreates = 0;
    process->DetachedReaps = 0;
    process->ThreadDeadlocks = 0;
    process->NextWaitOrder = 0;
    process->MemoryPressureLow = 0;
    for (WitU32 i = 0; i < WIT_RUNTIME_EVENT_CAPACITY; ++i) {
        process->MemoryPressureEvents[i] = 0;
    }
    process->EventParks = 0;
    process->EventWakes = 0;
    process->WaitTimeouts = 0;
    process->WaitCloses = 0;
    process->IdleHalts = 0;
    process->IdleTicks = 0;
    wit_events_initialize(&process->Events);
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        process->Threads[i].State = WitThreadEmpty;
        process->Threads[i].SuspendCount = 0;
        wit_user_thread_name_clear(&process->Threads[i]);
    }
    wit_handles_initialize(&process->Handles, process->Id);
    if (image && (image->Profile & WIT_PE_RUNTIME_FULL)) {
        process->Handles.Limit = WIT_RUNTIME_HANDLE_CAPACITY;
        process->Events.Limit = WIT_RUNTIME_EVENT_CAPACITY;
    }
    wit_user_references_initialize(process);
    wit_user_stack_leases_initialize(process);
    wit_user_exception_initialize(process);
    slot_owners[slot] = process;
    if (!wit_user_space_create_profile(&process->Space, allocator, image && (image->Profile & WIT_PE_RUNTIME_FULL))) {
        goto failed;
    }
    if (image) {
        if (!wit_user_image_map(&process->Space, code, image, base)) {
            goto failed;
        }
    } else if (!wit_user_space_map(&process->Space, WIT_USER_CODE, 0, 1)) {
        goto failed;
    }
    if (!wit_user_capture_tls(process, image)) {
        goto failed;
    }
    if (!wit_user_space_map(&process->Space, WIT_USER_INFO, 0, 0)) {
        goto failed;
    }
    for (WitU64 page = WIT_USER_DATA; page < WIT_USER_DATA_END; page += 4096) {
        if (!wit_user_space_map(&process->Space, page, 1, 0)) {
            goto failed;
        }
    }
    if (!image) {
        physical = wit_user_space_physical(&process->Space, WIT_USER_CODE, 0, 1);
        for (WitU32 i = 0; i < code_size; ++i) {
            ((WitU8 *)physical)[i] = code[i];
        }
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
        info->ResourceNameLength = resource_length;
        for (WitU32 i = 0; i < resource_length; ++i) {
            info->ResourceName[i] = resource[i];
        }
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
    if (!startup->ConsoleHandle ||
        wit_user_prepare_thread(process, 0, process->ImageEntry, WIT_USER_INFO, 0) != WIT_STATUS_OK) {
        goto failed;
    }
    process->State = WitUserReady;
    return WitPeOk;
failed:
    wit_user_destroy(process);
    return WitPeNoMemory;
}

int wit_user_create(
    WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot, const WitU8 *code, WitU32 code_size)
{
    if (!code || !code_size || code_size > 4096) {
        return 0;
    }
    return create_process(process, allocator, slot, code, code_size, 0, 0, 0, 0) == WitPeOk;
}

WitPeStatus wit_user_create_pe(
    WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot, const WitU8 *file, WitU32 size, WitU64 base)
{
    return wit_user_create_named_pe(process, allocator, slot, file, size, base, 0);
}

WitPeStatus wit_user_create_pe_profile(WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot,
    const WitU8 *file, WitU32 size, WitU64 base, const char *resource_name, WitU32 profile)
{
    if (profile & WIT_PE_LIBRARY) {
        return WitPeUnsupportedImage; // Libraries never enter the component-main path.
    }
    WitPeImage image;
    WitPeStatus status;
    WitU16 resource[WIT_IMAGE_RESOURCE_CAPACITY] = {0};
    WitU32 resource_length = 0;
    /* Trusted kernel resource label is bounded and copied before allocation.
     * No user-controlled path or pointer crosses this private loader interface. */
    if (resource_name) {
        while (resource_length < WIT_IMAGE_RESOURCE_CAPACITY && resource_name[resource_length]) {
            resource[resource_length] = (WitU8)resource_name[resource_length];
            ++resource_length;
        }
        if (!resource_length || !wit_image_resource_valid(resource, resource_length)) {
            return WitPeInvalidImage;
        }
    }
    if (!can_create(process, slot)) {
        return WitPeBusy;
    }
    status = wit_pe_validate_profile(file, size, &image, profile);
    if (status != WitPeOk) {
        return status;
    }
    const WitU64 limit = (profile & WIT_PE_RUNTIME_FULL) ? WIT_RUNTIME_USER_LIMIT : WIT_USER_LIMIT;
    if ((base & 65535) || base < WIT_USER_IMAGE_BASE || base >= limit || image.ImageSize > limit - base) {
        return WitPeBadBase;
    }
    if (base != image.PreferredBase && !image.RelocSize) {
        return WitPeUnsupportedImage;
    }
    return create_process(process, allocator, slot, file, size, &image, base, resource, resource_length);
}

void wit_user_run(WitUserProcess *process)
{
    require(!current_user &&
            !user_idle &&
            process->State == WitUserReady &&
            slot_owners[process->Slot] == process &&
            (wit_x64_read_flags() & 0x200) == 0 &&
            __readmsr(FS_BASE) == 0 &&
            __readmsr(GS_BASE) == 0,
        "Invalid user launch");
    current_user = process;
    process->State = WitUserRunning;
    process->Threads[0].State = WitThreadRunning;
    process->Ticks = 0;
    wit_arch_set_kernel_stack(stack_low(process, 0) + WIT_KERNEL_STACK_SIZE);
    wit_arch_timer_start();
    wit_arch_set_user_tls(process->Threads[0].Tls, process->Threads[0].CompilerTls);
    wit_arch_run_user(process->Threads[0].Context, process->Space.Root);
    require(!current_user &&
            process->State != WitUserRunning &&
            (__readcr3() & 0x000FFFFFFFFFF000ULL) == wit_virtual_kernel_root() &&
            (wit_x64_read_flags() & 0x200) == 0 &&
            __readmsr(FS_BASE) == 0 &&
            __readmsr(GS_BASE) == 0,
        "User return did not restore kernel state");
    wit_arch_set_kernel_stack((WitU64)wit_x64_kernel_stack + 4096 + WIT_KERNEL_STACK_SIZE);
}

void wit_user_destroy(WitUserProcess *process)
{
    require(current_user != process && process->State != WitUserRunning, "Destroying running component");
    wit_handles_close_all(&process->Handles);
    wit_files_initialize(&process->Files);
    wit_user_library_initialize(process);
    wit_user_references_initialize(process);
    wit_user_stack_leases_initialize(process);
    wit_user_exception_initialize(process);
    wit_events_initialize(&process->Events);
    wit_user_space_destroy(&process->Space);
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        process->Threads[i].State = WitThreadEmpty;
        process->Threads[i].SuspendCount = 0;
        wit_user_thread_name_clear(&process->Threads[i]);
    }
    if (process->Slot < 2 && slot_owners[process->Slot] == process) {
        slot_owners[process->Slot] = 0;
    }
    process->State = WitUserEmpty;
    process->ImageBase = 0;
    process->ImageEntry = 0;
    process->ImageSize = 0;
    process->TlsBytes = 0;
}

int wit_user_is_active(void)
{
    return current_user != 0;
}

WitInterruptContext *wit_user_timer_tick(WitInterruptContext *context)
{
    require(current_user != 0, "User timer without component");
    if (user_idle) {
        const WitU64 low = stack_low(current_user, current_user->CurrentThread);
        require(frame_inside_kernel_stack(context, sizeof(*context), current_user->CurrentThread) &&
                ((WitU64)context & 15) == 0 &&
                context->Cs == 8 &&
                (context->Ss == 0 || context->Ss == 0x10) &&
                context->Rsp >= low &&
                context->Rsp < low + WIT_KERNEL_STACK_SIZE &&
                context->Rip == (WitU64)wit_x64_idle_resume &&
                __readmsr(FS_BASE) == 0 &&
                __readmsr(GS_BASE) == 0,
            "Timer did not interrupt the kernel idle path");
        ++current_user->IdleTicks;
    } else {
        WitUserThread *thread = &current_user->Threads[current_user->CurrentThread];
        validate_return(context, current_user->CurrentThread, 0);
        thread->Context = context;
        thread->State = WitThreadReady;
    }
    expire_waits();
    if (++current_user->Ticks >= current_user->TickLimit) {
        finish(WitUserBudgetExpired, 0);
    }
    if (user_idle) {
        return context; /* Resume CLI/RET and recheck ready threads. */
    }
    return dispatch(1, 0);
}

WitInterruptContext *wit_x64_user_syscall(WitInterruptContext *context)
{
    WitU8 buffer[WIT_ABI_MAX_WRITE];
    WitU64 status;
    WitU64 call, argument0, argument1, argument2;
    require(current_user != 0 && current_user->State == WitUserRunning, "Syscall without component");
    validate_return(context, current_user->CurrentThread, 1);
    expire_waits();
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
    case WIT_CALL_THREAD_SUSPEND:
    case WIT_CALL_THREAD_RESUME:
        context->Rax = (argument1 || argument2)
            ? WIT_STATUS_INVALID_ARGUMENT
            : wit_user_thread_suspend(current_user, argument0, call == WIT_CALL_THREAD_RESUME, &context->Rdx);
        break;
    case WIT_CALL_FATAL_ARM:
        if (argument0 > 0xFFFFFFFFULL || argument1 || argument2) {
            context->Rax = WIT_STATUS_INVALID_ARGUMENT;
            break;
        }
        if (current_user->FatalArmed) {
            context->Rax = WIT_STATUS_BUSY;
            break;
        }
        current_user->Fatal = (WitUserFatalInfo){0};
        current_user->Fatal.Version = WIT_FATAL_INFO_VERSION;
        current_user->Fatal.Size = sizeof(current_user->Fatal);
        current_user->Fatal.Code = (WitU32)argument0;
        current_user->FatalOwner = current_user->Threads[current_user->CurrentThread].Handle;
        current_user->FatalArmed = 1;
        break;
    case WIT_CALL_FATAL_REPORT: {
        if (!current_user->FatalArmed ||
            current_user->FatalOwner != current_user->Threads[current_user->CurrentThread].Handle) {
            context->Rax = WIT_STATUS_DENIED;
            break;
        }
        if (argument1 != sizeof(WitUserFatalInfo) || argument2 != WIT_FATAL_INFO_VERSION) {
            context->Rax = WIT_STATUS_INVALID_ARGUMENT;
            break;
        }
        WitUserFatalInfo info;
        if (!wit_user_copy_from(&current_user->Space, argument0, (WitU8 *)&info, sizeof(info))) {
            context->Rax = WIT_STATUS_BAD_ADDRESS;
            break;
        }
        if (info.Version != WIT_FATAL_INFO_VERSION ||
            info.Size != sizeof(info) ||
            info.ParameterCount > WIT_FATAL_PARAMETER_CAPACITY) {
            context->Rax = WIT_STATUS_INVALID_ARGUMENT;
            break;
        }
        current_user->Fatal = info;
        break;
    }
    case WIT_CALL_EXCEPTION_BEGIN:
        context->Rax = wit_user_exception_begin(current_user, argument0, argument1, argument2, &context->Rdx);
        break;
    case WIT_CALL_EXCEPTION_REGISTER:
        context->Rax = wit_user_exception_register(current_user, argument0, argument1, argument2);
        break;
    case WIT_CALL_EXCEPTION_QUERY:
        context->Rax = wit_user_exception_query(current_user, argument0, argument1, argument2);
        break;
    case WIT_CALL_EXCEPTION_CONTINUE:
    case WIT_CALL_EXCEPTION_UNWIND:
        status = call == WIT_CALL_EXCEPTION_CONTINUE
            ? wit_user_exception_continue(current_user, argument0, argument1, argument2)
            : wit_user_exception_unwind(current_user, argument0, argument1, argument2);
        if (status == WIT_STATUS_OK) {
            wit_arch_set_user_tls(current_user->Threads[current_user->CurrentThread].Tls,
                current_user->Threads[current_user->CurrentThread].CompilerTls);
            return context;
        }
        context->Rax = status;
        break;
    case WIT_CALL_EXCEPTION_REJECT:
        if (argument1 || argument2) {
            context->Rax = WIT_STATUS_INVALID_ARGUMENT;
            break;
        }
        if (!argument0 || current_user->Threads[current_user->CurrentThread].Exception.Token != argument0) {
            context->Rax = WIT_STATUS_BAD_HANDLE;
            break;
        }
        (void)wit_x64_user_exception(context, ~0ULL, 0, 0); // Pending original fault is retained by the fatal path.
        wit_panic("Rejected exception unexpectedly resumed");
    case WIT_CALL_STACK_LEASE_ACQUIRE:
        context->Rax = wit_user_stack_lease_acquire(current_user, argument0, argument1, argument2);
        break;
    case WIT_CALL_STACK_LEASE_QUERY:
        context->Rax = wit_user_stack_lease_query(current_user, argument0, argument1, argument2);
        break;
    case WIT_CALL_STACK_LEASE_RELEASE:
        context->Rax = (argument1 || argument2) ? WIT_STATUS_INVALID_ARGUMENT
                                                : wit_user_stack_lease_release(current_user, argument0);
        break;
    case WIT_CALL_THREAD_CONTEXT_METADATA:
        context->Rax = wit_user_thread_context_metadata(current_user, argument0, argument1, argument2);
        break;
    case WIT_CALL_THREAD_CONTEXT_SET:
        context->Rax = wit_user_thread_context_set(current_user, argument0, argument1, argument2);
        break;
    case WIT_CALL_THREAD_CONTEXT_RESTORE:
        status = wit_user_thread_context_restore(current_user, argument0, argument1, argument2);
        if (status == WIT_STATUS_OK) {
            wit_arch_set_user_tls(current_user->Threads[current_user->CurrentThread].Tls,
                current_user->Threads[current_user->CurrentThread].CompilerTls);
            return context; // Preserve restored RAX/RDX and flags; no syscall-result overwrite.
        }
        context->Rax = status;
        break;
    case WIT_CALL_THREAD_CONTEXT_GET:
        context->Rax = wit_user_thread_context_get(current_user, argument0, argument1, argument2);
        break;
    case WIT_CALL_CPU_CONTEXT_QUERY:
        context->Rax = wit_user_cpu_context_query(current_user, argument0, argument1, argument2);
        break;
    case WIT_CALL_THREAD_NAME_SET:
        context->Rax = wit_user_thread_name_set(current_user, argument0, argument1, argument2);
        break;
    case WIT_CALL_THREAD_NAME_QUERY:
        context->Rax = wit_user_thread_name_query(current_user, argument0, argument1, argument2);
        break;
    case WIT_CALL_CONSOLE_WRITE:
        context->Rax = wit_user_console_write(current_user, argument0, argument1, argument2);
        break;
    case WIT_CALL_PROCESSOR_QUERY: {
        const WitU32 processor = 0; // Sole online BSP: group 0, number 0, reserved 0.
        if (argument1 != sizeof(processor) || argument2) {
            context->Rax = WIT_STATUS_INVALID_ARGUMENT;
            break;
        }
        if (WIT_USER_PROCESSOR_COUNT != 1) {
            context->Rax = WIT_STATUS_UNSUPPORTED;
            break;
        }
        if (!wit_user_copy_to(&current_user->Space, argument0, (const WitU8 *)&processor, sizeof(processor))) {
            context->Rax = WIT_STATUS_BAD_ADDRESS;
        } else {
            context->Rdx = sizeof(processor);
        }
        break;
    }
    case WIT_CALL_WRITE:
        status = wit_handle_check(&current_user->Handles, argument0, WIT_HANDLE_CONSOLE, WIT_RIGHT_WRITE);
        if (status != WIT_STATUS_OK) {
            context->Rax = status;
            break;
        }
        if (argument2 > WIT_ABI_MAX_WRITE) {
            context->Rax = WIT_STATUS_TOO_LARGE;
            break;
        }
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
    case WIT_CALL_LIBRARY:
        context->Rax = wit_user_library_call(current_user, argument0, argument1, argument2, &context->Rdx);
        break;
    case WIT_CALL_STORAGE_QUERY:
        context->Rax = wit_user_storage_query(current_user, argument0, argument1, argument2, &context->Rdx);
        break;
    case WIT_CALL_FILE:
        context->Rax = wit_user_file_call(current_user, argument0, argument1, argument2, &context->Rdx);
        break;
    case WIT_CALL_CODE_MEMORY:
        context->Rax = wit_user_code_call(current_user, argument0, argument1, argument2, &context->Rdx);
        break;
    case WIT_CALL_MEMORY_RESERVE:
        context->Rax = wit_user_memory_reserve(&current_user->Space, argument0, argument1, &context->Rdx);
        break;
    case WIT_CALL_MEMORY_COMMIT: {
        const WitU32 owned = current_user->Space.OwnedCount;
        const WitU64 free = wit_pages_free_count(current_user->Space.Allocator);
        context->Rax = wit_user_memory_commit(&current_user->Space, argument0, argument1, argument2);
        if (context->Rax == WIT_STATUS_NO_MEMORY) {
            ++current_user->MemoryCommitFailures;
            require(
                current_user->Space.OwnedCount == owned && wit_pages_free_count(current_user->Space.Allocator) == free,
                "Failed user commit changed settled ownership/accounting");
        }
        break;
    }
    case WIT_CALL_MEMORY_RESET:
        context->Rax =
            argument2 ? WIT_STATUS_INVALID_ARGUMENT : wit_user_memory_reset(&current_user->Space, argument0, argument1);
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
        if (context->Rax == WIT_STATUS_OK) {
            context->Rdx = WIT_MEMORY_INFO_SIZE;
        }
        break;
    case WIT_CALL_RANDOM: {
        WitU8 random[WIT_RANDOM_BLOCK_BYTES];
        if (argument2) {
            context->Rax = WIT_STATUS_INVALID_ARGUMENT;
            break;
        }
        if (argument1 > WIT_ABI_MAX_RANDOM) {
            context->Rax = WIT_STATUS_TOO_LARGE;
            break;
        }
        if (!wit_user_buffer_writable(&current_user->Space, argument0, (WitU32)argument1)) {
            context->Rax = WIT_STATUS_BAD_ADDRESS;
            break;
        }
        for (WitU32 offset = 0; offset < (WitU32)argument1;) {
            WitU32 count = (WitU32)argument1 - offset;
            if (count > sizeof(random)) {
                count = sizeof(random);
            }
            if (!wit_random_fill(random, count) ||
                !wit_user_copy_to(&current_user->Space, argument0 + offset, random, count)) {
                wit_panic("Random copy invariant failed");
            }
            for (WitU32 i = 0; i < sizeof(random); ++i) {
                ((volatile WitU8 *)random)[i] = 0;
            }
            offset += count;
        }
        if (argument1) {
            ++current_user->RandomRequests;
            current_user->RandomBytes += argument1;
        }
        context->Rdx = argument1;
        break;
    }
    case WIT_CALL_MONOTONIC_QUERY: {
        if (argument1 != sizeof(WitU64)) {
            context->Rax = WIT_STATUS_INVALID_ARGUMENT;
            break;
        }
        if (argument2 > WIT_MONOTONIC_HZ) {
            context->Rax = WIT_STATUS_UNSUPPORTED;
            break;
        }
        const WitU64 value =
            argument2 == WIT_MONOTONIC_COUNTER ? wit_arch_monotonic_read() : wit_arch_monotonic_frequency();
        // The dispatcher keeps IF clear through sampling and whole-buffer copy.
        if (!wit_user_copy_to(&current_user->Space, argument0, (const WitU8 *)&value, sizeof(value))) {
            context->Rax = WIT_STATUS_BAD_ADDRESS;
        } else {
            context->Rdx = sizeof(value);
        }
        break;
    }
    case WIT_CALL_MONOTONIC_READ:
        context->Rdx = wit_arch_monotonic_read();
        break;
    case WIT_CALL_MONOTONIC_FREQUENCY:
        context->Rdx = wit_arch_monotonic_frequency();
        break;
    case WIT_CALL_SLEEP_UNTIL:
        context->Rax = (argument1 || argument2)
            ? WIT_STATUS_INVALID_ARGUMENT
            : wit_user_sleep_until(current_user, argument0, wit_arch_monotonic_read());
        break;
    case WIT_CALL_EVENT_WAIT_UNTIL:
        context->Rax = argument2
            ? WIT_STATUS_INVALID_ARGUMENT
            : wit_user_event_wait_until(current_user, argument0, argument1, wit_arch_monotonic_read());
        break;
    case WIT_CALL_CLOCK_READ:
        context->Rdx = wit_arch_clock_ticks();
        break;
    case WIT_CALL_CLOCK_FREQUENCY:
        context->Rdx = WIT_CLOCK_FREQUENCY;
        break;
    case WIT_CALL_THREAD_SLEEP:
        context->Rax = wit_user_sleep(current_user, argument0, wit_arch_clock_ticks());
        break;
    case WIT_CALL_OBJECT_WAIT:
        context->Rax = wit_user_object_wait(
            current_user, argument0, argument1, argument2, wit_arch_monotonic_read(), &context->Rdx);
        break;
    case WIT_CALL_APC_QUEUE:
        context->Rax = wit_user_apc_queue(current_user, argument0, argument1, argument2);
        break;
    case WIT_CALL_APC_DEQUEUE:
        context->Rax =
            argument2 ? WIT_STATUS_INVALID_ARGUMENT : wit_user_apc_dequeue(current_user, argument0, argument1);
        if (context->Rax == WIT_STATUS_OK) {
            context->Rdx = sizeof(WitUserApc);
        }
        break;
    case WIT_CALL_EVENT_CREATE_RIGHTS:
        context->Rax = (argument2 || argument1 > 0xFFFFFFFFULL)
            ? WIT_STATUS_INVALID_ARGUMENT
            : wit_event_create(
                  &current_user->Events, &current_user->Handles, argument0, (WitU32)argument1, &context->Rdx);
        break;
    case WIT_CALL_EVENT_CREATE:
        context->Rax = wit_event_create(
            &current_user->Events, &current_user->Handles, argument0, WIT_RIGHT_WAIT | WIT_RIGHT_SIGNAL, &context->Rdx);
        if (context->Rax != WIT_STATUS_OK && current_user->Space.PageLimit > WIT_USER_PAGE_CAPACITY) {
            wit_console_write("[RUNTIME-RESOURCE] event failure status/events/handles: ");
            wit_console_write_u64(context->Rax);
            wit_console_write("/");
            wit_console_write_u64(current_user->Events.Count);
            wit_console_write("/");
            wit_console_write_u64(current_user->Handles.Count);
            wit_console_write("\n");
        }
        break;
    case WIT_CALL_EVENT_SET:
        context->Rax = wit_user_event_set(current_user, argument0);
        break;
    case WIT_CALL_EVENT_RESET:
        context->Rax = wit_user_event_reset(current_user, argument0);
        break;
    case WIT_CALL_EVENT_WAIT:
        context->Rax = wit_user_event_wait(current_user, argument0, argument1, wit_arch_clock_ticks());
        break;
    case WIT_CALL_MEMORY_PRESSURE_EVENT:
        context->Rax = (argument0 || argument1 || argument2) ? WIT_STATUS_INVALID_ARGUMENT
                                                             : wit_user_pressure_create(current_user, &context->Rdx);
        break;
    case WIT_CALL_EVENT_WAIT_ANY_UNTIL:
        context->Rax = wit_user_event_wait_any_until(
            current_user, argument0, argument1, argument2, wit_arch_monotonic_read(), &context->Rdx);
        break;
    case WIT_CALL_CPU_CACHE_SIZE:
        if (argument0 || argument1 || argument2) {
            context->Rax = WIT_STATUS_INVALID_ARGUMENT;
        } else if (WIT_USER_PROCESSOR_COUNT != 1) {
            context->Rax = WIT_STATUS_UNSUPPORTED;
        } else {
            context->Rdx = wit_arch_cache_size();
            if (!context->Rdx) {
                context->Rax = WIT_STATUS_UNSUPPORTED;
            }
        }
        break;
    case WIT_CALL_PROCESS_WRITE_BARRIER:
        if (argument0 || argument1 || argument2) {
            context->Rax = WIT_STATUS_INVALID_ARGUMENT;
        } else if (WIT_USER_PROCESSOR_COUNT != 1) {
            context->Rax = WIT_STATUS_UNSUPPORTED;
        } else {
            wit_arch_process_write_barrier();
            ++current_user->ProcessWriteBarriers;
        }
        break;
    case WIT_CALL_THREAD_REFERENCE_DUPLICATE:
        context->Rax = wit_user_reference_duplicate(current_user, argument0, argument1, argument2);
        if (context->Rax == WIT_STATUS_OK) {
            context->Rdx = sizeof(WitU64);
        }
        break;
    case WIT_CALL_THREAD_REFERENCE_QUERY:
        context->Rax = wit_user_reference_query(current_user, argument0, argument1, argument2);
        if (context->Rax == WIT_STATUS_OK) {
            context->Rdx = sizeof(WitThreadReferenceInfo);
        }
        break;
    case WIT_CALL_THREAD_QUERY:
        context->Rax = wit_user_thread_query(current_user, argument0, argument1, argument2);
        if (context->Rax == WIT_STATUS_OK) {
            context->Rdx = WIT_THREAD_INFO_SIZE;
        }
        break;
    case WIT_CALL_THREAD_NATIVE_ID:
        if (argument0 || argument1 || argument2) {
            context->Rax = WIT_STATUS_INVALID_ARGUMENT;
        } else {
            require(
                current_user->Threads[current_user->CurrentThread].NativeId != 0, "Current thread has no native ID");
            context->Rdx = current_user->Threads[current_user->CurrentThread].NativeId;
        }
        break;
    case WIT_CALL_THREAD_CURRENT:
        if (argument0 || argument1 || argument2) {
            context->Rax = WIT_STATUS_INVALID_ARGUMENT;
        } else {
            context->Rdx = current_user->Threads[current_user->CurrentThread].Handle;
        }
        break;
    case WIT_CALL_THREAD_CREATE_REFERENCE:
        if (argument2) {
            context->Rax = WIT_STATUS_INVALID_ARGUMENT;
        } else {
            context->Rax = wit_user_thread_create_reference(current_user, argument0, argument1, &context->Rdx);
        }
        break;
    case WIT_CALL_THREAD_CREATE:
        context->Rax = wit_user_thread_create_flags(current_user, argument0, argument1, argument2, &context->Rdx);
        break;
    case WIT_CALL_THREAD_YIELD: {
        WitInterruptContext *next;
        current_user->Threads[current_user->CurrentThread].State = WitThreadReady;
        next = dispatch(0, 0);
        context->Rdx = next != context ? 1 : 0;
        return next;
    }
    case WIT_CALL_THREAD_EXIT:
        if (current_user->RequireThreadCompletion ||
            current_user->Threads[current_user->CurrentThread].LibraryRequired ||
            (current_user->LibraryLifecycle.Token &&
                current_user->LibraryLifecycle.Owner == current_user->Threads[current_user->CurrentThread].Handle)) {
            // Never free one coordinated worker's TLS/stack and resume peers
            // whose user-space runtime may still hold its record or GC roots.
            current_user->AbruptThreadId = current_user->Threads[current_user->CurrentThread].Handle;
            current_user->AbruptThreadCode = argument0;
            finish(WitUserExited, WIT_PROCESS_ABRUPT_THREAD_EXIT);
        }
        return exit_thread(argument0);
    case WIT_CALL_THREAD_COMPLETE:
        if (argument1 || argument2) {
            context->Rax = WIT_STATUS_INVALID_ARGUMENT;
            break;
        }
        if ((current_user->Threads[current_user->CurrentThread].LibraryRequired &&
                current_user->Threads[current_user->CurrentThread].LibraryPhase != 4) ||
            (current_user->LibraryLifecycle.Token &&
                current_user->LibraryLifecycle.Owner == current_user->Threads[current_user->CurrentThread].Handle)) {
            current_user->AbruptThreadId = current_user->Threads[current_user->CurrentThread].Handle;
            current_user->AbruptThreadCode = argument0;
            finish(WitUserExited, WIT_PROCESS_ABRUPT_THREAD_EXIT);
        }
        ++current_user->OrderlyThreadExits;
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
    // Publish settled memory pressure, after expiring both deadline domains.
    // A finite wait can complete here before this syscall returns; dispatch its
    // Ready state normally instead of returning with an inconsistent state.
    expire_waits();
    if (current_user->Threads[current_user->CurrentThread].SuspendCount &&
        current_user->Threads[current_user->CurrentThread].State == WitThreadRunning) {
        current_user->Threads[current_user->CurrentThread].State = WitThreadReady;
    }
    if (current_user->Threads[current_user->CurrentThread].State != WitThreadRunning) {
        return dispatch(0, 0);
    }
    /* Join may have switched current thread; nonblocking calls retain the caller. */
    wit_arch_set_user_tls(current_user->Threads[current_user->CurrentThread].Tls,
        current_user->Threads[current_user->CurrentThread].CompilerTls);
    return context;
}

WitInterruptContext *wit_x64_user_exception(WitInterruptContext *context, WitU64 vector, WitU64 error, WitU64 address)
{
    require(current_user &&
            current_user->State == WitUserRunning &&
            frame_inside_kernel_stack(context, sizeof(*context), current_user->CurrentThread) &&
            !((WitU64)context & 15) &&
            (context->Cs & 3) == 3,
        "Invalid resumable user fault context");
    WitUserThread *thread = &current_user->Threads[current_user->CurrentThread];
    thread->Context = context;
    if (current_user->FatalArmed) {
        finish(WitUserExited, current_user->Fatal.Code);
    }
    if (wit_user_exception_deliver(current_user, context, vector, error, address)) {
        validate_return(context, current_user->CurrentThread, 0);
        wit_arch_set_user_tls(thread->Tls, thread->CompilerTls);
        return context;
    }
    if (thread->Exception.Token && thread->Exception.Vector == WIT_EXCEPTION_SOFTWARE_VECTOR) {
        wit_console_write("[USER-SOFTWARE-FAIL] code=");
        wit_console_write_hex(thread->Exception.Error);
        wit_console_write(" rip=");
        wit_console_write_hex(thread->Exception.Context.Rip);
        wit_console_write(" nested-vector=");
        wit_console_write_u64(vector);
        wit_console_write(" address=");
        wit_console_write_hex(address);
        wit_console_write("\n");
        finish(WitUserExited, WIT_EXCEPTION_SOFTWARE_FAILURE_EXIT);
    }
    WitExceptionFrame fault = {vector, error, context->Rip, context->Cs, context->Rflags, context->Rsp, context->Ss};
    if (thread->Exception.Token) {
        const WitUserExceptionInfo *original = &thread->Exception;
        fault = (WitExceptionFrame){original->Vector, original->Error, original->Context.Rip, original->Context.Cs,
            original->RawRflags, original->Context.Rsp, original->Context.Ss};
        address = original->Address;
    }
    wit_user_fault(&fault, address);
}

WIT_NORETURN void wit_user_fault(const WitExceptionFrame *frame, WitU64 address)
{
    require(current_user != 0 &&
            frame_inside_kernel_stack(frame, sizeof(*frame), current_user->CurrentThread) &&
            (frame->Cs & 3) == 3,
        "Invalid user fault frame");
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

WitPeStatus wit_user_create_named_pe(WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot,
    const WitU8 *file, WitU32 size, WitU64 base, const char *resource_name)
{
    return wit_user_create_pe_profile(process, allocator, slot, file, size, base, resource_name, 0);
}
