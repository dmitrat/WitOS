#include "user.h"
#include "witos/platform.h"

#define NO_THREAD WIT_USER_THREAD_CAPACITY

static WitUserProcess *current_user;
static WitUserProcess *slot_owners[2];
static WitU32 next_id = 1;
static volatile WitU32 user_idle;
static WitU64 contained_faults;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

#if defined(WITOS_TEST_RUNTIME_BOOT)
/* Diagnostics of a runtime component that exhausted its tick budget: every live thread and its stack. */
static void report_budget(WitUserState state)
{
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
            const WitArchFrame *c = t->Context;
            if (c) {
                wit_console_write("Runtime budget ");
                wit_arch_frame_describe(c);
                WitU64 stack[16] = {0};
                const WitU64 sp = wit_arch_frame_sp(c);
                if (sp >= t->StackBottom &&
                    sp <= t->StackTop - sizeof(stack) &&
                    wit_user_copy_from(&current_user->Space, sp, (WitU8 *)stack, sizeof(stack))) {
                    wit_console_write("Runtime budget stack: ");
                    for (WitU32 j = 0; j < 16; ++j) {
                        wit_console_write_hex(stack[j]);
                        wit_console_write(j == 15 ? "\n" : "/");
                    }
                }
            }
        }
    }
}
#endif

WIT_NORETURN void wit_user_finish(WitUserState state, WitU64 code)
{
    require(current_user != 0 && current_user->State == WitUserRunning, "No current user component");
    wit_platform_timer_stop();
#if defined(WITOS_TEST_RUNTIME_BOOT)
    report_budget(state);
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
    wit_arch_reset_user_tls();
    current_user = 0;
    wit_arch_leave_user();
}

static void validate_return(WitArchFrame *frame, WitU32 index, int syscall)
{
    const WitUserThread *thread = &current_user->Threads[index];
    require(wit_arch_frame_owned(frame, current_user->Slot, index), "User trap outside owning kernel stack");
    const WitU64 sp = wit_arch_frame_sp(frame);
    if (!wit_arch_frame_returns_to_user(frame) ||
        sp < thread->StackBottom ||
        sp >= thread->StackTop ||
        !wit_user_space_physical(&current_user->Space, sp, 1, 0) ||
        !wit_user_space_physical(&current_user->Space, wit_arch_frame_pc(frame), 0, 1)) {
        wit_user_finish(WitUserBadReturn, 0);
    }
    wit_arch_frame_prepare_return(frame, syscall);
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
    wit_user_wait_expire_time(current_user, wit_platform_monotonic_read());
    wit_user_pressure_update(current_user);
}

static WitArchFrame *dispatch(int timer, WitU64 last_exit)
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
            wit_arch_select_thread_stack(current_user->Slot, index);
            wit_arch_set_user_tls(thread->Tls, thread->CompilerTls);
            return thread->Context;
        }
        if (!waiting) {
            wit_user_finish(WitUserExited, last_exit);
        }
        /* Remain on this kernel stack until an IRQ returns to the instruction
         * after HLT. The nested IRQ never replaces any saved user context. */
        require(!wit_arch_interrupts_enabled(), "Idle entered with interrupts enabled");
        wit_arch_reset_user_tls();
        user_idle = 1;
        ++current_user->IdleHalts;
        wit_arch_idle_once();
        user_idle = 0;
    }
}

WitArchFrame *wit_user_exit_thread(WitU64 code)
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
        wit_arch_frame_set_result(waiter->Context, WIT_STATUS_OK, code);
        waiter->WaitingOn = NO_THREAD;
        waiter->WaitKind = WitWaitNone;
        waiter->State = WitThreadReady;
        thread->Joiner = NO_THREAD;
        ++current_user->ThreadJoins;
        reap(index);
    }
    return dispatch(0, code);
}

