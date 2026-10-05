#include <errno.h>
#include <malloc.h>
#include "crt.h"

/* malloc, calloc, realloc and free (P6.4.h, P6.4.i2) over the platform's C allocation family: the guest's native heap
 * or the Windows process heap. A zero-byte request still allocates; calloc zeroes the block and rejects a product
 * beyond the largest request; realloc to zero frees the block and returns null; a failed request reports ENOMEM and
 * leaves a reallocated block in place, as in UCRT. */
namespace WitCrt {

void *Malloc(size_t size)
{
    void *block = size <= _HEAP_MAXREQ ? Platform::Allocate(size ? size : 1) : nullptr;
    if (!block) {
        errno = ENOMEM;
    }
    return block;
}

void *Calloc(size_t count, size_t size)
{
    const size_t total = count * size;
    void *block = !count || size <= _HEAP_MAXREQ / count ? Platform::AllocateZeroed(total ? total : 1) : nullptr;
    if (!block) {
        errno = ENOMEM;
    }
    return block;
}

void *Realloc(void *block, size_t size)
{
    if (!block) {
        return Malloc(size);
    }
    if (!size) {
        Platform::Free(block);
        return nullptr;
    }
    void *moved = size <= _HEAP_MAXREQ ? Platform::Reallocate(block, size) : nullptr;
    if (!moved) {
        errno = ENOMEM;
    }
    return moved;
}

void Free(void *block)
{
    if (block) {
        Platform::Free(block);
    }
}

} // namespace WitCrt

#ifndef WITCRT_REFERENCE
extern "C" _CRTALLOCATOR _CRTRESTRICT void *__cdecl malloc(size_t size)
{
    return WitCrt::Malloc(size);
}

extern "C" _CRTALLOCATOR _CRTRESTRICT void *__cdecl calloc(size_t count, size_t size)
{
    return WitCrt::Calloc(count, size);
}

extern "C" _CRTALLOCATOR _CRTRESTRICT void *__cdecl realloc(void *block, size_t size)
{
    return WitCrt::Realloc(block, size);
}

extern "C" void __cdecl free(void *block)
{
    WitCrt::Free(block);
}
#endif
