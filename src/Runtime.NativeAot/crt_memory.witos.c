#include <stddef.h>
#include <stdint.h>
#include <string.h>
#pragma function(memset, memcmp, memmove, strcpy)

/* Byte-exact native CRT bootstrap implementations. Volatile accesses prevent
 * recursive CRT lowering and speculative reads beyond the caller's range.
 * Valid ranges/terminated strings are the caller's C contract; no allocation,
 * locale, compiler TLS, errno or native last-error is used here. */
void* __cdecl memset(void* destination, int value, size_t count)
{
    volatile unsigned char* to = (volatile unsigned char*)destination;
    for (size_t i = 0; i < count; ++i) to[i] = (unsigned char)value;
    return destination;
}
void* __cdecl memmove(void* destination, const void* source, size_t count)
{
    volatile unsigned char* to = (volatile unsigned char*)destination;
    const volatile unsigned char* from = (const volatile unsigned char*)source;
    if (destination == source || !count) return destination;
    /* Integer addresses avoid relational comparisons between unrelated C
     * objects, and subtraction avoids an overflowing source+count test. */
    if ((uintptr_t)destination > (uintptr_t)source && (uintptr_t)destination - (uintptr_t)source < count) {
        while (count) { --count; to[count] = from[count]; }
    } else {
        for (size_t i = 0; i < count; ++i) to[i] = from[i];
    }
    return destination;
}
int __cdecl memcmp(const void* first, const void* second, size_t count)
{
    const volatile unsigned char* a = (const volatile unsigned char*)first;
    const volatile unsigned char* b = (const volatile unsigned char*)second;
    for (size_t i = 0; i < count; ++i) {
        const unsigned char left = a[i], right = b[i];
        if (left != right) return (int)left - (int)right;
    }
    return 0;
}
char* __cdecl strcpy(char* destination, const char* source)
{
    volatile unsigned char* to = (volatile unsigned char*)destination;
    const volatile unsigned char* from = (const volatile unsigned char*)source;
    unsigned char value;
    do { value = *from++; *to++ = value; } while (value);
    return destination;
}
char* __cdecl strstr(const char* string, const char* substring)
{
    const volatile unsigned char* start = (const volatile unsigned char*)string;
    const volatile unsigned char* needle = (const volatile unsigned char*)substring;
    if (!*needle) return (char*)string;
    while (*start) {
        const volatile unsigned char* a = start;
        const volatile unsigned char* b = needle;
        for (;;) {
            const unsigned char right = *b;
            if (!right) return (char*)start;
            const unsigned char left = *a;
            if (!left || left != right) break;
            ++a; ++b;
        }
        ++start;
    }
    return NULL;
}
