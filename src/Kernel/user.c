#include "user.h"
#include "root_task.h"
#include "witos/cpu.h"
#include "witos/kernel_stacks.h"
#include "witos/platform.h"
#include "witos/processor.h"
#include "witos/virtual.h"

#define NO_THREAD WIT_PROCESS_THREAD_CAPACITY

#define PARK_SECONDS 2U

/* The dispatcher's state of each processor (P1.1): the component whose address space it has active — the one it runs,
 * or the one whose thread went to wait when it went idle —, the thread it runs, whether it idles now, and how the
 * dispatch that sent it idle looked for a thread. A processor idles on its own stack, never on a waiting thread's
 * kernel stack, which another processor could resume. Threads run on every processor (P1.3b): a secondary runs its
 * tick during a run and parks between runs, and an idle processor was kicked once when a thread became ready. */
typedef struct Dispatcher {
    WitUserProcess *Current;
    WitU32 Thread;
    volatile WitU32 Idle;
    int IdleTimer;
    int IdleRotate;
    volatile WitU32 Parked; /* a secondary between runs: no component, no tick */
    WitU32 Ticking; /* a secondary's own tick runs */
    WitU32 Kicked; /* asked to look again since it went idle */
    const WitUserThread *Yielding; /* the thread whose THREAD_YIELD this processor dispatches (P2) */
} Dispatcher;

static Dispatcher dispatchers[WIT_PROCESSOR_CAPACITY];
static WitUserProcess *slot_owners[WIT_PROCESS_CAPACITY]; /* the registry: the component of each slot (K5.2c) */
static WitUserProcess *root_user; /* the component wit_user_run launched; its end ends the run */
static WitU32 next_id = 1;
static WitU64 contained_faults;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static WitU32 me(void)
{
    return wit_processors_current();
}

/* The calling processor's dispatcher state. */
static Dispatcher *self(void)
{
    return &dispatchers[me()];
}

/* A run is on: the root task runs (P1.3b). Once it ended the processors leave what they run and the run ends. */
static int run_active(void)
{
    return root_user && root_user->State == WitUserRunning;
}

/* Whether any processor has the component's address space active. */
static int active_anywhere(const WitUserProcess *process)
{
    for (WitU32 i = 0; i < WIT_PROCESSOR_CAPACITY; ++i) {
        if (dispatchers[i].Current == process) {
            return 1;
        }
    }
    return 0;
}

/* The calling processor's idle stack, which the architecture gives (P1.1). */
static WitU64 idle_low(void)
{
    WitU64 low, high;
    wit_arch_idle_stack(wit_processors_current(), &low, &high);
    return low;
}

static WitU64 idle_high(void)
{
    WitU64 low, high;
    wit_arch_idle_stack(wit_processors_current(), &low, &high);
    return high;
}

/* A processor enters the kernel from its thread or its idle path: it takes the kernel lock (P1.2), and its component's
 * current thread is the one this processor runs (P1.1), which the calls name as their caller. Another processor may
 * have ended the component meanwhile (P1.3b); the entry then abandons the thread (abandon()). */
static WitUserProcess *enter(void)
{
    wit_cpus_lock();
    wit_kernel_stacks_collect(); /* stacks released since: this processor runs on a live thread's (P1.5) */
    Dispatcher *const cpu = self();
    if (cpu->Current && !cpu->Idle && cpu->Current->State == WitUserRunning) {
        cpu->Current->CurrentThread = cpu->Thread;
    }
    return cpu->Current;
}

/* Whether a ready thread that nobody runs and that may run on the processor exists in any live component (P1.4). */
static int thread_ready_for(WitU32 processor)
{
    for (WitU32 i = 0; i < WIT_PROCESS_CAPACITY; ++i) {
        const WitUserProcess *q = slot_owners[i];
        if (!q || q->State != WitUserRunning) {
            continue;
        }
        for (WitU32 t = 0; t < q->ThreadTop; ++t) {
            const WitUserThread *thread = &q->Threads[t];
            if (thread->State == WitThreadReady && !thread->SuspendCount && (thread->Affinity & (1ULL << processor))) {
                return 1;
            }
        }
    }
    return 0;
}

/* The processor leaves the kernel for a frame it returns to (P1.2). An idle processor is kicked once when a thread is
 * ready that this one does not run (P1.3b) and that may run there (P1.4). The lock goes in the architecture's return,
 * on the frame's stack: until then this processor runs on a kernel stack another could take, that of a thread it left
 * ready or waiting. */
static void kick_idle(void)
{
    if (wit_cpus_online() > 1 && run_active()) {
        for (WitU32 i = 0; i < WIT_PROCESSOR_CAPACITY; ++i) {
            Dispatcher *const other = &dispatchers[i];
            if (i != me() &&
                wit_cpus_is_online(i) &&
                other->Idle &&
                !other->Parked &&
                !other->Kicked &&
                thread_ready_for(i)) {
                other->Kicked = 1;
                wit_cpus_request(i, WIT_REQUEST_RESCHEDULE);
                break;
            }
        }
    }
}

static WitArchFrame *leave(WitArchFrame *frame)
{
    kick_idle();
    return frame;
}

/* Every other processor that has the component active enters its dispatcher (P1.3b). A request returns once the
 * target took the doorbell, so none of them runs the component's user code any more when this returns. */
