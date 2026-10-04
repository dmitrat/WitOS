#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstring>
#include <cstdlib>
static unsigned live, allocated, released;

template <class T> static T api(const char *name)
{
    auto symbol = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), name);
    if (!symbol) {
        std::abort();
    }
    T result;
    static_assert(sizeof(result) == sizeof(symbol));
    std::memcpy(&result, &symbol, sizeof(result));
    return result;
}

static LPWCH WINAPI capture_get()
{
    auto result = api<decltype(&GetEnvironmentStringsW)>("GetEnvironmentStringsW")();
    if (result) {
        ++live;
        ++allocated;
    }
    return result;
}

static BOOL WINAPI capture_free(LPWCH value)
{
    auto result = api<decltype(&FreeEnvironmentStringsW)>("FreeEnvironmentStringsW")(value);
    if (result) {
        if (!live) {
            std::abort();
        }
        --live;
        ++released;
    }
    return result;
}

extern "C" decltype(&GetEnvironmentStringsW) const __imp_GetEnvironmentStringsW = capture_get;
extern "C" decltype(&FreeEnvironmentStringsW) const __imp_FreeEnvironmentStringsW = capture_free;

extern "C" unsigned host_environment_live()
{
    return live;
}

extern "C" unsigned host_environment_allocated()
{
    return allocated;
}

extern "C" unsigned host_environment_released()
{
    return released;
}
