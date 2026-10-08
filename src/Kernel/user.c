#include "user.h"
#include "root_task.h"
#include "witos/package.h"
#include "witos/platform.h"
#include "witos/storage.h"
#include "witos/virtual.h"

#define NO_THREAD WIT_USER_THREAD_CAPACITY

static WitUserProcess *current_user;
static WitUserProcess *slot_owners[WIT_PROCESS_CAPACITY]; /* the registry: the component of each slot (K5.2c) */
static WitUserProcess *root_user; /* the component wit_user_run launched; its end ends the run */
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
    if (state == WitUserBudgetExpired && current_user->RuntimeProfile) {
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

/* The component's references end (K5.2b): what its channels carried first, so that the capabilities in flight
 * return to their objects before the records that count them are reset; then its bindings, pins, devices, memory
 * objects and processes; then the table. Its mappings keep their objects until the teardown. */
static void release_references(WitUserProcess *p)
{
    wit_user_channels_drop(p);
    wit_user_interrupts_reset(p);
    wit_user_pins_reset(p);
    wit_user_devices_reset(p);
    wit_user_memory_objects_release_handles(p);
    wit_user_process_handles_release(p);
    wit_handles_close_all(&p->Handles);
    wit_files_initialize(&p->Files);
    wit_user_library_initialize(p);
    wit_user_references_initialize(p);
    wit_user_stack_leases_initialize(p);
    wit_user_exception_initialize(p);
    wit_events_initialize(&p->Events);
}

/* A component ended: its state and exit code are recorded, every thread is over and its references are released. */
static void settle(WitUserProcess *p, WitUserState state, WitU64 code)
{
    if (p->FatalArmed) {
        state = WitUserExited;
        code = p->Fatal.Code;
    }
    p->FatalArmed = 0;
    p->FatalOwner = 0;
    p->State = state;
    p->ExitCode = code;
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        if (p->Threads[i].State != WitThreadEmpty && p->Threads[i].State != WitThreadExited) {
            /* The handles that observe a thread the end cuts short learn its exit with the component's code. */
            p->Threads[i].State = WitThreadExited;
            wit_user_references_exit(p->Threads[i].Handle, code);
        }
        p->Threads[i].SuspendCount = 0;
        wit_user_thread_name_clear(&p->Threads[i]);
        p->Threads[i].WaitKind = WitWaitNone;
        p->Threads[i].WaitHandle = 0;
        p->Threads[i].WaitCount = 0;
        p->Threads[i].WaitAll = 0;
        wit_user_activations_clear(&p->Threads[i]);
        for (WitU32 w = 0; w < WIT_WAIT_ANY_CAPACITY; ++w) {
            p->Threads[i].WaitHandles[w] = 0;
        }
        p->Threads[i].Deadline = WIT_WAIT_INFINITE;
    }
    release_references(p);
}

/* The rest of a component's record goes: the mappings' references and the charges of what it created, the address
 * space (inactive by now), the threads and the image fields. The registry slot is the caller's to free. */
static void teardown(WitUserProcess *process)
{
    wit_user_memory_objects_release_mappings(process);
    wit_memory_objects_orphan(process);
    wit_channels_orphan(process);
    wit_user_process_state_reset(process);
    wit_user_space_destroy(&process->Space);
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        process->Threads[i].State = WitThreadEmpty;
        process->Threads[i].SuspendCount = 0;
        wit_user_thread_name_clear(&process->Threads[i]);
    }
    process->ImageBase = 0;
    process->ImageEntry = 0;
    process->ImageSize = 0;
    process->ImageNameBytes = 0;
    process->ImageNameOffset = 0;
    process->TlsBytes = 0;
}

static void unregister(WitUserProcess *process)
{
    if (process->Slot < WIT_PROCESS_CAPACITY && slot_owners[process->Slot] == process) {
        slot_owners[process->Slot] = 0;
    }
}

/* A created process ended (K5.2c): torn down at once, off the registry, the waiters of its handles woken; the record
 * stays for PROCESS_QUERY while handles refer to it, and user_process.c frees it with the last one. The ending
 * process may be the running one, on whose kernel stack this runs: its address space leaves first. */
static void retire(WitUserProcess *process)
{
    require(process->Pooled && process->State != WitUserRunning, "Retiring a live or unpooled process");
    if (wit_arch_space_active(process->Space.Root)) {
        wit_arch_space_switch(wit_virtual_kernel_root());
    }
    teardown(process);
    unregister(process);
    process->Retired = 1;
    wit_user_wait_objects_changed_all();
    wit_user_process_retired(process);
}