static void reschedule_others(const WitUserProcess *process)
{
    for (WitU32 i = 0; i < WIT_PROCESSOR_CAPACITY; ++i) {
        if (i != me() && dispatchers[i].Current == process && wit_cpus_is_online(i)) {
            wit_cpus_request(i, WIT_REQUEST_RESCHEDULE);
        }
    }
}

void wit_user_thread_kick(const WitUserThread *thread)
{
    if (thread->State == WitThreadRunning && thread->Processor != me() && wit_cpus_is_online(thread->Processor)) {
        wit_cpus_request(thread->Processor, WIT_REQUEST_RESCHEDULE);
    }
}

void wit_user_space_shootdown(const WitUserSpace *space, WitU64 address)
{
    if (wit_cpus_online() < 2) {
        return;
    }
    for (WitU32 i = 0; i < WIT_PROCESSOR_CAPACITY; ++i) {
        const WitUserProcess *q = dispatchers[i].Current;
        if (i != me() && q && q->Space.Root == space->Root && wit_cpus_is_online(i)) {
            wit_cpus_invalidate(i, address);
        }
    }
}

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
    wit_user_references_initialize(p);
    wit_user_exception_initialize(p);
    wit_events_initialize(&p->Events);
}

/* A component ended: its state and exit code are recorded, every thread is over and its references are released. */
static void settle(WitUserProcess *p, WitUserState state, WitU64 code)
{
    p->State = state;
    p->ExitCode = code;
    for (WitU32 i = 0; i < WIT_PROCESS_THREAD_CAPACITY; ++i) {
        if (p->Threads[i].State != WitThreadEmpty && p->Threads[i].State != WitThreadExited) {
            /* The handles that observe a thread the end cuts short learn its exit with the component's code. */
            p->Threads[i].State = WitThreadExited;
            wit_user_references_exit(p->Threads[i].Handle, code);
        }
        p->Threads[i].SuspendCount = 0;
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
    wit_user_space_destroy(&process->Space);
    for (WitU32 i = 0; i < WIT_PROCESS_THREAD_CAPACITY; ++i) {
        process->Threads[i].State = WitThreadEmpty;
        process->Threads[i].SuspendCount = 0;
    }
    process->ThreadTop = 0;
    if (process->Slot < WIT_PROCESS_CAPACITY) {
        wit_kernel_stack_release_slot(process->Slot); /* collected once no processor runs on them (P1.5) */
    }
    process->ImageBase = 0;
    process->ImageEntry = 0;
    process->ImageSize = 0;
}

static void unregister(WitUserProcess *process)
{
    if (process->Slot < WIT_PROCESS_CAPACITY && slot_owners[process->Slot] == process) {
        slot_owners[process->Slot] = 0;
    }
}

/* A created process ended (K5.2c): torn down, off the registry, the waiters of its handles woken; the record stays for
 * PROCESS_QUERY while handles refer to it, and user_process.c frees it with the last one. Only once no processor has
 * its address space active (P1.3b): the last processor to leave it retires it. */
static void retire(WitUserProcess *process)
{
    require(process->Pooled &&
            process->State != WitUserRunning &&
            !process->Retired &&
            !active_anywhere(process) &&
            !wit_arch_space_active(process->Space.Root),
        "Retiring a live, unpooled or active process");
    teardown(process);
    unregister(process);
    process->Retired = 1;
    wit_user_wait_objects_changed_all();
    wit_user_process_retired(process);
}

/* The calling processor leaves the component it has active once that ended (P1.3b): the kernel's root, no user TLS,
 * and the component retires when no processor has it active any more. */
static void drop_component(Dispatcher *cpu)
{
    WitUserProcess *const process = cpu->Current;
    if (!process || process->State == WitUserRunning) {
        return;
    }
    if (wit_arch_space_active(process->Space.Root)) {
        wit_arch_space_switch(wit_virtual_kernel_root());
    }
    wit_arch_reset_user_tls();
    cpu->Current = 0;
    if (process->Pooled && !process->Retired && !active_anywhere(process)) {
        retire(process);
    }
}

/* A component ends (P1.3b): every other processor that has it active leaves its user code first, then its threads end
 * and its references go; its address space stays until no processor has it active. */
static void end(WitUserProcess *process, WitUserState state, WitU64 code)
{
    reschedule_others(process);
    settle(process, state, code);
}

void wit_user_end(WitUserProcess *process, WitUserState state, WitU64 code)
{
    require(process != self()->Current && process->Pooled && process->State == WitUserRunning,
        "Ending a process that is not another live created process");
    end(process, state, code);
    if (!active_anywhere(process)) {
        retire(process);
    }
}

/* The root's end ends every created process still alive; nothing of the pool survives the run (finish_run). */
static void end_children(void)
{
    for (WitU32 i = 0; i < WIT_PROCESS_CAPACITY; ++i) {
        WitUserProcess *q = slot_owners[i];
        if (q && q != root_user && q->Pooled && q->State == WitUserRunning) {
            wit_user_end(q, WitUserExited, 0);
        }
    }
}

static WitArchFrame *dispatch(int timer, int rotate);

/* The run ended with the root task (P1.3b): on the boot processor, once every secondary left what it had active and
 * parked — they need the lock, which this processor gives up while it waits —, the processes the run created are gone
 * and the root task's launch returns to the host, which gives the lock up on its own stack. */
WIT_NORETURN static void finish_run(void)
{
    Dispatcher *const cpu = self();
    require(me() == 0 && root_user && !run_active(), "The run ends on the boot processor after the root task");
    cpu->Idle = 0;
    drop_component(cpu);
    WitU32 kicked = 0;
    const WitU64 deadline = wit_platform_monotonic_read() + wit_platform_monotonic_frequency() * PARK_SECONDS;
    for (;;) {
        int parked = 1;
        for (WitU32 i = 1; i < WIT_PROCESSOR_CAPACITY; ++i) {
            if (wit_cpus_is_online(i) && (!dispatchers[i].Parked || dispatchers[i].Current)) {
                parked = 0;
                if (!(kicked & (1U << i))) {
                    kicked |= 1U << i;
                    wit_cpus_request(i, WIT_REQUEST_RESCHEDULE);
                }
            }
        }
        if (parked) {
            break;
        }
        require(wit_platform_monotonic_read() < deadline, "A secondary processor did not park at the run's end");
        wit_cpus_unlock();
        for (WitU32 spin = 0; spin < 64; ++spin) {
            wit_cpus_serve();
            wit_arch_spin_pause();
        }
        wit_cpus_lock();
    }
    wit_platform_timer_stop();
    require(wit_user_processes_pooled() == 0, "A created process survived the root's end");
    wit_arch_reset_user_tls();
    cpu->Current = 0;
    root_user = 0;
    wit_arch_leave_user();
}

WIT_NORETURN void wit_user_finish(WitUserState state, WitU64 code)
{
    Dispatcher *const cpu = self();
    WitUserProcess *const p = cpu->Current;
    require(p != 0 && p->State == WitUserRunning, "No current user component");
    end(p, state, code);
    if (p == root_user) {
        end_children();
    }
    /* The run goes on with the others, or ends; the resume abandons this kernel stack, the idle path's too when the
     * end came from a tick or a line that interrupted it (R6). */
    cpu->Idle = 0;
    drop_component(cpu);
    if (!run_active()) {
        if (me() == 0) {
            finish_run();
        }
        wit_cpus_request(0, WIT_REQUEST_RESCHEDULE); /* the boot processor ends the run */
    }
    wit_arch_resume_frame(dispatch(0, 0));
}

static void validate_return(WitArchFrame *frame, WitU32 index, int syscall)
{
    const WitUserThread *thread = &self()->Current->Threads[index];
    WitU64 bottom = 0, top = 0;
    require(wit_arch_frame_owned(frame, self()->Current->Slot, index), "User trap outside owning kernel stack");
    const WitU64 sp = wit_arch_frame_sp(frame);
    /* The stack pointer lies in the thread's stack or its alternate stack (S3.1). An empty stack sits at its top
     * (the one thread form starts there); the page probed is the one a push writes. */
    const int held = wit_user_thread_stack_range(thread, sp, &bottom, &top);
    const WitU64 probe = held && sp == top ? sp - 1 : sp;
    if (!wit_arch_frame_returns_to_user(frame) ||
        !held ||
        !wit_user_space_physical(&self()->Current->Space, probe, 1, 0) ||
        !wit_user_space_physical(&self()->Current->Space, wit_arch_frame_pc(frame), 0, 1)) {
        wit_user_finish(WitUserBadReturn, 0);
    }
    wit_arch_frame_prepare_return(frame, syscall);
}

/* Reclaims an exited thread's stack, TLS and private identity; the handles that observed it keep its exit code. */
static void reap(WitU32 index)
{
    WitUserThread *thread = &self()->Current->Threads[index];
    require(thread->State == WitThreadExited, "Reaping live thread");
    if (thread->OwnsStack) {
        for (WitU64 p = thread->StackBottom; p < thread->StackTop; p += 4096) {
            require(wit_user_space_unmap_fixed(&self()->Current->Space, p), "Thread stack ownership lost");
        }
    } else if (thread->ExitReservation) {
        /* The one thread form (K5.2a): the exiting thread named its stack's reservation, validated at the exit; the
         * thread no longer runs on it. */
        require(wit_user_memory_unmap(self()->Current, thread->ExitReservation, 0) == WIT_STATUS_OK,
            "Exit reservation vanished before its release");
        thread->ExitReservation = 0;
    }
    require(wit_handle_close(&self()->Current->Handles, thread->Handle) == WIT_STATUS_OK, "Thread handle lost");
    thread->Handle = 0;
    thread->SuspendCount = 0;
    thread->State = WitThreadEmpty;
    wit_user_thread_top_update(self()->Current);
    ++self()->Current->ThreadReaps;
    wit_kernel_stack_release(self()->Current->Slot, index); /* this processor may still run on it (P1.5) */
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

/* Whether the idle processor can still be woken: by a thread a deadline made ready, by a parked wait's deadline in any
 * component or by a bound line.
 * When nothing can wake it, every component waits for another that waits too, and the idle ticks are charged to the
 * component that went idle, so that its budget bounds the deadlock; otherwise a tick of the idle path charges nobody
 * (R6: a process whose threads all waited with deadlines was ended by the ticks of its waits). */
static int idle_can_wake(void)
{
    if (wit_user_interrupts_bound()) {
        return 1;
    }
    for (WitU32 i = 0; i < WIT_PROCESS_CAPACITY; ++i) {
        const WitUserProcess *q = slot_owners[i];
        if (!q || q->State != WitUserRunning) {
            continue;
        }
        if (wit_user_wait_timed(q)) {
            return 1;
        }
        for (WitU32 t = 0; t < q->ThreadTop; ++t) {
            /* A thread another processor runs may wake it too (P1.3b). */
            if (q->Threads[t].State == WitThreadReady || q->Threads[t].State == WitThreadRunning) {
                return 1;
            }
        }
    }
    return 0;
}

/* The running thread's frame is about to return to user mode (RFC 0011 section 7.5: from a call, a tick or a wait): a
 * pending activation enters the fault callback first, and the frame it leaves is validated like any user return. */
static WitArchFrame *resume_current(WitArchFrame *frame)
{
    const WitU32 index = self()->Current->CurrentThread;
    WitUserThread *thread = &self()->Current->Threads[index];
    require(frame == thread->Context, "Resuming a frame the current thread does not own");
    if (self()->Yielding == thread) {
        /* THREAD_YIELD's value, written before an activation's delivery may take the frame's argument registers (P2):
         * the yield picked its caller again. */
        *wit_arch_frame_value(frame) = 0;
    }
    if (wit_user_activation_deliver(self()->Current, thread)) {
        validate_return(frame, index, 0);
    }
    return frame;
}

/* The next thread to run, round-robin over the live components and over each component's threads from the one that
 * ran last (K5.2c). A tick or a yield rotates: the next component comes first and the current one last, so that a
 * thread that keeps running, or spins on a lock whose holder waits for another component, never keeps the processor
 * from the others (D5.2); a wait, an exit or a device line looks at the current component first. Another component's
 * thread means its address space, its kernel stack and its TLS; a component with no ready thread but a waiting or
 * suspended one keeps the processor idle until a tick or a device line changes something. The component whose last
 * thread exited ended in wit_user_exit_thread. The frame to resume, or zero when no thread is ready; *waiting says
 * whether a thread waits, is suspended or runs on another processor (P1.3b), which a processor that idles waits for. */
static WitArchFrame *pick(int timer, int rotate, int *waiting)
{
    Dispatcher *const cpu = self();
    expire_waits();
    const WitU32 first = cpu->Current ? cpu->Current->Slot : 0;
    const WitU32 start = rotate ? 1U : 0U;
    for (WitU32 n = start; n < start + WIT_PROCESS_CAPACITY; ++n) {
        WitUserProcess *q = slot_owners[(first + n) % WIT_PROCESS_CAPACITY];
        if (!q || q->State != WitUserRunning) {
            continue;
        }
        const WitU32 previous = q->CurrentThread;
        for (WitU32 offset = 1; offset <= q->ThreadTop; ++offset) {
            const WitU32 index = (previous + offset) % q->ThreadTop;
            WitUserThread *thread = &q->Threads[index];
            /* A ready thread whose affinity leaves this processor out waits for another (P1.4). */
            const int elsewhere = thread->State == WitThreadReady && !(thread->Affinity & (1ULL << me()));
            if (thread->State == WitThreadWaiting ||
                thread->State == WitThreadRunning ||
                thread->SuspendCount ||
                elsewhere) {
                *waiting = 1;
            }
            if (thread->State != WitThreadReady || thread->SuspendCount || elsewhere) {
                continue;
            }
            if (q != cpu->Current) {
                WitUserProcess *const left = cpu->Current;
                wit_arch_space_switch(q->Space.Root);
                cpu->Current = q;
                if (left && left->State != WitUserRunning && left->Pooled && !left->Retired && !active_anywhere(left)) {
                    retire(left); /* the last processor that had it active left it (P1.3b) */
                }
            }
            validate_return(thread->Context, index, 0);
            if (index != previous) {
                ++q->ThreadSwitches;
                if (timer) {
                    ++q->ThreadTimerSwitches;
                }
            }
            q->CurrentThread = index;
            cpu->Thread = index;
            thread->State = WitThreadRunning;
            thread->Processor = me();
            wit_kernel_stack_running(q->Slot, index);
            wit_arch_select_thread_stack(q->Slot, index);
            wit_arch_set_user_tls(thread->Tls);
            return resume_current(thread->Context);
        }
    }
    return 0;
}

/* The processor's idle path (P1.1), on its own stack: it halts until an interrupt, then looks for a ready thread again
 * as the dispatch that sent it here looked; a tick, a device line or a doorbell returns to the halt, and a ready
 * thread's frame resumes from here, which leaves the idle stack. The nested interrupt never replaces a saved user
 * context. A component that ended under the processor is left first (P1.3b); once the run ended the boot processor
 * ends it, and a secondary parks until the next run, its tick stopped. */
WIT_NORETURN static void idle(void)
{
    Dispatcher *const cpu = self();
    for (;;) {
        cpu->Yielding = 0; /* a yield that went idle let others run first */
        wit_kernel_stack_running(WIT_PROCESS_CAPACITY, 0); /* the idle stack (P1.5) */
        drop_component(cpu);
        wit_kernel_stacks_collect();
        cpu->Kicked = 0;
        if (run_active()) {
            if (me() != 0 && !cpu->Ticking) {
                wit_platform_timer_start();
                cpu->Ticking = 1;
            }
            cpu->Parked = 0;
            int waiting = 0;
            WitArchFrame *const frame = pick(cpu->IdleTimer, cpu->IdleRotate, &waiting);
            if (frame) {
                wit_arch_resume_frame(frame); /* the lock goes on the frame's stack */
            }
            require(waiting, "No thread of any component to run or to wait for");
        } else if (me() == 0) {
            finish_run();
        } else {
            if (cpu->Ticking) {
                wit_platform_timer_stop();
                cpu->Ticking = 0;
            }
            cpu->Parked = 1;
        }
        require(!wit_arch_interrupts_enabled(), "Idle entered with interrupts enabled");
        wit_arch_reset_user_tls();
        kick_idle(); /* a ready thread this processor may not run goes to one that idles (P1.4) */
        cpu->Idle = 1;
        if (cpu->Current) {
            ++cpu->Current->IdleHalts;
        }
        wit_cpus_unlock(); /* a tick or a line that wakes the halt takes it on its own entry */
        wit_arch_idle_once();
        wit_cpus_lock();
        cpu->Idle = 0;
    }
}

static WitArchFrame *dispatch(int timer, int rotate)
{
    if (run_active()) {
        int waiting = 0;
        WitArchFrame *const frame = pick(timer, rotate, &waiting);
        if (frame) {
            return frame;
        }
        require(waiting, "No thread of any component to run or to wait for");
    }
    Dispatcher *const cpu = self();
    cpu->IdleTimer = timer;
    cpu->IdleRotate = rotate;
    wit_arch_run_on_stack(idle_high(), idle);
}

/* The processor's thread ended with its component, which another processor ended while the thread ran or entered the
 * kernel (P1.3b): its frame goes, the processor leaves the component and dispatches. */
static WitArchFrame *abandon(int timer)
{
    Dispatcher *const cpu = self();
    cpu->Idle = 0;
    drop_component(cpu);
    return dispatch(timer, 0);
}

WIT_NORETURN void wit_user_processor_main(void)
{
    require(me() != 0 && !wit_arch_interrupts_enabled(), "A secondary processor enters its dispatcher out of context");
    wit_cpus_lock();
    self()->Current = 0;
    self()->Parked = 1;
    wit_arch_run_on_stack(idle_high(), idle);
}

WitArchFrame *wit_user_exit_thread(WitU64 code, WitU64 reservation, WitU64 clear, WitU64 event)
{
    static const WitU8 zero[4] = {0};
    const WitU32 index = self()->Current->CurrentThread;
    WitUserThread *thread = &self()->Current->Threads[index];
    thread->ExitReservation = thread->OwnsStack ? 0 : reservation;
    thread->ExitClear = clear;
    thread->ExitEvent = event;
    wit_user_exception_clear(thread);
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
            wit_user_copy_to(&self()->Current->Space, clear, zero, sizeof(zero)), "Exit word vanished before the exit");
    }
    if (event) {
        require(wit_user_event_set(self()->Current, event) == WIT_STATUS_OK, "Exit event vanished before the exit");
    }
    reap(index);
    for (WitU32 i = 0; i < WIT_PROCESS_THREAD_CAPACITY; ++i) {
        if (self()->Current->Threads[i].State != WitThreadEmpty) {
            return dispatch(0, 0);
        }
    }
    wit_user_finish(WitUserExited, code);
}

