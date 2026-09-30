#ifndef WITOS_NATIVE_HEAP_H
#define WITOS_NATIVE_HEAP_H
#include <stddef.h>
// Private fixed-address Local allocation family, separate from C++ ownership.
extern "C" void* wit_native_local_allocate(size_t bytes);
extern "C" bool wit_native_local_release(void* address);
#endif