WitArchFrame *wit_user_join_thread(WitArchFrame *frame, WitU64 handle)
{
    WitU32 target, walk;
    WitUserThread *thread, *caller;
    WitU64 *status = wit_arch_frame_status(frame);
    *status = wit_handle_check(&current_user->Handles, handle, WIT_HANDLE_THREAD, WIT_RIGHT_JOIN);
    if (*status != WIT_STATUS_OK) {
        return frame;
    }
    target = thread_index(handle);
    thread = &current_user->Threads[target];
    walk = target;
    /* Edges and wakeup publication are serialized under the interrupt gate.
     * Reject self-join and any cycle before installing a wait edge. */
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        if (walk == current_user->CurrentThread) {
            ++current_user->ThreadDeadlocks;
            *status = WIT_STATUS_DEADLOCK;
            return frame;
        }
        if (current_user->Threads[walk].State != WitThreadWaiting ||
            current_user->Threads[walk].WaitKind != WitWaitJoin) {
            break;
        }
        walk = current_user->Threads[walk].WaitingOn;
        require(walk < WIT_USER_THREAD_CAPACITY, "Invalid join edge");
    }
    if (thread->Joiner != NO_THREAD) {
        *status = WIT_STATUS_BUSY;
        return frame;
    }
    if (thread->State == WitThreadExited) {
        *wit_arch_frame_value(frame) = thread->ExitCode;
        ++current_user->ThreadJoins;
        reap(target);
        return frame;
    }
    caller = &current_user->Threads[current_user->CurrentThread];
    caller->State = WitThreadWaiting;
    caller->WaitingOn = target;
    caller->WaitKind = WitWaitJoin;
    caller->Deadline = WIT_WAIT_INFINITE;
    thread->Joiner = current_user->CurrentThread;
    return dispatch(0, 0);
}

WitU64 wit_user_close_handle(WitU64 handle)
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
        !wit_arch_interrupts_enabled();
}

/* Clears the statistics of a fresh component. */
static void reset_counters(WitUserProcess *process)
{
    process->Writes = 0;
    process->RandomRequests = 0;
    process->RandomBytes = 0;
    process->OrderlyThreadExits = 0;
    process->MemoryCommitFailures = 0;
    process->ForeignObjectWaitSuspends = 0;
    process->ReferenceThreadCapacityFailures = 0;
    process->HardwareNullReads = 0;
    process->HardwareNullWrites = 0;
    process->HardwareDivideFaults = 0;
    process->HardwareIllegalFaults = 0;
    process->ExceptionContinuations = 0;
    process->ProcessWriteBarriers = 0;
    process->ThreadCreates = 0;
    process->ThreadSwitches = 0;
    process->ThreadTimerSwitches = 0;
    process->ThreadJoins = 0;
    process->ThreadReaps = 0;
    process->DetachedCreates = 0;
    process->DetachedReaps = 0;
    process->ThreadDeadlocks = 0;
    process->EventParks = 0;
    process->EventWakes = 0;
    process->WaitTimeouts = 0;
    process->WaitCloses = 0;
    process->IdleHalts = 0;
    process->IdleTicks = 0;
}

/* Gives a fresh component its identity, profile, image placement and empty threads, objects and handles. */
static void reset_process(WitUserProcess *process, WitU32 slot, WitU32 code_size, const WitPeImage *image, WitU64 base)
{
    const int runtime = image && (image->Profile & WIT_PE_RUNTIME_FULL);
    wit_files_initialize(&process->Files);
    wit_user_library_initialize(process);
    wit_user_process_state_reset(process);
    process->Id = next_id++;
    process->Slot = slot;
    process->State = WitUserEmpty;
    process->FatalArmed = 0;
    process->FatalOwner = 0;
    for (WitU32 i = 0; i < sizeof(process->Fatal); ++i) {
        ((WitU8 *)&process->Fatal)[i] = 0;
    }
    reset_counters(process);
    process->RequireThreadCompletion = runtime;
    process->AbruptThreadId = 0;
    process->AbruptThreadCode = 0;
    process->Ticks = 0;
    process->TickLimit = runtime ? WIT_RUNTIME_TICK_BUDGET : WIT_USER_TICK_BUDGET;
    process->ExitCode = 0;
    process->ImageBase = image ? base : WIT_USER_CODE;
    process->ImageEntry = image ? base + image->EntryRva : WIT_USER_CODE;
    process->ImageSize = image ? image->ImageSize : code_size;
    process->FaultVector = 0;
    process->FaultError = 0;
    process->FaultAddress = 0;
    process->FaultState = (WitArchFaultState){0};
    process->CurrentThread = 0;
    process->FaultThread = NO_THREAD;
    process->NextWaitOrder = 0;
    process->MemoryPressureLow = 0;
    for (WitU32 i = 0; i < WIT_RUNTIME_EVENT_CAPACITY; ++i) {
        process->MemoryPressureEvents[i] = 0;
    }
    wit_events_initialize(&process->Events);
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        process->Threads[i].State = WitThreadEmpty;
        process->Threads[i].SuspendCount = 0;
        wit_user_thread_name_clear(&process->Threads[i]);
    }
    wit_handles_initialize(&process->Handles, process->Id);
    if (runtime) {
        process->Handles.Limit = WIT_RUNTIME_HANDLE_CAPACITY;
        process->Events.Limit = WIT_RUNTIME_EVENT_CAPACITY;
    }
    wit_user_references_initialize(process);
    wit_user_stack_leases_initialize(process);
    wit_user_exception_initialize(process);
}

