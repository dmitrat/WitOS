#include <new>

/* Throwing operator new (P6.4.g) over the platform's nothrow allocator: the guest's native heap
 * (Runtime.NativeAot/native_new.witos.cpp, which also owns every delete) or, on Windows, platform_windows.cpp. There
 * is no new handler: an allocation that fails throws std::bad_alloc. */
void *__cdecl operator new(size_t size)
{
    if (void *memory = operator new(size, std::nothrow)) {
        return memory;
    }
    throw std::bad_alloc();
}

void *__cdecl operator new[](size_t size)
{
    if (void *memory = operator new[](size, std::nothrow)) {
        return memory;
    }
    throw std::bad_alloc();
}
