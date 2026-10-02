#include <windows.h>
#include <stdio.h>
static HANDLE ready, leave;

static DWORD WINAPI worker(void *)
{
    SetEvent(ready);
    WaitForSingleObject(leave, INFINITE);
    return 42;
}

int main()
{
    ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    leave = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE thread = CreateThread(nullptr, 0, worker, nullptr, CREATE_SUSPENDED, nullptr);
    if (!ready || !leave || !thread) {
        return 1;
    }
    for (DWORD i = 1; i < MAXIMUM_SUSPEND_COUNT; ++i) {
        if (SuspendThread(thread) != i) {
            return 2;
        }
    }
    SetLastError(0);
    if (SuspendThread(thread) != (DWORD)-1) {
        return 3;
    }
    DWORD error = GetLastError();
    printf("overflow error=%lu\n", error);
    if (error != ERROR_SIGNAL_REFUSED) {
        return 4;
    }
    for (DWORD i = MAXIMUM_SUSPEND_COUNT; i > 0; --i) {
        if (ResumeThread(thread) != i) {
            return 5;
        }
    }
    if (WaitForSingleObject(ready, INFINITE) != WAIT_OBJECT_0 || ResumeThread(thread) != 0) {
        return 6;
    }
    SetEvent(leave);
    if (WaitForSingleObject(thread, INFINITE) != WAIT_OBJECT_0) {
        return 7;
    }
    CloseHandle(thread);
    CloseHandle(leave);
    CloseHandle(ready);
    puts("PASS: Windows suspend count and overflow reference");
    return 0;
}
