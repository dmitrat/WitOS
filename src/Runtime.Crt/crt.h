#pragma once
/* The WitOS UCRT subset (P6.4.h): the C runtime functions that the real hostfxr and hostpolicy call, under UCRT's
 * names and contracts and checked differentially against UCRT. The exported functions forward to the functions
 * declared here; the hosted differential compiles the sources with WITCRT_REFERENCE, which leaves the exports out, and
 * calls these next to UCRT's own. Locales are the C locale and the UTF-8 locale of _create_locale. An invalid
 * parameter ends the process, as UCRT's default invalid-parameter handler does, and so does every conversion or mode
 * this subset does not implement. The platform functions are in platform_windows.cpp (hosted) or platform_witos.cpp
 * (guest). */
#include <process.h>
#include <stdarg.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <time.h>
#include <wchar.h>

namespace WitCrt {

/* UCRT's _CRT_INTERNAL_PRINTF_* option bits that integer, character and string conversions see; the rounding bit
 * only concerns floating-point conversions, which are not implemented. */
constexpr unsigned long long PRINTF_LEGACY_NULL_TERMINATION = 1ULL << 0;
constexpr unsigned long long PRINTF_STANDARD_SNPRINTF = 1ULL << 1;
constexpr unsigned long long PRINTF_LEGACY_WIDE_SPECIFIERS = 1ULL << 2;

[[noreturn]] void InvalidParameter();

/* The errno of a Windows error, as UCRT maps the errors of the functions it calls (errno.cpp). */
int ErrnoFromOs(unsigned long error);

/* A locale: what _locale_t points to, the public data UCRT's headers read through it, and the ctype table, whose
 * index 0 is EOF. The multibyte data pointer is null. */
struct Locale {
    __crt_locale_pointers Pointers;
    __crt_locale_data_public Public;
    unsigned short Ctype[257];
    bool Utf8;
};

/* The locale of a call: the given one, or the C locale for null. */
const Locale &Resolve(_locale_t locale);

/* Converts one wide character of formatted output for a text stream as UCRT does: the C locale writes the characters
 * up to U+00FF as bytes and ? for the others; the UTF-8 locale encodes them and drops surrogates, even paired ones.
 * Returns the byte count. */
int ToStream(const Locale &locale, wchar_t value, char bytes[3]);

/* Converts the next multibyte character of at most `available` bytes; returns the bytes it used (at least 1) and
 * the one or two wide characters it produced, or -1 for an invalid or incomplete sequence. */
int ToWide(const Locale &locale, const char *text, size_t available, wchar_t wide[2], int &count);

_locale_t CreateLocale(int category, const char *name);
void FreeLocale(_locale_t locale);

/* Formatting (format.cpp): where formatted wide characters go. Write returns false when the output fails; the
 * formatter then returns -1 with errno as the output set it. */
class __declspec(novtable) Output {
public:
    virtual bool Write(const wchar_t *text, size_t count) = 0;

protected:
    ~Output() = default;
};

/* Formats into the output; returns the count of wide characters, or -1 for an output or encoding failure or a count
 * beyond INT_MAX. A secure format, as of the _s functions, rejects what the others write leniently. */
int Format(
    Output &output, unsigned long long options, bool secure, const wchar_t *format, const Locale &locale, va_list args);

int Vswprintf(
    unsigned long long options, wchar_t *buffer, size_t count, const wchar_t *format, _locale_t locale, va_list args);
int Vsnwprintf_s(unsigned long long options, wchar_t *buffer, size_t size, size_t limit, const wchar_t *format,
    _locale_t locale, va_list args);

/* Streams (stdio.cpp). */
FILE *Iob(unsigned index);
int Vfwprintf(unsigned long long options, FILE *stream, const wchar_t *format, _locale_t locale, va_list args);
wint_t Fputwc(wchar_t value, FILE *stream);
int Fputc(int value, FILE *stream);
int Fputs(const char *text, FILE *stream);
size_t Fwrite(const void *data, size_t size, size_t count, FILE *stream);
int Fflush(FILE *stream);
int Setvbuf(FILE *stream, char *buffer, int mode, size_t size);
FILE *Wfsopen(const wchar_t *path, const wchar_t *mode, int share);
int Fclose(FILE *stream);
int Wremove(const wchar_t *path);
int Wrename(const wchar_t *from, const wchar_t *to);
void InitializeStdioOptions();

/* Strings, characters and numbers (string.cpp). */
size_t Wcslen(const wchar_t *text);
int Wcscmp(const wchar_t *first, const wchar_t *second);
int Wcsncmp(const wchar_t *first, const wchar_t *second, size_t count);
wchar_t *Wcschr(const wchar_t *text, wchar_t value);
int Wcsicmp(const wchar_t *first, const wchar_t *second);
int Wcsnicmp(const wchar_t *first, const wchar_t *second, size_t count);
int Tolower(int value);
int Toupper(int value);
unsigned long Wcstoul(const wchar_t *text, wchar_t **end, int base);
int Wtoi(const wchar_t *text);
errno_t Wcserror_s(wchar_t *buffer, size_t count, int error);

/* Time (time.cpp). There is no time zone. */
__time64_t Time64(__time64_t *result);
errno_t Gmtime64_s(struct tm *result, const __time64_t *time);
size_t Wcsftime(wchar_t *buffer, size_t count, const wchar_t *format, const struct tm *time);

/* Memory (heap.cpp) and the rest (runtime.cpp). */
void *Malloc(size_t size);
void *Calloc(size_t count, size_t size);
void *Realloc(void *block, size_t size);
void Free(void *block);
float Ceilf(float value);

/* Threads (thread.cpp). */
uintptr_t Beginthreadex(
    void *security, unsigned stack, _beginthreadex_proc_type start, void *argument, unsigned flags, unsigned *id);
[[noreturn]] void Endthreadex(unsigned code);

/* Platform functions. Handles are the platform's; a failing function sets errno. A write writes all bytes or fails. */
namespace Platform {

struct Lock {
    void *Storage; // zero is unlocked
};

void Acquire(Lock &lock);
void Release(Lock &lock);
[[noreturn]] void Fatal();
void *StandardHandle(unsigned index); // 1 standard output, 2 standard error; null when absent
bool Write(void *handle, const char *bytes, size_t count);
void *Open(const wchar_t *path, bool append, int share); // writes go to the end when appending; null on failure
bool Close(void *handle);
bool Remove(const wchar_t *path);
bool Rename(const wchar_t *from, const wchar_t *to);
void *Allocate(size_t size);
void *AllocateZeroed(size_t size);
void *Reallocate(void *block, size_t size);
void Free(void *block);
bool UtcNow(__time64_t &seconds); // false without a UTC clock
/* A platform thread running start(argument): its handle, or null with errno set. The flags are CreateThread's. */
void *CreateThread(
    void *security, unsigned stack, _beginthreadex_proc_type start, void *argument, unsigned flags, unsigned *id);
[[noreturn]] void ExitThread(unsigned code); // ends the calling thread as returning from its start function does

} // namespace Platform

} // namespace WitCrt
