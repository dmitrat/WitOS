#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>

/* Windows reference for C++ thread_local objects in a DLL that links the WitOS dynamic TLS support (P6.4.c). The
 * scenario is the one of LibraryTlsCallbacks.c; each thread touches the objects once. Tokens:
 *   C<object>:<serial>@<thread> constructed, D<object>:<serial>@<thread> destroyed, M<step> marker,
 *   T / N / P the main thread, the thread started after the load and the one that was running before it.
 * TRACE keeps the scenario threads only; RAW also shows threads Windows starts on its own, labelled X. */
#define WHO_CONSTRUCT 4ULL
#define WHO_DESTRUCT 5ULL
#define WHO_MAIN 6ULL
#define WHO_AFTER 7ULL
#define WHO_BEFORE 8ULL
#define WHO_STEP 9ULL

static void (*record)(unsigned long long);
static void (*identify)(unsigned long long);
static int (*touch)(void);
static HANDLE ready, go;

static void step(unsigned number)
{
    record(WHO_STEP << 56 | (unsigned long long)number << 48);
}

static DWORD WINAPI started_after(void *arg)
{
    (void)arg;
    identify(WHO_AFTER);
    return touch() ? 0 : 1;
}

/* Running before the load: a thread that has not yet started would be initialized after it and receive an attach. */
static DWORD WINAPI started_before(void *arg)
{
    (void)arg;
    if (!SetEvent(ready) || WaitForSingleObject(go, 30000) != WAIT_OBJECT_0) {
        return 1;
    }
    identify(WHO_BEFORE);
    return touch() ? 0 : 1;
}

static int join(HANDLE thread)
{
    DWORD code = 1;
    const int ok =
        thread && WaitForSingleObject(thread, 30000) == WAIT_OBJECT_0 && GetExitCodeThread(thread, &code) && code == 0;
    if (thread) {
        CloseHandle(thread);
    }
    return ok;
}

static FARPROC symbol(HMODULE module, const char *name)
{
    return module ? GetProcAddress(module, name) : 0;
}

static char label(const unsigned long long *events, unsigned count, unsigned address)
{
    for (unsigned i = 0; i < count; ++i) {
        const unsigned who = (unsigned)(events[i] >> 56);
        if (who >= WHO_MAIN && who <= WHO_BEFORE && (unsigned)events[i] == address) {
            return "TNP"[who - WHO_MAIN];
        }
    }
    return 'X';
}

static void print(const char *name, const unsigned long long *events, unsigned count, int scenario_only)
{
    printf("%s:", name);
    for (unsigned i = 0; i < count; ++i) {
        const unsigned who = (unsigned)(events[i] >> 56);
        const unsigned object = (unsigned)(events[i] >> 48 & 0xFF);
        const unsigned serial = (unsigned)(events[i] >> 32 & 0xFFFF);
        const char thread = label(events, count, (unsigned)events[i]);
        if (who == WHO_STEP) {
            printf(" M%u", object);
        } else if (who >= WHO_MAIN) {
            printf(" %c", "TNP"[who - WHO_MAIN]);
        } else if (!scenario_only || thread != 'X') {
            printf(" %c%u:%u@%c", who == WHO_CONSTRUCT ? 'C' : 'D', object, serial, thread);
        }
    }
    printf("\n");
}

int main(int argc, char **argv)
{
    if (argc != 3) {
        return 1;
    }
    HMODULE sink = LoadLibraryExA(argv[1], 0, LOAD_WITH_ALTERED_SEARCH_PATH);
    FARPROC raw = symbol(sink, "SinkRecord");
    memcpy(&record, &raw, sizeof(record));
    unsigned (*read)(unsigned long long *, unsigned) = 0;
    raw = symbol(sink, "SinkRead");
    memcpy(&read, &raw, sizeof(read));
    ready = CreateEventA(0, TRUE, FALSE, 0);
    go = CreateEventA(0, TRUE, FALSE, 0);
    if (!record || !read || !ready || !go) {
        return 2;
    }
    step(1);
    HANDLE before = CreateThread(0, 0, started_before, 0, 0, 0);
    if (!before || WaitForSingleObject(ready, 30000) != WAIT_OBJECT_0) {
        return 2;
    }
    step(2);
    HMODULE library = LoadLibraryExA(argv[2], 0, LOAD_WITH_ALTERED_SEARCH_PATH);
    raw = symbol(library, "TlsObjectsRecord");
    memcpy(&identify, &raw, sizeof(identify));
    raw = symbol(library, "TouchObjects");
    memcpy(&touch, &raw, sizeof(touch));
    if (!library || !identify || !touch) {
        return 3;
    }
    step(3);
    identify(WHO_MAIN);
    if (!touch()) {
        return 4;
    }
    step(4);
    if (!join(CreateThread(0, 0, started_after, 0, 0, 0))) {
        return 5;
    }
    step(5);
    if (!SetEvent(go) || !join(before)) {
        return 6;
    }
    step(6);
    if (!FreeLibrary(library)) {
        return 7;
    }
    step(7);
    unsigned long long events[64];
    const unsigned count = read(events, 64);
    if (count > 64) {
        return 8;
    }
    print("TRACE", events, count, 1);
    print("RAW", events, count, 0);
    return 0;
}
