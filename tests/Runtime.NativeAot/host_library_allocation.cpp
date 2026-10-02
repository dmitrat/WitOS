// Hosted test-only replacement allocation: delegate real allocations to the
// Windows CRT; inject one real bad_alloc after PATH to test PAL RAII cleanup.
// This file is never linked into a guest or a production native archive.
#include <new>
#include <cstdlib>
static bool failNext;
extern "C" void host_library_arm_allocation_failure(){failNext=true;}
void* operator new(size_t bytes)
{
    if(failNext){failNext=false;throw std::bad_alloc();}
    if(void* value=std::malloc(bytes?bytes:1))return value;
    throw std::bad_alloc();
}
void operator delete(void* value) noexcept {std::free(value);}
void operator delete(void* value,size_t) noexcept {std::free(value);}
