/* Windows-only native host. No CRT and no CoreCLR; not a WitOS guest adapter. */
typedef unsigned long U32;
typedef unsigned __int64 U64;
typedef void *Handle;
typedef U32(__stdcall *ThreadEntry)(void *);
typedef int(__cdecl *Probe)(int);
typedef int(__cdecl *Version)(void);
__declspec(dllimport) Handle __stdcall LoadLibraryExA(const char *, Handle, U32);
__declspec(dllimport) void *__stdcall GetProcAddress(Handle, const char *);
__declspec(dllimport) Handle __stdcall GetStdHandle(U32);
__declspec(dllimport) int __stdcall WriteFile(Handle, const void *, U32, U32 *, void *);
__declspec(dllimport) __declspec(noreturn) void __stdcall ExitProcess(U32);
__declspec(dllimport) Handle __stdcall CreateThread(void *, U64, ThreadEntry, void *, U32, U32 *);
__declspec(dllimport) Handle __stdcall CreateEventA(void *, int, int, const char *);
__declspec(dllimport) int __stdcall SetEvent(Handle);
__declspec(dllimport) U32 __stdcall WaitForSingleObject(Handle, U32);
__declspec(dllimport) int __stdcall CloseHandle(Handle);

static Probe probe;
static Handle gate;
static volatile U32 outcomes[2];

static void write(const char *text)
{
    U32 length = 0, written = 0;
    while (text[length]) {
        ++length;
    }
    if (!WriteFile(GetStdHandle((U32)-11), text, length, &written, 0) || written != length) {
        ExitProcess(240);
    }
}

static __declspec(noreturn) void fail(U32 code)
{
    write("[TARGET-FAIL]\n");
    ExitProcess(code);
}

static U32 __stdcall worker(void *argument)
{
    const U32 index = (U32)(U64)argument;
    const int seed = 40 + (int)index;
    if (WaitForSingleObject(gate, 10000) != 0) {
        return 1;
    }
    if (probe(seed) != (0x10100 | seed) || probe(seed) != (0x10200 | seed)) {
        return 2;
    }
    outcomes[index] = 1;
    return 0;
}

void host_main(void)
{
    Handle threads[2], module;
    Version version;
    /* Restrict module/dependency search to the executable directory and System32. */
    module = LoadLibraryExA("NativeAotTarget.dll", 0, 0x200 | 0x800);
    if (!module) {
        fail(1);
    }
    probe = (Probe)GetProcAddress(module, "witos_target_probe");
    version = (Version)GetProcAddress(module, "witos_target_version");
    if (!probe || !version || version() != 100008) {
        fail(2);
    }
    write("[TARGET-PASS] FirstExportInitialization\n");
    if (probe(11) != 0x1010B || probe(12) != 0x1020C) {
        fail(3);
    }
    write("[TARGET-PASS] AllocationGcAndExceptions\n");
    gate = CreateEventA(0, 1, 0, 0);
    if (!gate) {
        fail(4);
    }
    for (U32 i = 0; i < 2; ++i) {
        threads[i] = CreateThread(0, 0, worker, (void *)(U64)i, 0, 0);
        if (!threads[i]) {
            fail(5);
        }
    }
    if (!SetEvent(gate)) {
        fail(6);
    }
    for (U32 i = 0; i < 2; ++i) {
        if (WaitForSingleObject(threads[i], 10000) != 0 || outcomes[i] != 1) {
            fail(7);
        }
        if (!CloseHandle(threads[i])) {
            fail(8);
        }
    }
    if (!CloseHandle(gate)) {
        fail(9);
    }
    write("[TARGET-PASS] TlsOnNativeThreads\n");
    if (probe(13) != 0x1030D) {
        fail(10);
    }
    write("[TARGET-PASS] RepeatEntry\n[TARGET-SUCCESS]\n");
    /* NativeAOT library unloading is unsupported. End this dedicated host process. */
    ExitProcess(0);
}
