#include "user.h"
#include "witos/platform.h"

void wit_user_suspend_deadline_self_test(void)
{
    static WitUserProcess model;
    WitInterruptContext frame = {0};
    WitUserThread *thread = &model.Threads[0];
    thread->Context = &frame;
    thread->State = WitThreadWaiting;
    thread->WaitKind = WitWaitSleep;
    thread->Deadline = 10;
    thread->MonotonicWait = 1;
    thread->SuspendCount = 1;
    wit_user_wait_expire_time(&model, 10);
    if (thread->State != WitThreadReady ||
        thread->SuspendCount != 1 ||
        thread->WaitKind != WitWaitNone ||
        frame.Rax != WIT_STATUS_OK ||
        frame.Rdx) {
        wit_panic("Deadline changed suspension state");
    }
    thread->State = WitThreadWaiting;
    thread->WaitKind = WitWaitEvent;
    thread->Deadline = 20;
    thread->MonotonicWait = 1;
    wit_user_wait_expire_time(&model, 20);
    if (thread->State != WitThreadReady ||
        thread->SuspendCount != 1 ||
        frame.Rax != WIT_STATUS_TIMED_OUT ||
        model.WaitTimeouts != 1) {
        wit_panic("Suspended event deadline did not complete");
    }
    thread->Context = 0;
    thread->State = WitThreadEmpty;
    thread->SuspendCount = 0;
    wit_console_write("[TEST-PASS] Cpu.SuspendedDeadlineState\n");
}
