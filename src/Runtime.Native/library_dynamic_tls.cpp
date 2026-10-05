/* Dynamic C++ TLS for a native DLL (P6.4.c). Every DLL with thread_local objects links this object, as an MSVC DLL
 * links the CRT's DLL startup and tlsdyn: it supplies the entry point wit_library_dll_entry, the DLL's TLS directory
 * with the callback list, its own initializer table, the compiler's guard and the destructor registry. The semantics
 * follow the CRT: the loading thread initializes at the process attach, before DllMain; a thread attached after the
 * load runs the initializers in its attach callback; a thread that already ran initializes on its first access;
 * registered destructors run newest first when the thread detaches or the DLL unloads. It needs no OS or WitOS import:
 * table and function pointers are checked against the DLL's own headers, and a broken contract ends the process with
 * __fastfail. */
#include "native_limits.h"

extern "C" __declspec(noreturn) void __fastfail(unsigned int code);
#pragma intrinsic(__fastfail)

typedef void(__cdecl *TlsFunction)(void);
typedef void(__stdcall *TlsCallback)(void *, unsigned long, void *);

/* Static TLS: the template bounds, the index the loader writes, and the directory. */
#pragma section(".tls", long, read, write)
#pragma section(".tls$ZZZ", long, read, write)
#pragma section(".CRT$XDA", long, read)
#pragma section(".CRT$XDZ", long, read)
#pragma section(".CRT$XLA", long, read)
#pragma section(".CRT$XLC", long, read)
#pragma section(".CRT$XLD", long, read)
#pragma section(".CRT$XLZ", long, read)

extern "C" {
__declspec(allocate(".tls")) char _tls_start = 0;
__declspec(allocate(".tls$ZZZ")) char _tls_end = 0;
unsigned _tls_index;
extern __declspec(allocate(".CRT$XDA")) TlsFunction const wit_library_tls_initializers_begin = nullptr;
extern __declspec(allocate(".CRT$XDZ")) TlsFunction const wit_library_tls_initializers_end = nullptr;
[[msvc::no_tls_guard]] __declspec(thread) bool __tls_guard = false;
extern const unsigned char __ImageBase;
void __stdcall __dyn_tls_init(void *, unsigned long, void *) noexcept;
void __stdcall __dyn_tls_dtor(void *, unsigned long, void *) noexcept;
extern __declspec(allocate(".CRT$XLA")) TlsCallback const wit_library_tls_callbacks_begin = nullptr;
extern __declspec(allocate(".CRT$XLC")) TlsCallback const wit_library_tls_init_callback = __dyn_tls_init;
extern __declspec(allocate(".CRT$XLD")) TlsCallback const wit_library_tls_dtor_callback = __dyn_tls_dtor;
extern __declspec(allocate(".CRT$XLZ")) TlsCallback const wit_library_tls_callbacks_end = nullptr;

struct WitTlsDirectory {
    const void *Start, *End, *Index;
    const TlsCallback *Callbacks;
    unsigned ZeroFill, Characteristics;
};

extern const WitTlsDirectory _tls_used = {
    &_tls_start, &_tls_end, &_tls_index, &wit_library_tls_callbacks_begin + 1, 0, 0};
}

static __declspec(thread) unsigned phase; // 0 none, 1 initializing, 2 ready, 3 destroying, 4 destroyed
static __declspec(thread) unsigned count;
static __declspec(thread) TlsFunction destructors[WIT_NATIVE_TLS_MAX_DESTRUCTORS];

static __declspec(noreturn) void fail()
{
    __fastfail(7); // FAST_FAIL_FATAL_APP_EXIT
}

#define SECTION_EXECUTE 0x20000000U
#define SECTION_READ 0x40000000U
#define SECTION_WRITE 0x80000000U

/* A range of this DLL: inside one section of the image the loader mapped, with the required access and none of the
 * forbidden. */
