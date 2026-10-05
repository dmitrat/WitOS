#pragma once
/* WitOS's replacement for internal_shared.h, the closed vcruntime header that the separately compiled microsoft/STL
 * sources include from the toolset's crt/src/vcruntime (P6.4.i). It declares only what the pinned sources WitOS
 * builds use: the Windows API, the C runtime's internal allocation functions, which are the ordinary ones of the
 * WitOS UCRT subset, and owners of such allocations (P6.4.i3). Nothing of the toolset's header is copied. */
#include <Windows.h>
#include <malloc.h>
#include <stdlib.h>

#define _calloc_crt calloc
#define _free_crt free
#define _malloc_crt malloc
#define _realloc_crt realloc

#ifdef __cplusplus
/* Owns a block of the C runtime's heap and frees it when it goes out of scope. */
template <typename T> class __crt_unique_heap_ptr {
public:
    explicit __crt_unique_heap_ptr(T *pointer = nullptr) noexcept : pointer_(pointer) {}

    __crt_unique_heap_ptr(__crt_unique_heap_ptr &&other) noexcept : pointer_(other.detach()) {}

    __crt_unique_heap_ptr(const __crt_unique_heap_ptr &) = delete;
    __crt_unique_heap_ptr &operator=(const __crt_unique_heap_ptr &) = delete;

    ~__crt_unique_heap_ptr()
    {
        free(pointer_);
    }

    T *get() const noexcept
    {
        return pointer_;
    }

    T *detach() noexcept
    {
        T *pointer = pointer_;
        pointer_ = nullptr;
        return pointer;
    }

    explicit operator bool() const noexcept
    {
        return pointer_ != nullptr;
    }

private:
    T *pointer_;
};

/* A temporary buffer of a function's scope. The STL's sources take it from the stack where it is small; here it is
 * always a heap block, which the owner frees the same way when it goes out of scope. */
template <typename T> using __crt_scoped_stack_ptr = __crt_unique_heap_ptr<T>;

/* Null when the element count overflows or the heap is exhausted, as the sources check. */
#define _malloc_crt_t(t, n) (__crt_unique_heap_ptr<t>(static_cast<t *>(calloc((n), sizeof(t)))))
#define _malloca_crt_t(t, n) (__crt_scoped_stack_ptr<t>(static_cast<t *>(calloc((n), sizeof(t)))))
#endif