WitU64 wit_user_close_handle(WitU64 handle)
{
    const WitU64 reference = wit_user_reference_close(self()->Current, handle);
    if (reference != WIT_STATUS_WRONG_TYPE) {
        return reference;
    }
    const WitU64 channel = wit_user_channel_close(self()->Current, handle);
    if (channel != WIT_STATUS_WRONG_TYPE) {
        return channel;
    }
    const WitU64 object = wit_user_memory_object_close(self()->Current, handle);
    if (object != WIT_STATUS_WRONG_TYPE) {
        return object;
    }
    const WitU64 device = wit_user_device_close(self()->Current, handle);
    if (device != WIT_STATUS_WRONG_TYPE) {
        return device;
    }
    const WitU64 interrupt = wit_user_interrupt_close(self()->Current, handle);
    if (interrupt != WIT_STATUS_WRONG_TYPE) {
        return interrupt;
    }
    const WitU64 pin = wit_user_pin_close(self()->Current, handle);
    if (pin != WIT_STATUS_WRONG_TYPE) {
        return pin;
    }
    const WitU64 process_status = wit_user_process_close(self()->Current, handle);
    if (process_status != WIT_STATUS_WRONG_TYPE) {
        return process_status;
    }
    /* A thread's private identity is not a capability user space can release: it ends with the thread. */
    const WitU64 status = wit_handle_check(&self()->Current->Handles, handle, WIT_HANDLE_THREAD_IDENTITY, 0);
    if (status == WIT_STATUS_OK) {
        return WIT_STATUS_BUSY;
    }
    if (status != WIT_STATUS_WRONG_TYPE) {
        return status;
    }
    const WitU64 event_status = wit_user_event_close(self()->Current, handle);
    return event_status == WIT_STATUS_WRONG_TYPE ? wit_handle_close(&self()->Current->Handles, handle) : event_status;
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
        !self()->Current &&
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

/* Gives a fresh component its identity, the fixture profile's quotas and empty threads, objects and handles. */
static void reset_process(WitUserProcess *process, WitU32 slot)
{
    process->Id = next_id++;
    process->Slot = slot;
    process->State = WitUserEmpty;
    reset_counters(process);
    process->Ticks = 0;
    process->TickLimit = WIT_USER_TICK_BUDGET;
    process->ObjectLimit = WIT_MEMORY_OBJECT_CAPACITY;
    process->ThreadLimit = WIT_USER_THREAD_CAPACITY;
    process->ChannelLimit = WIT_USER_CHANNEL_CAPACITY;
    process->ExitCode = 0;
    process->ImageBase = 0;
    process->ImageEntry = 0;
    process->ImageSize = 0;
    process->FaultVector = 0;
    process->FaultError = 0;
    process->FaultAddress = 0;
    process->FaultState = (WitArchFaultState){0};
    process->CurrentThread = 0;
    process->FaultThread = NO_THREAD;
    process->NextWaitOrder = 0;
    process->MemoryPressureLow = 0;
    process->MemoryPressureCount = 0;
    for (WitU32 i = 0; i < WIT_PROCESS_EVENT_CAPACITY; ++i) {
        process->MemoryPressureEvents[i] = 0;
    }
    wit_user_interrupts_reset(process);
    wit_user_pins_reset(process);
    process->InterruptsDelivered = 0;
    wit_events_initialize(&process->Events);
    require(wit_memory_objects_charged(process) == 0 && wit_channels_charged(process) == 0,
        "A fresh component inherits memory objects or channels");
    wit_user_devices_reset(process);
    for (WitU32 i = 0; i < WIT_PROCESS_THREAD_CAPACITY; ++i) {
        process->Threads[i].State = WitThreadEmpty;
        process->Threads[i].SuspendCount = 0;
    }
    process->ThreadTop = 0;
    wit_handles_initialize(&process->Handles, process->Id);
    wit_user_references_initialize(process);
    wit_user_exception_initialize(process);
}

/* A component from a validated flat image (K4; the fixtures too since K8.4c): its segments mapped at their addresses
 * and copied page by page, the startup page and the data pages, and the first thread at the entry with WIT_USER_INFO
 * in its argument register; the caller fills the startup descriptor. The system layer's profile (the root task,
 * S1.3) has the system layer's pages and the wider fixed window, as a created process gets them, so that a libc
 * program and the runtime fit, and the system layer's reservation table (S5.4), threads, handles and events (K5.3)
 * and objects (S6.1); a fixture keeps the default quotas its tests exhaust, and its segments must lie in the fixed
 * window of that profile. */
int wit_user_create_flat(
    WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot, const WitFlatLayout *layout, int system)
{
    if (!can_create(process, slot)) {
        return 0;
    }
    reset_process(process, slot);
    if (system) {
        process->Handles.Limit = WIT_PROCESS_HANDLE_CAPACITY;
        process->Events.Limit = WIT_PROCESS_EVENT_CAPACITY;
        process->ThreadLimit = WIT_PROCESS_THREAD_CAPACITY;
        process->ChannelLimit = WIT_PROCESS_CHANNEL_CAPACITY;
        /* An hour of the machine's time: every processor's tick charges the component it runs (P1.3b), so a process
         * busy on all of them spends its budget as many times faster (P2). */
        process->TickLimit = WIT_PROCESS_TICK_BUDGET * wit_processors_online();
    }
    slot_owners[slot] = process;
    process->ImageBase = layout->Segments[0].Address;
    process->ImageEntry = layout->Entry;
    process->ImageSize = 0;
    if (!wit_user_space_create_profile(&process->Space, allocator, system) ||
        !wit_user_space_map(&process->Space, WIT_USER_INFO, 0, 0)) {
        goto failed;
    }
    if (system) {
        process->ObjectLimit = WIT_PROCESS_OBJECT_CAPACITY;
    }
    for (WitU32 i = 0; i < layout->SegmentCount; ++i) {
        if (layout->Segments[i].Address >= process->Space.FixedLimit ||
            layout->Segments[i].MemorySize > process->Space.FixedLimit - layout->Segments[i].Address) {
            goto failed;
        }
    }
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
    if (wit_user_prepare_thread(process, layout->Entry, WIT_USER_INFO) != WIT_STATUS_OK) {
        goto failed;
    }
    process->State = WitUserReady;
    return 1;
failed:
    wit_user_destroy(process);
    return 0;
}

/* An empty process for PROCESS_CREATE (K5.2c): a record of the pool in a free registry slot, the system layer's
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
    reset_process(process, slot);
    process->Handles.Limit = WIT_PROCESS_HANDLE_CAPACITY; /* the system layer's threads, handles and events (K5.3) */
    process->Events.Limit = WIT_PROCESS_EVENT_CAPACITY;
    process->ThreadLimit = WIT_PROCESS_THREAD_CAPACITY;
    process->ChannelLimit = WIT_PROCESS_CHANNEL_CAPACITY; /* and channels (N4.2) */
    process->TickLimit = ticks;
    slot_owners[slot] = process;
    if (!wit_user_space_create_profile(&process->Space, allocator, 1)) {
        wit_user_destroy(process);
        return 0;
    }
    process->Space.PageLimit = pages;
    process->ObjectLimit = WIT_PROCESS_OBJECT_CAPACITY; /* the system layer's objects (S6.1) */
    process->State = WitUserRunning;
    return 1;
}