static bool own_range(const void *address, unsigned long long bytes, unsigned required, unsigned forbidden)
{
    const unsigned char *base = &__ImageBase;
    const unsigned char *nt = base + *(const unsigned *)(base + 0x3C);
    const unsigned sections = *(const unsigned short *)(nt + 6);
    const unsigned char *table = nt + 24 + *(const unsigned short *)(nt + 20);
    const unsigned long long rva = (unsigned long long)((const unsigned char *)address - base);
    for (unsigned i = 0; i < sections; ++i) {
        const unsigned char *section = table + 40 * i;
        const unsigned start = *(const unsigned *)(section + 12), size = *(const unsigned *)(section + 8);
        const unsigned flags = *(const unsigned *)(section + 36);
        if (rva >= start && rva - start < size) {
            return bytes <= size - (rva - start) && (flags & required) == required && !(flags & forbidden);
        }
    }
    return false;
}

static bool own_code(const void *address)
{
    return own_range(address, 1, SECTION_EXECUTE, SECTION_WRITE);
}

/* Whether an address is code of this DLL, for the library startup's atexit (library_startup.cpp). */
extern "C" bool wit_library_owns_code(const void *address)
{
    return own_code(address);
}

/* The whole initializer table, read-only data between null sentinels that names at most
 * WIT_NATIVE_TLS_MAX_INITIALIZERS functions of this DLL, is checked before the first initializer runs. */
static void initialize()
{
    if (phase) {
        return; // Includes constructor recursion: never twice.
    }
    const TlsFunction *first = &wit_library_tls_initializers_begin, *last = &wit_library_tls_initializers_end;
    if (last <= first ||
        (unsigned long long)(last - first) - 1 > WIT_NATIVE_TLS_MAX_INITIALIZERS ||
        *first ||
        *last ||
        !own_range(first, (unsigned long long)(last - first + 1) * sizeof(TlsFunction), SECTION_READ,
            SECTION_WRITE | SECTION_EXECUTE)) {
        fail();
    }
    for (const TlsFunction *p = first + 1; p < last; ++p) {
        if (*p && !own_code((const void *)*p)) {
            fail();
        }
    }
    phase = 1;
    __tls_guard = true; // Guarded access to other thread_local objects is legal in constructors.
    for (const TlsFunction *p = first + 1; p < last; ++p) {
        if (*p) {
            (*p)();
        }
    }
    phase = 2;
}

static void destroy()
{
    if (phase != 2) {
        return; // Never initialized here, or already destroyed.
    }
    phase = 3;
    unsigned invoked = 0;
    while (count) {
        if (++invoked > 2 * WIT_NATIVE_TLS_MAX_DESTRUCTORS) {
            fail();
        }
        const TlsFunction callback = destructors[--count];
        destructors[count] = nullptr;
        if (!own_code((const void *)callback)) {
            fail();
        }
        callback(); // Pop before calling; a destructor may register bounded new work.
    }
    phase = 4;
}

extern "C" void __stdcall __dyn_tls_init(void *, unsigned long reason, void *) noexcept
{
    if (reason == 2) { // DLL_THREAD_ATTACH
        initialize();
    }
}

extern "C" void __cdecl __dyn_tls_on_demand_init() noexcept
{
    initialize();
}

extern "C" void __stdcall __dyn_tls_dtor(void *, unsigned long reason, void *) noexcept
{
    if (reason == 0 || reason == 3) { // DLL_PROCESS_DETACH, DLL_THREAD_DETACH
        destroy();
    }
}

/* The DLL's own DllMain, or this default, which accepts every reason. */
extern "C" int __stdcall DllMain(void *module, unsigned long reason, void *reserved);

extern "C" int __stdcall wit_library_default_dll_main(void *, unsigned long, void *)
{
    return 1;
}

#pragma comment(linker, "/alternatename:DllMain=wit_library_default_dll_main")

/* The entry point, as the CRT's DLL startup: the loading thread initializes its objects before DllMain. */
extern "C" int __stdcall wit_library_dll_entry(void *module, unsigned long reason, void *reserved)
{
    if (reason == 1) { // DLL_PROCESS_ATTACH
        initialize();
    }
    return DllMain(module, reason, reserved);
}

extern "C" int __cdecl __tlregdtor(TlsFunction callback)
{
    // Compiler-generated callers ignore failures, so exhaustion or a foreign function ends the process instead.
    if (phase != 1 && phase != 2 && phase != 3) {
        fail();
    }
    if (count == WIT_NATIVE_TLS_MAX_DESTRUCTORS || !own_code((const void *)callback)) {
        fail();
    }
    destructors[count++] = callback;
    return 0;
}