void wit_user_end(WitUserProcess *process, WitUserState state, WitU64 code)
{
    require(process != current_user && process->Pooled && process->State == WitUserRunning,
        "Ending a process that is not another live created process");
    settle(process, state, code);
    retire(process);
}

/* The root's end ends every created process still alive; nothing of the pool survives the run. */
static void end_children(void)
{
    for (WitU32 i = 0; i < WIT_PROCESS_CAPACITY; ++i) {
        WitUserProcess *q = slot_owners[i];
        if (q && q != root_user && q->Pooled && q->State == WitUserRunning) {
            wit_user_end(q, WitUserExited, 0);
        }
    }
    require(wit_user_processes_pooled() == 0, "A created process survived the root's end");
}

static WitArchFrame *dispatch(int timer, WitU64 last_exit);

WIT_NORETURN void wit_user_finish(WitUserState state, WitU64 code)
{
    require(current_user != 0 && current_user->State == WitUserRunning, "No current user component");
#if defined(WITOS_TEST_RUNTIME_BOOT)
    report_budget(state);
#endif
    WitUserProcess *p = current_user;
    settle(p, state, code);
    if (p != root_user) {
        /* A created process ended: the run goes on with the others, and the resume abandons this kernel stack. */
        retire(p);
        wit_arch_resume_frame(dispatch(0, code));
    }
    wit_platform_timer_stop();
    end_children();
    user_idle = 0;
    wit_arch_reset_user_tls();
    current_user = 0;
    root_user = 0;
    wit_arch_leave_user();
}

static void validate_return(WitArchFrame *frame, WitU32 index, int syscall)
{
    const WitUserThread *thread = &current_user->Threads[index];
    WitU64 bottom = 0, top = 0;
    require(wit_arch_frame_owned(frame, current_user->Slot, index), "User trap outside owning kernel stack");
    const WitU64 sp = wit_arch_frame_sp(frame);
    /* The stack pointer lies in the thread's stack or its alternate stack (S3.1). An empty stack sits at its top
     * (the one thread form starts there); the page probed is the one a push writes. */
    const int held = wit_user_thread_stack_range(thread, sp, &bottom, &top);
    const WitU64 probe = held && sp == top ? sp - 1 : sp;
    if (!wit_arch_frame_returns_to_user(frame) ||
        !held ||
        !wit_user_space_physical(&current_user->Space, probe, 1, 0) ||
        !wit_user_space_physical(&current_user->Space, wit_arch_frame_pc(frame), 0, 1)) {
        wit_user_finish(WitUserBadReturn, 0);
    }
    wit_arch_frame_prepare_return(frame, syscall);
}

/* Reclaims an exited thread's stack, TLS and private identity; the handles that observed it keep its exit code. */
static void reap(WitU32 index)
{
    WitUserThread *thread = &current_user->Threads[index];
    require(thread->State == WitThreadExited, "Reaping live thread");
    require(!wit_user_stack_leased(current_user, thread->Handle, 0), "Reaping leased stack");
    if (thread->OwnsStack) {
        for (WitU64 p = thread->StackBottom; p < thread->StackTop; p += 4096) {
            require(wit_user_space_unmap_fixed(&current_user->Space, p), "Thread stack ownership lost");
        }
        require(wit_user_space_unmap_fixed(&current_user->Space, thread->Tls), "Thread TLS ownership lost");
    } else if (thread->ExitReservation) {
        /* The one thread form (K5.2a): the exiting thread named its stack's reservation, validated at the exit; the
         * thread no longer runs on it. */
        require(wit_user_memory_unmap(current_user, thread->ExitReservation, 0) == WIT_STATUS_OK,
            "Exit reservation vanished before its release");
        thread->ExitReservation = 0;
    }
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
    thread->State = WitThreadEmpty;
    ++current_user->ThreadReaps;
}

/* Every live component's deadlines and pressure; the one clock read serves them all. */
static void expire_waits(void)
{
    const WitU64 now = wit_platform_monotonic_read();
    for (WitU32 i = 0; i < WIT_PROCESS_CAPACITY; ++i) {
        WitUserProcess *q = slot_owners[i];
        if (q && q->State == WitUserRunning) {
            wit_user_wait_expire(q, now);
            wit_user_pressure_update(q);
        }
    }
}

/* The running thread's frame is about to return to user mode (RFC 0011 section 7.5: from a call, a tick or a wait): a
 * pending activation enters the fault callback first, and the frame it leaves is validated like any user return. */