/* Builds the address space: the image or the fixed code page, TLS, the startup page and the data pages. */
static int map_process(WitUserProcess *process, WitPageAllocator *allocator, const WitU8 *code, WitU32 code_size,
    const WitPeImage *image, WitU64 base)
{
    if (!wit_user_space_create_profile(&process->Space, allocator, image && (image->Profile & WIT_PE_RUNTIME_FULL))) {
        return 0;
    }
    if (image) {
        if (!wit_user_image_map(&process->Space, code, image, base)) {
            return 0;
        }
    } else if (!wit_user_space_map(&process->Space, WIT_USER_CODE, 0, 1)) {
        return 0;
    }
    if (!wit_user_capture_tls(process, image) || !wit_user_space_map(&process->Space, WIT_USER_INFO, 0, 0)) {
        return 0;
    }
    for (WitU64 page = WIT_USER_DATA; page < WIT_USER_DATA_END; page += 4096) {
        if (!wit_user_space_map(&process->Space, page, 1, 0)) {
            return 0;
        }
    }
    if (!image) {
        const WitU64 physical = wit_user_space_physical(&process->Space, WIT_USER_CODE, 0, 1);
        for (WitU32 i = 0; i < code_size; ++i) {
            ((WitU8 *)physical)[i] = code[i];
        }
        wit_user_space_publish_code(&process->Space, WIT_USER_CODE, code_size);
    }
    return 1;
}

