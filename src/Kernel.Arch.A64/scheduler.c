#include "witos/arch.h"
#include "witos/platform.h"
#include "witos/cpu.h"
#include "user.h"
#include "a64.h"

/* ARM64 timer interrupt: counts ticks, runs the user scheduler while a component is active and, in the preemption
 * self-test, switches between two kernel workers and the bootstrap thread. */

static volatile WitU64 timer_ticks;

#if defined(WITOS_SELFTEST)
/* Kernel-worker preemption test: two workers on private kernel stacks and the bootstrap context. */
volatile WitU64 wit_worker_iterations[2];
volatile WitU64 wit_worker_slices[2];
volatile WitU64 wit_worker_done[2];
volatile WitU64 wit_worker_errors[2];
static WitA64Frame *saved[3];
static volatile WitU32 scheduling;
static WitU32 current = 2;
static WitU64 switches;

void wit_a64_worker(WitU64 index);

/* EL1h with debug, SError and FIQ masked and IRQ enabled. */
#define WORKER_SPSR 0x345ULL

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}
#endif

WitU64 wit_arch_clock_ticks(void)
{
    return timer_ticks;
}

#if defined(WITOS_SELFTEST)
static WIT_NORETURN void thread_returned(void)
{
    wit_panic("Kernel thread unexpectedly returned");
}

static WitA64Frame *prepare_thread(WitU32 index)
{
    const WitU64 top = (WitU64)wit_a64_worker_stacks[index] + 4096 + WIT_A64_KERNEL_STACK_SIZE;
    WitA64Frame *frame = (WitA64Frame *)(top - WIT_A64_FRAME_SIZE);
    for (WitU32 i = 0; i < sizeof(*frame); ++i) {
        ((WitU8 *)frame)[i] = 0;
    }
    frame->X[0] = index;
    frame->X[30] = (WitU64)thread_returned;
    frame->Sp = top;
    frame->Elr = (WitU64)wit_a64_worker;
    frame->Spsr = WORKER_SPSR;
    return frame;
}

static WitU64 stack_begin(WitU32 thread)
{
    return (WitU64)(thread == 2 ? wit_a64_kernel_stack : wit_a64_worker_stacks[thread]) + 4096;
}
#endif

WitA64Frame *wit_a64_interrupt(WitA64Frame *frame)
{
    WitU32 line = 0;
    const int claimed = wit_platform_interrupt_claim(&line);
    if (claimed == 0) {
        return frame;
    }
    if (claimed == 2) {
        /* A device line: masked and completed here, delivered to the bound event by the kernel (K3.2). */
        wit_platform_line_mask(line);
        wit_platform_line_complete(line);
        return wit_user_interrupt(frame, line);
    }
    if (claimed == 3) {
        /* An inter-processor interrupt on a secondary processor (K7.2): served and acknowledged, then back to idle. */
        wit_cpus_ipi_received(line == 0 ? WIT_IPI_FENCE : WIT_IPI_INVALIDATE);
        wit_platform_ipi_complete(line);
        return frame;
    }
    if (timer_ticks < ~0ULL - 1) {
        ++timer_ticks; /* Saturate; never wrap deadlines. */
    }
    wit_platform_timer_acknowledge(); /* Before dispatching a different context. */
    if (wit_user_is_active()) {
        return wit_user_timer_tick(frame);
    }
#if defined(WITOS_SELFTEST)
    if (!scheduling) {
        return frame;
    }

    const WitU64 low = stack_begin(current);
    const WitU64 high = low + WIT_A64_KERNEL_STACK_SIZE;
    require((WitU64)frame >= low &&
            (WitU64)frame <= high - WIT_A64_FRAME_SIZE &&
            ((WitU64)frame & 15) == 0 &&
            frame->Sp == (WitU64)frame + WIT_A64_FRAME_SIZE,
        "Interrupt frame outside owning stack");
    saved[current] = frame;
    if (wit_worker_done[0] && wit_worker_done[1]) {
        scheduling = 0;
        current = 2;
        ++switches;
        require(saved[2] != 0, "Bootstrap context was not saved");
        return saved[2];
    }
    for (WitU32 offset = 1; offset <= 2; ++offset) {
        const WitU32 next = (current == 2 ? offset - 1 : (current + offset) % 2);
        if (!wit_worker_done[next]) {
            current = next;
            ++switches;
            ++wit_worker_slices[next];
            wit_console_write(next == 0 ? "A: " : "B: ");
            wit_console_write_u64(wit_worker_slices[next]);
            wit_console_write("\n");
            return saved[next];
        }
    }
    wit_panic("No runnable kernel context");
#else
    return frame;
#endif
}

#if defined(WITOS_SELFTEST)
void wit_arch_scheduler_self_test(void)
{
    require(!wit_arch_interrupts_enabled(), "Scheduler initialized with interrupts enabled");
    saved[0] = prepare_thread(0);
    saved[1] = prepare_thread(1);
    saved[2] = 0;
    scheduling = 1;
    current = 2;
    timer_ticks = 0;
    switches = 0;
    wit_console_write("[TEST-BEGIN] Scheduler.Preemption\n");
    wit_platform_timer_start();
    wit_a64_enable_interrupts();
    while (scheduling) {
        wit_a64_wait();
    }
    wit_platform_timer_stop();

    require(timer_ticks >= 7 && switches >= 7, "Timer or context switching stalled");
    require(wit_worker_done[0] &&
            wit_worker_done[1] &&
            wit_worker_slices[0] >= 3 &&
            wit_worker_slices[1] >= 3 &&
            switches == wit_worker_slices[0] + wit_worker_slices[1] + 1 &&
            wit_worker_iterations[0] != 0 &&
            wit_worker_iterations[1] != 0,
        "Worker progress failed");
    require(wit_worker_errors[0] == 0 && wit_worker_errors[1] == 0, "Context register or SIMD state corrupted");
    wit_console_write("Timer ticks: ");
    wit_console_write_u64(timer_ticks);
    wit_console_write("\nContext switches: ");
    wit_console_write_u64(switches);
    wit_console_write("\nWorker A iterations: ");
    wit_console_write_u64(wit_worker_iterations[0]);
    wit_console_write("\nWorker B iterations: ");
    wit_console_write_u64(wit_worker_iterations[1]);
    wit_console_write(
        "\n[TEST-PASS] Cpu.Timer\n[TEST-PASS] Scheduler.Preemption\n[TEST-PASS] Scheduler.RegisterState\n");
}
#endif
