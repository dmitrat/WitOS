#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "user_wait_image.h"

static WitUserProcess processes[2];
static WitUserProcess model;
static WitInterruptContext model_contexts[WIT_USER_THREAD_CAPACITY];

static void require(int condition, const char *message)
{
    if (!condition) wit_panic(message);
}

static WitU64 make_event(WitUserProcess *process, WitU64 flags, WitU32 rights)
{
    WitU64 handle = 0;
    require(wit_event_create(&process->Events, &process->Handles, flags, rights, &handle) == WIT_STATUS_OK,
        "Test event creation failed");
    return handle;
}

static void initialize_model(void)
{
    wit_handles_initialize(&model.Handles, 0xF00D);
    wit_events_initialize(&model.Events);
    model.NextWaitOrder = 0;
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        model.Threads[i].Context = &model_contexts[i];
        model.Threads[i].State = WitThreadRunning;
        model.Threads[i].WaitKind = WitWaitNone;
    }
}

static void park(WitU32 index, WitU64 handle, WitU64 deadline)
{
    model.CurrentThread = index;
    require(wit_user_event_wait(&model, handle, deadline, 100) == WIT_STATUS_OK &&
        model.Threads[index].State == WitThreadWaiting, "Wait fixture did not park");
}

static void queue_semantics(void)
{
    WitU64 handle, replacement;
    initialize_model();
    handle = make_event(&model, 0, WIT_RIGHT_WAIT | WIT_RIGHT_SIGNAL);
    park(2, handle, WIT_WAIT_INFINITE);
    park(0, handle, WIT_WAIT_INFINITE);
    park(1, handle, WIT_WAIT_INFINITE);
    require(wit_user_event_set(&model, handle) == WIT_STATUS_OK &&
        model.Threads[2].State == WitThreadReady && model.Threads[0].State == WitThreadWaiting &&
        model.Threads[1].State == WitThreadWaiting, "Auto event did not select first parked waiter");
    model.CurrentThread = 3;
    require(wit_user_event_wait(&model, handle, 0, 100) == WIT_STATUS_TIMED_OUT,
        "Another thread stole a claimed wakeup");
    require(wit_user_event_set(&model, handle) == WIT_STATUS_OK &&
        model.Threads[0].State == WitThreadReady && model.Threads[1].State == WitThreadWaiting,
        "Auto event FIFO order failed");
    require(wit_user_event_close(&model, handle) == WIT_STATUS_OK &&
        model.Threads[1].State == WitThreadReady && model_contexts[1].Rax == WIT_STATUS_CLOSED,
        "Closing event did not cancel pending wait");
    replacement = make_event(&model, WIT_EVENT_INITIAL_SIGNALED, WIT_RIGHT_WAIT | WIT_RIGHT_SIGNAL);
    require(replacement != handle && wit_user_event_set(&model, handle) == WIT_STATUS_BAD_HANDLE &&
        model_contexts[1].Rax == WIT_STATUS_CLOSED, "Reused event changed an old completion");

    initialize_model();
    handle = make_event(&model, WIT_EVENT_MANUAL_RESET, WIT_RIGHT_WAIT | WIT_RIGHT_SIGNAL);
    park(0, handle, 102);
    park(1, handle, 102);
    park(2, handle, 103);
    wit_user_wait_expire(&model, 101);
    require(model.Threads[0].State == WitThreadWaiting, "Wait expired before deadline");
    wit_user_wait_expire(&model, 102);
    require(model_contexts[0].Rax == WIT_STATUS_TIMED_OUT &&
        model_contexts[1].Rax == WIT_STATUS_TIMED_OUT && model.Threads[2].State == WitThreadWaiting,
        "Exact deadline handling failed");
    require(wit_user_event_set(&model, handle) == WIT_STATUS_OK &&
        model_contexts[2].Rax == WIT_STATUS_OK && model_contexts[0].Rax == WIT_STATUS_TIMED_OUT,
        "Signal overwrote an expired completion");
    require(wit_user_event_reset(&model, handle) == WIT_STATUS_OK && model_contexts[2].Rax == WIT_STATUS_OK,
        "Reset revoked a claimed completion");

    for (WitU32 i = 0; i < 3; ++i) park(i, handle, WIT_WAIT_INFINITE);
    require(wit_user_event_set(&model, handle) == WIT_STATUS_OK, "Manual signal failed");
    for (WitU32 i = 0; i < 3; ++i)
        require(model.Threads[i].State == WitThreadReady && model_contexts[i].Rax == WIT_STATUS_OK,
            "Manual event failed to wake every waiter");
    model.CurrentThread = 3;
    require(wit_user_event_wait(&model, handle, 0, 100) == WIT_STATUS_OK,
        "Manual signal was not persistent");
    wit_handles_close_all(&model.Handles);
    wit_events_initialize(&model.Events);
    wit_console_write("[TEST-PASS] User.WaitQueueSemantics\n");
}

