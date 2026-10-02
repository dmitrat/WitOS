#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>
static volatile LONG attached, detached, processDetached;

static DWORD WINAPI worker(void *unused)
{
    (void)unused;
    return 42;
}

static void notification(unsigned reason)
{
    if (reason == 2) {
        InterlockedExchange(&attached, (LONG)GetCurrentThreadId());
    } else if (reason == 3) {
        InterlockedExchange(&detached, (LONG)GetCurrentThreadId());
    } else if (!reason) {
        InterlockedExchange(&processDetached, (LONG)GetCurrentThreadId());
    }
}

int main(int argc, char **argv)
{
    if (argc != 2) {
        return 1;
    }
    HMODULE module = LoadLibraryExA(argv[1], 0, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!module) {
        return 2;
    }
    FARPROC raw = GetProcAddress(module, "SetNotification");
    int (*set)(void (*)(unsigned));
    memcpy(&set, &raw, sizeof(set));
    raw = GetProcAddress(module, "ThreadCounts");
    unsigned (*counts)(void);
    memcpy(&counts, &raw, sizeof(counts));
    if (!set || !counts || !set(notification) || counts()) {
        return 3;
    }
    DWORD id = 0, code = 0;
    HANDLE thread = CreateThread(0, 0, worker, 0, CREATE_SUSPENDED, &id);
    if (!thread || !id || attached || detached || counts()) {
        return 4;
    }
    if (ResumeThread(thread) != 1 ||
        WaitForSingleObject(thread, 30000) != WAIT_OBJECT_0 ||
        !GetExitCodeThread(thread, &code) ||
        code != 42 ||
        counts() != 0x10001U ||
        (DWORD)attached != id ||
        (DWORD)detached != id) {
        return 5;
    }
    CloseHandle(thread);
    attached = detached = 0;
    thread = CreateThread(0, 0, worker, 0, 0, &id);
    if (!thread ||
        WaitForSingleObject(thread, 30000) != WAIT_OBJECT_0 ||
        !GetExitCodeThread(thread, &code) ||
        code != 42 ||
        counts() != 0x20002U ||
        (DWORD)attached != id ||
        (DWORD)detached != id) {
        return 6;
    }
    CloseHandle(thread);
    if (!FreeLibrary(module) || (DWORD)processDetached != GetCurrentThreadId()) {
        return 7;
    }
    puts("PASS: actual Windows normal/suspended DLL thread attach/detach execute on the target thread; process detach "
         "on caller");
    return 0;
}
