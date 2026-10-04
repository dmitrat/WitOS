#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>

/* Windows reference for the order of PE TLS callbacks and DLL entry calls (P6.4). The host records its own step
 * markers into the same sink as the library; each scenario thread records itself, which names the thread of every
 * library record by its TLS address. Tokens:
 *   A<reason>:<tls>@<thread> / B... first / second TLS callback, E... entry point, M<step> marker,
 *   T:<tls> / N:<tls> / P:<tls> the main thread, the thread started after the load and the one that existed before it.
 * TRACE keeps the scenario threads only; RAW also shows threads Windows starts on its own, labelled X. */
#define WHO_MAIN 6ULL
#define WHO_AFTER 7ULL
#define WHO_BEFORE 8ULL
#define WHO_STEP 9ULL

static void (*record)(unsigned long long);
static void (*identify)(unsigned long long);
static void (*marker_set)(int);
static HANDLE ready, go;

static void step(unsigned number)
{
    record(WHO_STEP << 56 | (unsigned long long)number << 48);
}

static DWORD WINAPI started_after(void *arg)
{
    (void)arg;
    identify(WHO_AFTER);
    marker_set(9);
    return 0;
}

/* Running before the load: a thread that has not yet started would be initialized after it and receive an attach. */
static DWORD WINAPI started_before(void *arg)
{
    (void)arg;
    if (!SetEvent(ready) || WaitForSingleObject(go, 30000) != WAIT_OBJECT_0) {
        return 1;
    }
    identify(WHO_BEFORE);
    return 0;
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
        const unsigned reason = (unsigned)(events[i] >> 48 & 0xFF);
        const unsigned value = (unsigned)(events[i] >> 32 & 0xFFFF);
        const char thread = label(events, count, (unsigned)events[i]);
        if (who == WHO_STEP) {
            printf(" M%u", reason);
        } else if (who >= WHO_MAIN) {
            printf(" %c:%u", "TNP"[who - WHO_MAIN], value);
        } else if (!scenario_only || thread != 'X') {
            printf(" %c%u:%u@%c", "?ABE"[who < 4 ? who : 0], reason, value, thread);
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
    raw = symbol(library, "TlsMarkerRecord");
    memcpy(&identify, &raw, sizeof(identify));
    raw = symbol(library, "TlsMarkerSet");
    memcpy(&marker_set, &raw, sizeof(marker_set));
    if (!library || !identify || !marker_set || !before) {
        return 3;
    }
    step(3);
    identify(WHO_MAIN);
    marker_set(5);
    if (!join(CreateThread(0, 0, started_after, 0, 0, 0))) {
        return 4;
    }
    step(4);
    if (!SetEvent(go) || !join(before)) {
        return 5;
    }
    step(5);
    if (!FreeLibrary(library)) {
        return 6;
    }
    step(6);
    unsigned long long events[64];
    const unsigned count = read(events, 64);
    if (count > 64) {
        return 7;
    }
    print("TRACE", events, count, 1);
    print("RAW", events, count, 0);
    return 0;
}