static void resource_limits(void)
{
    WitU64 handles[WIT_HANDLE_CAPACITY], result;
    WitU32 count = 0;
    initialize_model();
    for (WitU32 i = 0; i < WIT_EVENT_CAPACITY; ++i)
        handles[i] = make_event(&model, 0, WIT_RIGHT_WAIT | WIT_RIGHT_SIGNAL);
    require(wit_event_create(&model.Events, &model.Handles, 0, WIT_RIGHT_WAIT, &result) == WIT_STATUS_NO_MEMORY &&
        result == 0 && model.Events.Count == WIT_EVENT_CAPACITY &&
        model.Handles.Count == WIT_EVENT_CAPACITY, "Event quota failure changed ownership");
    for (WitU32 i = 0; i < WIT_EVENT_CAPACITY; ++i)
        require(wit_user_event_close(&model, handles[i]) == WIT_STATUS_OK, "Event quota cleanup failed");
    while (model.Handles.Count < WIT_HANDLE_CAPACITY)
        handles[count++] = wit_handle_grant(&model.Handles, WIT_HANDLE_SELF, 0);
    require(wit_event_create(&model.Events, &model.Handles, 0, WIT_RIGHT_WAIT, &result) == WIT_STATUS_NO_MEMORY &&
        result == 0 && model.Events.Count == 0, "Handle exhaustion leaked event");
    while (count) require(wit_handle_close(&model.Handles, handles[--count]) == WIT_STATUS_OK, "Fixture close failed");
    handles[0] = make_event(&model, 0, WIT_RIGHT_WAIT);
    require(wit_user_event_close(&model, handles[0]) == WIT_STATUS_OK && model.Events.Count == 0 &&
        model.Handles.Count == 0, "Event creation did not recover");
    wit_console_write("[TEST-PASS] User.WaitResourceLimits\n");
}

static WitUserTestConfig *create(WitPageAllocator *pages, WitU32 slot, WitU64 mode)
{
    WitUserTestConfig *info;
    require(wit_user_create(&processes[slot], pages, slot, wit_user_wait_image, sizeof(wit_user_wait_image)),
        "Wait process creation failed");
    info = (WitUserTestConfig *)wit_user_space_physical(&processes[slot].Space, WIT_USER_INFO, 0, 0);
    info->Mode = mode;
    return info;
}