void wit_user_run(WitUserProcess *process)
{
    require(!self()->Current &&
            !root_user &&
            !self()->Idle &&
            process->State == WitUserReady &&
            slot_owners[process->Slot] == process &&
            !wit_arch_interrupts_enabled() &&
            wit_arch_user_tls_is_reset(),
        "Invalid user launch");
    require(wit_user_processes_pooled() == 0 && me() == 0, "A created process survived the previous run");
    wit_cpus_lock();
    root_user = process;
    self()->Current = process;
    self()->Thread = 0;
    process->State = WitUserRunning;
    process->Threads[0].State = WitThreadRunning;
    process->Threads[0].Processor = 0;
    process->Ticks = 0;
    wit_arch_select_thread_stack(process->Slot, 0);
    wit_kernel_stack_running(process->Slot, 0);
    wit_platform_timer_start();
    wit_arch_set_user_tls(process->Threads[0].Tls);
    for (WitU32 i = 1; i < WIT_PROCESSOR_CAPACITY; ++i) {
        if (wit_cpus_is_online(i)) {
            wit_cpus_request(i, WIT_REQUEST_RESCHEDULE); /* the secondaries join the run (P1.3b) */
        }
    }
    wit_arch_run_user(process->Threads[0].Context, process->Space.Root); /* the lock goes on the frame's stack */
    wit_kernel_stack_running(WIT_PROCESS_CAPACITY, 0);
    wit_kernel_stacks_collect(); /* every processor parked or on the host's stack: every stack the run released goes */
    wit_cpus_release(); /* the run's end returned here with it, on the host's stack */
    require(!self()->Current &&
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
    require(!active_anywhere(process) && process->State != WitUserRunning, "Destroying running component");
    /* A component torn down without an exit still holds its references; the order is the exit's (K5.2b). */
    release_references(process);
    teardown(process);
    unregister(process);
    process->State = WitUserEmpty;
    wit_kernel_stacks_collect(); /* between runs: no processor runs on its stacks (P1.5) */
}

int wit_user_is_active(void)
{
    return root_user != 0;
}

WitUserProcess *wit_user_current(void)
{
    return self()->Current;
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

/* THREAD_YIELD: its value, 1 when another thread was picked, goes into the frame before the dispatch, since a dispatch
 * that picks the caller again delivers a pending activation into the same frame, whose callback's arguments a later
 * write would overwrite (P2: on two of the three ISAs the value register is the callback's vector register). */
WitArchFrame *wit_user_yield(WitU64 *value)
{
    Dispatcher *const cpu = self();
    WitUserThread *const caller = &cpu->Current->Threads[cpu->Current->CurrentThread];
    caller->State = WitThreadReady;
    *value = 1;
    cpu->Yielding = caller;
    WitArchFrame *const next = dispatch(0, 1);
    cpu->Yielding = 0;
    return next;
}

WitArchFrame *wit_user_timer_tick(WitArchFrame *context)
{
    Dispatcher *const cpu = self();
    WitUserProcess *const current = enter();
    if (cpu->Idle) {
        require(
            wit_arch_frame_is_idle(context, idle_low(), idle_high()), "Timer did not interrupt the kernel idle path");
        if (current) {
            ++current->IdleTicks;
        }
    } else if (!current || current->State != WitUserRunning) {
        return leave(abandon(1)); /* another processor ended the component (P1.3b) */
    } else {
        WitUserThread *thread = &current->Threads[current->CurrentThread];
        validate_return(context, current->CurrentThread, 0);
        thread->Context = context;
        thread->State = WitThreadReady;
    }
    expire_waits();
    /* The budget is the running component's; a tick of the idle path charges the component that went idle only when
     * nothing can wake the processor (idle_can_wake). Each processor's tick charges its own (P1.3b). */
    if (current &&
        (!cpu->Idle || !idle_can_wake()) &&
        current->State == WitUserRunning &&
        ++current->Ticks >= current->TickLimit) {
        wit_user_finish(WitUserBudgetExpired, 0);
    }
    if (cpu->Idle) {
        return leave(context); /* Resume CLI/RET on the idle stack and look for ready threads again. */
    }
    return leave(dispatch(1, 1));
}

/* A device line, masked and completed by the architecture: the bound event of its owner is set and the ready threads
 * are dispatched as after a tick; without a run the line stays masked. Lines reach the boot processor alone (P1.2),
 * which may idle without a component during a run (P1.3b). */
WitArchFrame *wit_user_interrupt(WitArchFrame *context, WitU32 line)
{
    Dispatcher *const cpu = self();
    WitUserProcess *const current = enter();
    if (!root_user) {
        return leave(context);
    }
    int abandoned = 0;
    if (cpu->Idle) {
        require(wit_arch_frame_is_idle(context, idle_low(), idle_high()),
            "Device interrupt did not interrupt the kernel idle path");
    } else if (!current || current->State != WitUserRunning) {
        abandoned = 1; /* another processor ended the component (P1.3b) */
    } else {
        WitUserThread *thread = &current->Threads[current->CurrentThread];
        validate_return(context, current->CurrentThread, 0);
        thread->Context = context;
        thread->State = WitThreadReady;
    }
    wit_user_interrupt_raised(line); /* To the binding's owner, whichever component that is (K5.2c). */
    expire_waits();
    if (abandoned) {
        return leave(abandon(1));
    }
    if (cpu->Idle) {
        return leave(context); /* Resume CLI/RET on the idle stack and look for ready threads again. */
    }
    return leave(dispatch(1, 0));
}

WitArchFrame *wit_user_syscall(
    WitArchFrame *context, WitU64 number, WitU64 argument0, WitU64 argument1, WitU64 argument2)
{
    WitUserProcess *const current = enter();
    if (!current || current->State != WitUserRunning) {
        return leave(abandon(0)); /* another processor ended the component (P1.3b) */
    }
    validate_return(context, self()->Current->CurrentThread, 1);
    expire_waits();
    self()->Current->Threads[self()->Current->CurrentThread].Context = context;
    WitUserCall call = {self()->Current, context, number, argument0, argument1, argument2,
        wit_arch_frame_status(context), wit_arch_frame_value(context)};
    wit_arch_frame_set_result(context, WIT_STATUS_OK, 0);
    WitArchFrame *const resume = wit_user_call(&call);
    if (resume) {
        return leave(resume_current(resume));
    }
    if (*call.Status == WIT_USER_CALL_RESTART) {
        /* The call runs again once the caller is dispatched anew (P1.3b): its target is leaving another processor. */
        wit_arch_frame_restart_call(context, number, argument0, argument1, argument2);
        self()->Current->Threads[self()->Current->CurrentThread].State = WitThreadReady;
        return leave(dispatch(0, 1));
    }
    context = call.Context; /* A join may have switched the current thread. */
    // Publish settled memory pressure, after expiring both deadline domains.
    // A finite wait can complete here before this syscall returns; dispatch its
    // Ready state normally instead of returning with an inconsistent state.
    expire_waits();
    /* A suspended caller, or one whose affinity left this processor out (P1.4), leaves it. */
    WitUserThread *const caller = &self()->Current->Threads[self()->Current->CurrentThread];
    if (caller->State == WitThreadRunning && (caller->SuspendCount || !(caller->Affinity & (1ULL << me())))) {
        caller->State = WitThreadReady;
    }
    if (self()->Current->Threads[self()->Current->CurrentThread].State != WitThreadRunning) {
        return leave(dispatch(0, 0));
    }
    wit_arch_set_user_tls(self()->Current->Threads[self()->Current->CurrentThread].Tls);
    return leave(resume_current(context));
}

WitArchFrame *wit_user_exception_trap(WitArchFrame *context, WitU64 vector, WitU64 error, WitU64 address)
{
    WitUserProcess *const current = enter();
    if (!current || current->State != WitUserRunning) {
        return leave(abandon(0)); /* another processor ended the component (P1.3b) */
    }
    require(self()->Current &&
            self()->Current->State == WitUserRunning &&
            wit_arch_frame_owned(context, self()->Current->Slot, self()->Current->CurrentThread) &&
            wit_arch_frame_from_user(context),
        "Invalid resumable user fault context");
    WitUserThread *thread = &self()->Current->Threads[self()->Current->CurrentThread];
    thread->Context = context;
    if (wit_user_exception_deliver(self()->Current, context, vector, error, address)) {
        validate_return(context, self()->Current->CurrentThread, 0);
        wit_arch_set_user_tls(thread->Tls);
        return leave(context);
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
    WitUserProcess *const current = enter();
    if (!current || current->State != WitUserRunning) {
        wit_arch_resume_frame(leave(abandon(0))); /* another processor ended the component (P1.3b) */
    }
    require(self()->Current != 0 &&
            wit_arch_kernel_stack_contains(self()->Current->Slot, self()->Current->CurrentThread, trap, trap_size),
        "Invalid user fault frame");
    wit_user_fault_state(vector, error, address, state);
}

WitArchFrame *wit_user_reschedule(WitArchFrame *context)
{
    Dispatcher *const cpu = self();
    WitUserProcess *const current = enter();
    if (cpu->Idle) {
        return leave(context); /* the idle path looks again after the interrupt */
    }
    if (!current || current->State != WitUserRunning) {
        return leave(abandon(0)); /* another processor ended the component */
    }
    WitUserThread *thread = &current->Threads[current->CurrentThread];
    validate_return(context, current->CurrentThread, 0);
    thread->Context = context;
    thread->State = WitThreadReady;
    return leave(dispatch(0, 0));
}

WIT_NORETURN void wit_user_fault_state(WitU64 vector, WitU64 error, WitU64 address, const WitArchFaultState *state)
{
    require(self()->Current != 0 && wit_arch_fault_from_user(state), "Invalid user fault state");
    self()->Current->FaultThread = self()->Current->CurrentThread;
    self()->Current->FaultVector = vector;
    self()->Current->FaultError = error;
    self()->Current->FaultAddress = address;
    self()->Current->FaultState = *state;
    ++contained_faults;
    wit_console_write("[USER-FAULT] id=");
    wit_console_write_u64(self()->Current->Id);
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
