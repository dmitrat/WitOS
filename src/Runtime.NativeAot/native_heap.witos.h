#ifndef WITOS_NATIVE_HEAP_H
#define WITOS_NATIVE_HEAP_H
#include <stddef.h>
// Private fixed-address Local allocation family, separate from C++ ownership.
extern "C" void *wit_native_local_allocate(size_t bytes);
extern "C" bool wit_native_local_release(void *address);
// Private C allocation family of the UCRT subset (malloc/realloc/free), separate from both.
extern "C" void *wit_native_c_allocate(size_t bytes);
extern "C" bool wit_native_c_release(void *address);
extern "C" size_t wit_native_c_size(const void *address); // usable bytes of a C allocation, 0 for anything else
#endif