void wit_user_wait_self_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    static const char *names[] = {
        "WaitSignalState", "WaitClockAndIdle", "WaitAutoWake", "WaitManualWake",
        "WaitCloseAndReuse", "WaitHandoff", "WaitDeadlineOrder", "WaitExitCleanup",
        "WaitIdleBudget", "WaitRights", "WaitActiveTimeout", "WaitJoinChain"
    };
    WitUserProcess *process = &processes[0];
    queue_semantics();
    resource_limits();
    for (WitU32 mode = 0; mode < sizeof(names) / sizeof(names[0]); ++mode) {
        WitU64 foreign = 0, clock_before = wit_x64_clock_ticks();
        WitUserTestConfig *info = create(pages, 0, mode);
        wit_console_write("[TEST-BEGIN] User.");
        wit_console_write(names[mode]);
        wit_console_write("\n");
        if (mode == WIT_WAIT_TEST_RIGHTS) {
            WitEvent *event;
            create(pages, 1, WIT_WAIT_TEST_SIGNAL_STATE);
            foreign = make_event(&processes[1], 0, WIT_RIGHT_WAIT | WIT_RIGHT_SIGNAL);
            info->ForeignHandle = foreign;
            info->ReadOnlyHandle = make_event(process, 0, WIT_RIGHT_WAIT);
            info->SelfHandle = make_event(process, 0, WIT_RIGHT_SIGNAL);
            require(wit_event_get(&processes[1].Events, &processes[1].Handles, foreign, 0, &event) == WIT_STATUS_OK &&
                !event->Signaled, "Foreign event setup failed");
        }
        wit_user_run(process);
        if (mode == WIT_WAIT_TEST_BUDGET) {
            require(process->State == WitUserBudgetExpired && process->Ticks == WIT_USER_TICK_BUDGET &&
                process->IdleTicks != 0, "Infinite idle wait escaped test budget");
        } else {
            if (process->State != WitUserExited || process->ExitCode != WIT_TEST_EXIT_CODE) {
                wit_console_write("Wait state/code: ");
                wit_console_write_u64(process->State);
                wit_console_write("/");
                wit_console_write_u64(process->ExitCode);
                wit_console_write("\n");
                wit_panic("User wait fixture failed");
            }
        }
        require(wit_x64_clock_ticks() >= clock_before && process->Handles.Count == 0 &&
            process->Events.Count == 0, "Wait teardown/clock failed");
        if (mode == WIT_WAIT_TEST_CLOCK)
            require(process->IdleTicks >= 1 && process->WaitTimeouts == 1, "Clock wait did not use idle/timer");
        if (mode == WIT_WAIT_TEST_AUTO || mode == WIT_WAIT_TEST_MANUAL)
            require(process->EventParks == 3 && process->EventWakes == 3 &&
                process->ThreadJoins == 3 && process->Space.OwnedCount == 13, "Event wake/join accounting failed");
        if (mode == WIT_WAIT_TEST_CLOSE)
            require(process->WaitCloses == 3 && process->ThreadJoins == 3, "Close did not wake blocked threads");
        if (mode == WIT_WAIT_TEST_HANDOFF)
            require(process->EventParks >= 32 && process->EventParks == process->EventWakes &&
                process->ThreadJoins == 1, "Event handoff lost wakeups");
        if (mode == WIT_WAIT_TEST_DEADLINE)
            require(process->WaitTimeouts == 1 && process->EventWakes == 0 &&
                process->IdleTicks >= 1, "Deadline did not precede signal");
        if (mode == WIT_WAIT_TEST_EXIT)
            require(process->EventParks == 3 && process->ThreadJoins == 0, "Exit-with-waiters setup failed");
        if (mode == WIT_WAIT_TEST_ACTIVE_TIMEOUT)
            require(process->WaitTimeouts == 1 && process->IdleTicks == 0 &&
                process->ThreadTimerSwitches != 0, "Active timeout did not use user preemption");
        if (mode == WIT_WAIT_TEST_JOIN_CHAIN)
            require(process->ThreadJoins == 2 && process->ThreadDeadlocks == 0 && process->IdleTicks >= 1,
                "Join/event/sleep chain did not wake");
        if (mode == WIT_WAIT_TEST_RIGHTS) {
            WitEvent *event;
            require(wit_event_get(&processes[1].Events, &processes[1].Handles, foreign, 0, &event) == WIT_STATUS_OK &&
                !event->Signaled, "Foreign component changed an event");
            wit_user_destroy(&processes[1]);
        }
        wit_user_destroy(process);
        require(wit_pages_free_count(pages) == before, "Wait scenario leaked physical pages");
        wit_console_write("[TEST-PASS] User.");
        wit_console_write(names[mode]);
        wit_console_write("\n");
    }
}
