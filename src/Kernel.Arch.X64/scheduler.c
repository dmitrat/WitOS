#include "x64.h"
#include "user.h"
#include "witos/platform.h"

void __outbyte(unsigned short, unsigned char);
unsigned __int64 __readmsr(unsigned long);
void __writemsr(unsigned long, unsigned __int64);
void __halt(void);
void _enable(void);
void _disable(void);
#pragma intrinsic(__outbyte, __readmsr, __writemsr, __halt, _enable, _disable)

volatile WitU64 wit_worker_iterations[2];
volatile WitU64 wit_worker_slices[2];
volatile WitU64 wit_worker_done[2];
volatile WitU64 wit_worker_errors[2];
volatile WitU32 wit_worker_mxcsr[2];

static WitInterruptContext *saved[3];
static volatile WitU32 scheduling;
static WitU32 current = 2;
static volatile WitU64 timer_ticks;
static WitU64 switches;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static void io_wait(void)
{
    __outbyte(0x80, 0);
}

WitU64 wit_x64_clock_ticks(void)
{
    return timer_ticks;
}

void wit_x64_timer_start(void)
{
    const WitU64 apic = __readmsr(0x1B);
    require((apic & (1ULL << 10)) == 0, "x2APIC is unsupported by the bootstrap timer");
    /* The controlled one-CPU PC backend uses the legacy PIC/PIT path.
     * Disable local APIC delivery so it does not retain firmware routing. */
    __writemsr(0x1B, apic & ~(1ULL << 11));
    __outbyte(0x21, 0xFF);
    __outbyte(0xA1, 0xFF);
    __outbyte(0x20, 0x11);
    io_wait();
    __outbyte(0xA0, 0x11);
    io_wait();
    __outbyte(0x21, 0x20);
    io_wait();
    __outbyte(0xA1, 0x28);
    io_wait();
    __outbyte(0x21, 0x04);
    io_wait();
    __outbyte(0xA1, 0x02);
    io_wait();
    __outbyte(0x21, 0x01);
    io_wait();
    __outbyte(0xA1, 0x01);
    io_wait();
    __outbyte(0x21, 0xFF);
    __outbyte(0xA1, 0xFF);
    /* Channel 0, low/high count, mode 2; approximately 100 Hz. */
    __outbyte(0x43, 0x34);
    __outbyte(0x40, (WitU8)(11932 & 255));
    __outbyte(0x40, (WitU8)(11932 >> 8));
    __outbyte(0x21, 0xFE); /* Only IRQ0. */
}

void wit_x64_timer_stop(void)
{
    _disable();
    __outbyte(0x21, 0xFF);
    __outbyte(0xA1, 0xFF);
}

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

WitInterruptContext *wit_x64_timer_interrupt(WitInterruptContext *context)
{
    WitU64 low;
    WitU64 high;
    if (timer_ticks < WIT_WAIT_INFINITE - 1) {
        ++timer_ticks; /* Saturate; never wrap deadlines. */
    }
    __outbyte(0x20, 0x20); /* EOI before dispatching a different context. */
    if (wit_user_is_active()) {
        return wit_user_timer_tick(context);
    }
    if (!scheduling) {
        return context;
    }

    low = (WitU64)(current == 2 ? wit_x64_kernel_stack : wit_x64_worker_stacks[current]) + 4096;
    high = low + WIT_KERNEL_STACK_SIZE;
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
}

void wit_scheduler_self_test(void)
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
    wit_x64_timer_start();
    _enable();
    while (scheduling) {
        __halt();
    }
    wit_x64_timer_stop();

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
