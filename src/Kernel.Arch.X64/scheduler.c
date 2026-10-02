#include "x64.h"
#include "user.h"
#include "witos/platform.h"

unsigned __int64 __readmsr(unsigned long);
void __writemsr(unsigned long, unsigned __int64);
void __halt(void);
void _enable(void);
void _disable(void);
#pragma intrinsic(__readmsr, __writemsr, __halt, _enable, _disable)

static volatile WitU64 timer_ticks;

#if defined(WITOS_SELFTEST)
/* Kernel-worker preemption test: two workers on private kernel stacks and the bootstrap context. */
volatile WitU64 wit_worker_iterations[2];
volatile WitU64 wit_worker_slices[2];
volatile WitU64 wit_worker_done[2];
volatile WitU64 wit_worker_errors[2];
volatile WitU32 wit_worker_mxcsr[2];
static WitInterruptContext *saved[3];
static volatile WitU32 scheduling;
static WitU32 current = 2;
static WitU64 switches;
#endif

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

WitU64 wit_arch_clock_ticks(void)
{
    return timer_ticks;
}

#if defined(WITOS_SELFTEST)
/* Kernel worker threads of the preemption self-test. */
WIT_NORETURN void wit_x64_thread_returned(void)
{
    wit_panic("Kernel thread unexpectedly returned");
}

static WitInterruptContext *prepare_thread(WitU32 index)
{
    const WitU64 top = (WitU64)wit_x64_worker_stacks[index] + 4096 + WIT_KERNEL_STACK_SIZE;
    WitInterruptContext *context = (WitInterruptContext *)(top - 4096);
    for (WitU32 i = 0; i < sizeof(*context); ++i) {
        ((WitU8 *)context)[i] = 0;
    }
    wit_x64_fxsave(context->FxState);
    context->Rcx = index;
    context->Rip = (WitU64)wit_x64_worker;
    context->Cs = 8;
    context->Rflags = 0x202;
    context->Rsp = top - 40; /* Return address plus the ABI shadow space. */
    context->Ss = 0x10;
    *(WitU64 *)context->Rsp = (WitU64)wit_x64_thread_returned;
    return context;
}
#endif

WitInterruptContext *wit_x64_timer_interrupt(WitInterruptContext *context)
{
    if (timer_ticks < WIT_WAIT_INFINITE - 1) {
        ++timer_ticks; /* Saturate; never wrap deadlines. */
    }
    wit_platform_timer_acknowledge(); /* Before dispatching a different context. */
    if (wit_user_is_active()) {
        return wit_user_timer_tick(context);
    }
#if defined(WITOS_SELFTEST)
    if (!scheduling) {
        return context;
    }

    const WitU64 low = (WitU64)(current == 2 ? wit_x64_kernel_stack : wit_x64_worker_stacks[current]) + 4096;
    const WitU64 high = low + WIT_KERNEL_STACK_SIZE;
    require((WitU64)context >= low &&
            (WitU64)context <= high - sizeof(*context) &&
            ((WitU64)context & 15) == 0 &&
            context->Rsp >= low &&
            context->Rsp < high,
        "Interrupt context outside owning stack");
    saved[current] = context;
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
    return context;
#endif
}

#if defined(WITOS_SELFTEST)
void wit_arch_scheduler_self_test(void)
{
    const WitU64 flags = wit_x64_read_flags();
    require((flags & 0x200) == 0, "Scheduler initialized with interrupts enabled");
    saved[0] = prepare_thread(0);
    saved[1] = prepare_thread(1);
    saved[2] = 0;
    scheduling = 1;
    current = 2;
    timer_ticks = 0;
    switches = 0;
    wit_console_write("[TEST-BEGIN] Scheduler.Preemption\n");
    wit_platform_timer_start();
    _enable();
    while (scheduling) {
        __halt();
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