static WitArchFrame *resume_current(WitArchFrame *frame)
{
    const WitU32 index = current_user->CurrentThread;
    WitUserThread *thread = &current_user->Threads[index];
    require(frame == thread->Context, "Resuming a frame the current thread does not own");
    if (wit_user_activation_deliver(current_user, thread)) {
        validate_return(frame, index, 0);
    }
    return frame;
}

/* The next thread to run, round-robin over the live components from the current one and over each component's
 * threads from the one that ran last (K5.2c). Another component's thread means its address space, its kernel stack
 * and its TLS; a component with no ready thread but a waiting or suspended one keeps the processor idle until a tick
 * or a device line changes something. The component whose last thread exited ended in wit_user_exit_thread. */
static WitArchFrame *dispatch(int timer, WitU64 last_exit)
{
    (void)last_exit;
    for (;;) {
        int waiting = 0;
        expire_waits();
        const WitU32 first = current_user ? current_user->Slot : 0;
        for (WitU32 n = 0; n < WIT_PROCESS_CAPACITY; ++n) {
            WitUserProcess *q = slot_owners[(first + n) % WIT_PROCESS_CAPACITY];
            if (!q || q->State != WitUserRunning) {
                continue;
            }
            const WitU32 previous = q->CurrentThread;
            for (WitU32 offset = 1; offset <= WIT_USER_THREAD_CAPACITY; ++offset) {
                const WitU32 index = (previous + offset) % WIT_USER_THREAD_CAPACITY;
                WitUserThread *thread = &q->Threads[index];
                if (thread->State == WitThreadWaiting || thread->SuspendCount) {
                    waiting = 1;
                }
                if (thread->State != WitThreadReady || thread->SuspendCount) {
                    continue;
                }
                if (q != current_user) {
                    wit_arch_space_switch(q->Space.Root);
                    current_user = q;
                }
                validate_return(thread->Context, index, 0);
                if (index != previous) {
                    ++q->ThreadSwitches;
                    if (timer) {
                        ++q->ThreadTimerSwitches;
                    }
                }
                q->CurrentThread = index;
                thread->State = WitThreadRunning;
                wit_arch_select_thread_stack(q->Slot, index);
                wit_arch_set_user_tls(thread->Tls, thread->CompilerTls);
                return resume_current(thread->Context);
            }
        }
        require(waiting, "No thread of any component to run or to wait for");
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

WitArchFrame *wit_user_exit_thread(WitU64 code, WitU64 reservation, WitU64 clear, WitU64 event)
{
    static const WitU8 zero[4] = {0};
    const WitU32 index = current_user->CurrentThread;
    WitUserThread *thread = &current_user->Threads[index];
    thread->ExitReservation = thread->OwnsStack ? 0 : reservation;
    thread->ExitClear = clear;
    thread->ExitEvent = event;
    wit_user_exception_clear(thread);
    wit_user_stack_leases_exit(current_user, thread->Handle);
    require(!wit_user_stack_leased(current_user, thread->Handle, 0), "Exiting foreign-leased stack");
    wit_user_thread_name_clear(thread);
    thread->State = WitThreadExited;
    thread->ExitCode = code;
    thread->WaitAll = 0;
    wit_user_activations_clear(thread);
    /* The handles that observe the thread, in every component, learn its exit (and wake their waiters) before its
     * pages go; the last thread's exit ends the component with its code. */
    wit_user_references_exit(thread->Handle, code);
    /* The exit request (S2.1): the thread no longer runs and this path is not interrupted, so its word is zeroed and
     * its event set now, before the reap releases the stack reservation the word may lie in; both were validated at
     * the call and nothing else ran since. The woken waiters run after the reap. */
    if (clear) {
        require(
            wit_user_copy_to(&current_user->Space, clear, zero, sizeof(zero)), "Exit word vanished before the exit");
    }
    if (event) {
        require(wit_user_event_set(current_user, event) == WIT_STATUS_OK, "Exit event vanished before the exit");
    }
    reap(index);
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        if (current_user->Threads[i].State != WitThreadEmpty) {
            return dispatch(0, code);
        }
    }
    wit_user_finish(WitUserExited, code);
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
    const WitU64 channel = wit_user_channel_close(current_user, handle);
    if (channel != WIT_STATUS_WRONG_TYPE) {
        return channel;
    }
    const WitU64 object = wit_user_memory_object_close(current_user, handle);
    if (object != WIT_STATUS_WRONG_TYPE) {
        return object;
    }
    const WitU64 device = wit_user_device_close(current_user, handle);
    if (device != WIT_STATUS_WRONG_TYPE) {
        return device;
    }
    const WitU64 interrupt = wit_user_interrupt_close(current_user, handle);
    if (interrupt != WIT_STATUS_WRONG_TYPE) {
        return interrupt;
    }
    const WitU64 pin = wit_user_pin_close(current_user, handle);
    if (pin != WIT_STATUS_WRONG_TYPE) {
        return pin;
    }
    const WitU64 process_status = wit_user_process_close(current_user, handle);
    if (process_status != WIT_STATUS_WRONG_TYPE) {
        return process_status;
    }
    /* A thread's private identity is not a capability user space can release: it ends with the thread. */
    const WitU64 status = wit_handle_check(&current_user->Handles, handle, WIT_HANDLE_THREAD_IDENTITY, 0);
    if (status == WIT_STATUS_OK) {
        return WIT_STATUS_BUSY;
    }
    if (status != WIT_STATUS_WRONG_TYPE) {
        return status;
    }
    const WitU64 event_status = wit_user_event_close(current_user, handle);
    return event_status == WIT_STATUS_WRONG_TYPE ? wit_handle_close(&current_user->Handles, handle) : event_status;
}

static int registered(const WitUserProcess *process)
{
    for (WitU32 i = 0; i < WIT_PROCESS_CAPACITY; ++i) {
        if (slot_owners[i] == process) {
            return 1;
        }
    }
    return 0;
}

static int can_create(const WitUserProcess *process, WitU32 slot)
{
    return process &&
        !current_user &&
        slot < WIT_PROCESS_CAPACITY &&
        !slot_owners[slot] &&
        next_id &&
        !registered(process) &&
        !wit_arch_interrupts_enabled();
}

/* Clears the statistics of a fresh component. */
static void reset_counters(WitUserProcess *process)
{
    process->Writes = 0;
    process->RandomRequests = 0;
    process->RandomBytes = 0;
    process->ThreadExits = 0;
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
    process->ThreadReaps = 0;
    process->EventParks = 0;
    process->EventWakes = 0;
    process->ThreadWaitParks = 0;
    process->ThreadWaitWakes = 0;
    process->WaitTimeouts = 0;
    process->WaitCloses = 0;
    process->WaitInterruptions = 0;
    process->ActivationDeliveries = 0;
    process->ChannelSends = 0;
    process->ChannelReceives = 0;
    process->ChannelDrops = 0;
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
    process->RuntimeProfile = runtime;
    process->AbruptThreadId = 0;
    process->AbruptThreadCode = 0;
    process->Ticks = 0;
    process->TickLimit = runtime ? WIT_RUNTIME_TICK_BUDGET : WIT_USER_TICK_BUDGET;
    process->ExitCode = 0;
    process->ImageBase = image ? base : WIT_USER_CODE;
    process->ImageEntry = image ? base + image->EntryRva : WIT_USER_CODE;
    process->ImageSize = image ? image->ImageSize : code_size;
    process->ImageNameBytes = 0;
    process->ImageNameOffset = 0;
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
    wit_user_interrupts_reset(process);
    wit_user_pins_reset(process);
    process->InterruptsDelivered = 0;
    wit_events_initialize(&process->Events);
    require(wit_memory_objects_charged(process) == 0 && wit_channels_charged(process) == 0,
        "A fresh component inherits memory objects or channels");
    wit_user_devices_reset(process);
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

int wit_user_create_flat(WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot, const WitFlatLayout *layout)
{
    if (!can_create(process, slot)) {
        return 0;
    }
    reset_process(process, slot, 0, 0, 0);
    /* The root task is the system layer (S1.3): the full profile's handles, events, pages and the wider fixed window,
     * as a created process gets them, so that a libc program and later the runtime fit, and the system layer's
     * reservation table (S5.4). */
    process->Handles.Limit = WIT_RUNTIME_HANDLE_CAPACITY;
    process->Events.Limit = WIT_RUNTIME_EVENT_CAPACITY;
    process->TickLimit = WIT_RUNTIME_TICK_BUDGET;
    slot_owners[slot] = process;
    process->ImageBase = layout->Segments[0].Address;
    process->ImageEntry = layout->Entry;
    process->ImageSize = 0;
    if (!wit_user_space_create_profile(&process->Space, allocator, 1) ||
        !wit_user_capture_tls(process, 0) ||
        !wit_user_space_map(&process->Space, WIT_USER_INFO, 0, 0)) {
        goto failed;
    }
    process->Space.ReservationLimit = WIT_PROCESS_RESERVATION_CAPACITY;
    for (WitU64 page = WIT_USER_DATA; page < WIT_USER_DATA_END; page += 4096) {
        if (!wit_user_space_map(&process->Space, page, 1, 0)) {
            goto failed;
        }
    }
    for (WitU32 i = 0; i < layout->SegmentCount; ++i) {
        const WitFlatSegment *s = &layout->Segments[i];
        const int writable = (s->Protection & WIT_MEMORY_WRITE) != 0,
                  executable = (s->Protection & WIT_MEMORY_EXECUTE) != 0;
        for (WitU64 offset = 0; offset < s->MemorySize; offset += 4096) {
            if (!wit_user_space_map(&process->Space, s->Address + offset, writable, executable)) {
                goto failed;
            }
            WitU8 *page = (WitU8 *)wit_user_space_physical(&process->Space, s->Address + offset, 0, 0);
            const WitU64 copy = offset < s->FileSize ? (s->FileSize - offset < 4096 ? s->FileSize - offset : 4096) : 0;
            for (WitU64 k = 0; k < copy; ++k) {
                page[k] = layout->File[s->FileOffset + offset + k];
            }
        }
        if (executable) {
            wit_user_space_publish_code(&process->Space, s->Address, s->MemorySize);
        }
        if (s->Address + s->MemorySize - process->ImageBase > process->ImageSize) {
            process->ImageSize = (WitU32)(s->Address + s->MemorySize - process->ImageBase);
        }
    }
    if (wit_user_prepare_thread(process, 0, layout->Entry, WIT_USER_INFO, 0) != WIT_STATUS_OK) {
        goto failed;
    }
    process->State = WitUserReady;
    return 1;
failed:
    wit_user_destroy(process);
    return 0;
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

/* An empty process for PROCESS_CREATE (K5.2c): a record of the pool in a free registry slot, the full profile's
 * quotas with the page quota asked and the creator's tick budget, no thread yet; it is part of the schedule from
 * now on. Unlike the components the host creates, it is created while a component runs. */
int wit_user_create_empty(WitUserProcess *process, WitPageAllocator *allocator, WitU32 pages, WitU64 ticks)
{
    WitU32 slot = 0;
    while (slot < WIT_PROCESS_CAPACITY && slot_owners[slot]) {
        ++slot;
    }
    if (slot == WIT_PROCESS_CAPACITY ||
        !process ||
        registered(process) ||
        !next_id ||
        !pages ||
        wit_arch_interrupts_enabled()) {
        return 0;
    }
    reset_process(process, slot, 0, 0, 0);
    process->Handles.Limit = WIT_RUNTIME_HANDLE_CAPACITY;
    process->Events.Limit = WIT_RUNTIME_EVENT_CAPACITY;
    process->TickLimit = ticks;
    slot_owners[slot] = process;
    if (!wit_user_space_create_profile(&process->Space, allocator, 1) || !wit_user_capture_tls(process, 0)) {
        wit_user_destroy(process);
        return 0;
    }
    process->Space.PageLimit = pages;
    process->Space.ReservationLimit = WIT_PROCESS_RESERVATION_CAPACITY; /* the system layer's table (S5.4) */
    process->State = WitUserRunning;
    return 1;
}

void wit_user_run(WitUserProcess *process)
{
    require(!current_user &&
            !root_user &&
            !user_idle &&
            process->State == WitUserReady &&
            slot_owners[process->Slot] == process &&
            !wit_arch_interrupts_enabled() &&
            wit_arch_user_tls_is_reset(),
        "Invalid user launch");
    require(wit_user_processes_pooled() == 0, "A created process survived the previous run");
    root_user = process;
    current_user = process;
    process->State = WitUserRunning;
    process->Threads[0].State = WitThreadRunning;
    process->Ticks = 0;
    wit_arch_select_thread_stack(process->Slot, 0);
    wit_platform_timer_start();
    wit_arch_set_user_tls(process->Threads[0].Tls, process->Threads[0].CompilerTls);
    wit_arch_run_user(process->Threads[0].Context, process->Space.Root);
    require(!current_user &&
            !root_user &&
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
    /* A component torn down without an exit still holds its references; the order is the exit's (K5.2b). */
    release_references(process);
    teardown(process);
    unregister(process);
    process->State = WitUserEmpty;
}

int wit_user_is_active(void)
{
    return current_user != 0;
}

WitUserProcess *wit_user_current(void)
{
    return current_user;
}

WitUserProcess *wit_user_process_at(WitU32 index)
{
    return index < sizeof(slot_owners) / sizeof(slot_owners[0]) ? slot_owners[index] : 0;
}

WitUserProcess *wit_user_process_by_id(WitU32 id)
{
    for (WitU32 i = 0; i < sizeof(slot_owners) / sizeof(slot_owners[0]); ++i) {
        if (slot_owners[i] && slot_owners[i]->Id == id) {
            return slot_owners[i];
        }
    }
    return 0;
}

/* A kernel object changed (an endpoint's queue or peer): the parked waits of every component may be ready. */
void wit_user_wait_objects_changed_all(void)
{
    for (WitU32 i = 0; i < sizeof(slot_owners) / sizeof(slot_owners[0]); ++i) {
        if (slot_owners[i] && slot_owners[i]->State == WitUserRunning) {
            wit_user_wait_objects_changed(slot_owners[i]);
        }
    }
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
    /* The budget is the running component's; the idle path after a created process's end charges nobody. */
    if (current_user->State == WitUserRunning && ++current_user->Ticks >= current_user->TickLimit) {
        wit_user_finish(WitUserBudgetExpired, 0);
    }
    if (user_idle) {
        return context; /* Resume CLI/RET and recheck ready threads. */
    }
    return dispatch(1, 0);
}

/* A device line, masked and completed by the architecture: the bound event of the running component is set and
 * the ready threads are dispatched as after a tick; without a component the line stays masked. */
WitArchFrame *wit_user_interrupt(WitArchFrame *context, WitU32 line)
{
    if (!current_user) {
        return context;
    }
    if (user_idle) {
        require(wit_arch_frame_owned(context, current_user->Slot, current_user->CurrentThread) &&
                wit_arch_frame_is_idle(context, current_user->Slot, current_user->CurrentThread),
            "Device interrupt did not interrupt the kernel idle path");
    } else {
        require(current_user->State == WitUserRunning, "Device interrupt outside a running component");
        WitUserThread *thread = &current_user->Threads[current_user->CurrentThread];
        validate_return(context, current_user->CurrentThread, 0);
        thread->Context = context;
        thread->State = WitThreadReady;
    }
    wit_user_interrupt_raised(line); /* To the binding's owner, whichever component that is (K5.2c). */
    expire_waits();
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
        return resume_current(resume);
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
    wit_arch_set_user_tls(current_user->Threads[current_user->CurrentThread].Tls,
        current_user->Threads[current_user->CurrentThread].CompilerTls);
    return resume_current(context);
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
    /* A fault inside the handler of a fault reports the original fault; a fault inside the handler of an activation is
     * the thread's own fault and is reported as such. */
    WitArchFaultState state;
    if (thread->Exception.Token && thread->Exception.Vector != WIT_EXCEPTION_ACTIVATION_VECTOR) {
        const WitUserExceptionInfo *original = &thread->Exception;
        wit_arch_fault_from_record(&state, original);
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
            wit_arch_kernel_stack_contains(current_user->Slot, current_user->CurrentThread, trap, trap_size),
        "Invalid user fault frame");
    wit_user_fault_state(vector, error, address, state);
}

WIT_NORETURN void wit_user_fault_state(WitU64 vector, WitU64 error, WitU64 address, const WitArchFaultState *state)
{
    require(current_user != 0 && wit_arch_fault_from_user(state), "Invalid user fault state");
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

WitPeStatus wit_user_create_package_pe(
    WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot, const char *name, WitU64 base, WitU32 profile)
{
    const WitPackage *package = wit_storage_package();
    WitU32 length = 0;
    while (name && length < WIT_PACKAGE_MAX_NAME && name[length]) {
        ++length;
    }
    WitPackageFile file;
    if (!package ||
        !length ||
        wit_package_find(package, (const WitU8 *)name, length, &file) != WitPackageOk ||
        file.Length > 0xFFFFFFFFULL) {
        return WitPeInvalidImage;
    }
    /* The package's bytes are immutable and kernel-owned for the boot's lifetime, as a loaded library's are. */
    const WitPeStatus status = wit_user_create_pe_profile(
        process, allocator, slot, package->Data + file.Offset, (WitU32)file.Length, base, 0, profile);
    if (status == WitPeOk) {
        process->ImageNameOffset = (WitU64)(file.Name - package->Data);
        process->ImageNameBytes = file.NameLength;
    }
    return status;
}