/* Describes the image after the startup block: placement, sections, unwind directory and resource name. */
static void write_image_info(
    WitUserProcess *process, WitUserStartup *startup, const WitPeImage *image, const WitU16 *resource, WitU32 length)
{
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
    info->ResourceNameLength = length;
    for (WitU32 i = 0; i < length; ++i) {
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

static WitPeStatus create_process(WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot, const WitU8 *code,
    WitU32 code_size, const WitPeImage *image, WitU64 base, const WitU16 *resource, WitU32 resource_length)
{
    WitUserStartup *startup;
    if (!can_create(process, slot)) {
        return WitPeBusy;
    }
    reset_process(process, slot, code_size, image, base);
    slot_owners[slot] = process;
    if (!map_process(process, allocator, code, code_size, image, base)) {
        goto failed;
    }
    startup = (WitUserStartup *)wit_user_space_physical(&process->Space, WIT_USER_INFO, 0, 0);
    startup->Version = WIT_ABI_VERSION;
    startup->Size = sizeof(*startup);
    startup->ImageInfo = 0;
    if (image) {
        write_image_info(process, startup, image, resource, resource_length);
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
            !wit_arch_interrupts_enabled() &&
            wit_arch_user_tls_is_reset(),
        "Invalid user launch");
    current_user = process;
    process->State = WitUserRunning;
    process->Threads[0].State = WitThreadRunning;
    process->Ticks = 0;
    wit_arch_select_thread_stack(process->Slot, 0);
    wit_platform_timer_start();
    wit_arch_set_user_tls(process->Threads[0].Tls, process->Threads[0].CompilerTls);
    wit_arch_run_user(process->Threads[0].Context, process->Space.Root);
    require(!current_user &&
            process->State != WitUserRunning &&
            wit_arch_kernel_space_active() &&
            !wit_arch_interrupts_enabled() &&
            wit_arch_user_tls_is_reset(),
        "User return did not restore kernel state");
    wit_arch_select_boot_stack();
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
    wit_user_process_state_reset(process);
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

WitUserProcess *wit_user_current(void)
{
    return current_user;
}

WitU64 wit_user_contained_faults(void)
{
    return contained_faults;
}

WitArchFrame *wit_user_yield(void)
{
    current_user->Threads[current_user->CurrentThread].State = WitThreadReady;
    return dispatch(0, 0);
}

WitArchFrame *wit_user_timer_tick(WitArchFrame *context)
{
    require(current_user != 0, "User timer without component");
    if (user_idle) {
        require(wit_arch_frame_owned(context, current_user->Slot, current_user->CurrentThread) &&
                wit_arch_frame_is_idle(context, current_user->Slot, current_user->CurrentThread),
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
        wit_user_finish(WitUserBudgetExpired, 0);
    }
    if (user_idle) {
        return context; /* Resume CLI/RET and recheck ready threads. */
    }
    return dispatch(1, 0);
}

WitArchFrame *wit_user_syscall(
    WitArchFrame *context, WitU64 number, WitU64 argument0, WitU64 argument1, WitU64 argument2)
{
    require(current_user != 0 && current_user->State == WitUserRunning, "Syscall without component");
    validate_return(context, current_user->CurrentThread, 1);
    expire_waits();
    current_user->Threads[current_user->CurrentThread].Context = context;
    WitUserCall call = {current_user, context, number, argument0, argument1, argument2, wit_arch_frame_status(context),
        wit_arch_frame_value(context)};
    wit_arch_frame_set_result(context, WIT_STATUS_OK, 0);
    WitArchFrame *const resume = wit_user_call(&call);
    if (resume) {
        return resume;
    }
    context = call.Context; /* A join may have switched the current thread. */
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

WitArchFrame *wit_user_exception_trap(WitArchFrame *context, WitU64 vector, WitU64 error, WitU64 address)
{
    require(current_user &&
            current_user->State == WitUserRunning &&
            wit_arch_frame_owned(context, current_user->Slot, current_user->CurrentThread) &&
            wit_arch_frame_from_user(context),
        "Invalid resumable user fault context");
    WitUserThread *thread = &current_user->Threads[current_user->CurrentThread];
    thread->Context = context;
    if (current_user->FatalArmed) {
        wit_user_finish(WitUserExited, current_user->Fatal.Code);
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
        wit_console_write_hex(wit_arch_context_pc(&thread->Exception.Context));
        wit_console_write(" nested-vector=");
        wit_console_write_u64(vector);
        wit_console_write(" address=");
        wit_console_write_hex(address);
        wit_console_write("\n");
        wit_user_finish(WitUserExited, WIT_EXCEPTION_SOFTWARE_FAILURE_EXIT);
    }
    WitArchFaultState state;
    if (thread->Exception.Token) {
        const WitUserExceptionInfo *original = &thread->Exception;
        wit_arch_fault_from_context(&state, &original->Context);
        vector = original->Vector;
        error = original->Error;
        address = original->Address;
    } else {
        wit_arch_fault_from_frame(&state, context);
    }
    wit_user_fault(&state, sizeof(state), vector, error, address, &state);
}

WIT_NORETURN void wit_user_fault(
    const void *trap, WitU64 trap_size, WitU64 vector, WitU64 error, WitU64 address, const WitArchFaultState *state)
{
    require(current_user != 0 &&
            wit_arch_kernel_stack_contains(current_user->Slot, current_user->CurrentThread, trap, trap_size) &&
            wit_arch_fault_from_user(state),
        "Invalid user fault frame");
    current_user->FaultThread = current_user->CurrentThread;
    current_user->FaultVector = vector;
    current_user->FaultError = error;
    current_user->FaultAddress = address;
    current_user->FaultState = *state;
    ++contained_faults;
    wit_console_write("[USER-FAULT] id=");
    wit_console_write_u64(current_user->Id);
    wit_console_write(" vector=");
    wit_console_write_u64(vector);
    wit_console_write(" error=");
    wit_console_write_hex(error);
    wit_console_write(" address=");
    wit_console_write_hex(address);
    wit_arch_fault_describe(state);
    wit_console_write("\n");
    wit_user_finish(WitUserFaulted, 0);
}

WitPeStatus wit_user_create_named_pe(WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot,
    const WitU8 *file, WitU32 size, WitU64 base, const char *resource_name)
{
    return wit_user_create_pe_profile(process, allocator, slot, file, size, base, resource_name, 0);
}
