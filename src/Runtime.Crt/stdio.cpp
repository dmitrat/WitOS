#include <errno.h>
#include <limits.h>
#include <share.h>
#include "crt.h"

/* Streams (P6.4.h): the three standard streams and files opened for writing, in UCRT's text and binary modes. Text
 * mode writes every LF as CRLF and wide output in the locale of the call (see ToStream); binary mode writes wide
 * output as UTF-16LE code units, as UCRT does. The standard streams are unbuffered: each call writes its output when
 * it ends, so nothing waits for an exit-time flush. Files buffer 4096 bytes until fflush or fclose. Reading, update
 * modes and buffering the standard streams are not implemented and end the process. */
namespace WitCrt {

namespace {

constexpr size_t FILE_BUFFER = 4096, STAGING = 512;

struct Stream {
    void *Handle;
    Platform::Lock Lock;
    char *Buffer;
    size_t Capacity; // 0 for an unbuffered stream
    size_t Used;
    bool OwnsBuffer;
    bool Chosen; // setvbuf chose the buffering
    bool Text;
    unsigned Standard; // 1 + the index of a standard stream, 0 for a file
    Stream *Next; // the next open file
};

Stream standard[3] = {{nullptr, {}, nullptr, 0, 0, false, false, true, 1, nullptr},
    {nullptr, {}, nullptr, 0, 0, false, false, true, 2, nullptr},
    {nullptr, {}, nullptr, 0, 0, false, false, true, 3, nullptr}};
Stream *files;
Platform::Lock filesLock;

Stream &From(FILE *file)
{
    if (!file) {
        InvalidParameter();
    }
    return *reinterpret_cast<Stream *>(file);
}

bool WriteOut(Stream &stream, const char *bytes, size_t count)
{
    void *handle = stream.Standard ? Platform::StandardHandle(stream.Standard - 1) : stream.Handle;
    if (!handle) {
        errno = EBADF;
        return false;
    }
    return Platform::Write(handle, bytes, count);
}

/* Writes the buffered bytes; the bytes are dropped when the write fails, as UCRT drops them. */
bool FlushLocked(Stream &stream)
{
    if (!stream.Used) {
        return true;
    }
    const size_t used = stream.Used;
    stream.Used = 0;
    return WriteOut(stream, stream.Buffer, used);
}

/* The bytes of one call: into the stream's buffer, or staged and written when the call ends. */
class Writer {
public:
    explicit Writer(Stream &stream) : stream_(stream)
    {
        if (!stream.Standard && !stream.Chosen && !stream.Buffer) {
            stream.Buffer = static_cast<char *>(Malloc(FILE_BUFFER));
            if (stream.Buffer) {
                stream.Capacity = FILE_BUFFER;
                stream.OwnsBuffer = true;
            }
        }
    }

    bool Put(const char *bytes, size_t count)
    {
        for (size_t i = 0; i < count; ++i) {
            if (stream_.Text && bytes[i] == '\n' && !Byte('\r')) {
                return false;
            }
            if (!Byte(bytes[i])) {
                return false;
            }
            ++accepted_;
        }
        return true;
    }

    bool Finish()
    {
        if (!staged_) {
            return true;
        }
        const size_t staged = staged_;
        staged_ = 0;
        return WriteOut(stream_, staging_, staged);
    }

    size_t Accepted() const
    {
        return accepted_;
    }

private:
    bool Byte(char value)
    {
        if (stream_.Capacity) {
            if (stream_.Used == stream_.Capacity && !FlushLocked(stream_)) {
                return false;
            }
            stream_.Buffer[stream_.Used++] = value;
            return true;
        }
        if (staged_ == STAGING && !Finish()) {
            return false;
        }
        staging_[staged_++] = value;
        return true;
    }

    Stream &stream_;
    char staging_[STAGING];
    size_t staged_ = 0;
    size_t accepted_ = 0;
};

/* One wide character as the stream's mode writes it. UCRT takes a written U+FFFF for WEOF and fails after it, except
 * where the C locale wrote ? for it. */
bool PutWide(Writer &writer, bool text, const Locale &locale, wchar_t value)
{
    char bytes[3];
    if (!text) {
        bytes[0] = char(value & 0xFF);
        bytes[1] = char(value >> 8);
        return writer.Put(bytes, 2) && value != WEOF;
    }
    return writer.Put(bytes, size_t(ToStream(locale, value, bytes))) && (value != WEOF || !locale.Utf8);
}

class StreamOutput final : public Output {
public:
    StreamOutput(Writer &writer, bool text, const Locale &locale) : writer_(writer), text_(text), locale_(locale) {}

    bool Write(const wchar_t *text, size_t count) override
    {
        for (size_t i = 0; i < count; ++i) {
            if (!PutWide(writer_, text_, locale_, text[i])) {
                return false;
            }
        }
        return true;
    }

private:
    Writer &writer_;
    bool text_;
    const Locale &locale_;
};

/* Narrow formatted output: its bytes as they are, text mode adding CR before LF. */
class NarrowStreamOutput final : public NarrowOutput {
public:
    explicit NarrowStreamOutput(Writer &writer) : writer_(writer) {}

    bool Write(const char *text, size_t count) override
    {
        return writer_.Put(text, count);
    }

private:
    Writer &writer_;
};

} // namespace

FILE *Iob(unsigned index)
{
    if (index > 2) {
        InvalidParameter();
    }
    return reinterpret_cast<FILE *>(&standard[index]);
}

int Vfwprintf(unsigned long long options, FILE *file, const wchar_t *format, _locale_t locale, va_list args)
{
    Stream &stream = From(file);
    if (!format) {
        InvalidParameter();
    }
    const Locale &resolved = Resolve(locale);
    Platform::Acquire(stream.Lock);
    Writer writer(stream);
    StreamOutput output(writer, stream.Text, resolved);
    int result = Format(output, options, false, format, resolved, args);
    if (!writer.Finish()) {
        result = -1;
    }
    Platform::Release(stream.Lock);
    return result;
}

wint_t Fputwc(wchar_t value, FILE *file)
{
    Stream &stream = From(file);
    if (stream.Text && value > 0xFF) {
        errno = EILSEQ; // unlike formatted output, fputwc fails where the C locale has no byte for it
        return WEOF;
    }
    Platform::Acquire(stream.Lock);
    Writer writer(stream);
    const bool written = PutWide(writer, stream.Text, Resolve(nullptr), value) && writer.Finish();
    Platform::Release(stream.Lock);
    return written ? value : WEOF;
}

int Fputc(int value, FILE *file)
{
    Stream &stream = From(file);
    const char byte = char(value);
    Platform::Acquire(stream.Lock);
    Writer writer(stream);
    const bool written = writer.Put(&byte, 1) && writer.Finish();
    Platform::Release(stream.Lock);
    return written ? static_cast<unsigned char>(byte) : EOF;
}

int Fputs(const char *text, FILE *file)
{
    if (!text) {
        InvalidParameter();
    }
    Stream &stream = From(file);
    size_t length = 0;
    while (text[length]) {
        ++length;
    }
    Platform::Acquire(stream.Lock);
    Writer writer(stream);
    const bool written = writer.Put(text, length) && writer.Finish();
    Platform::Release(stream.Lock);
    return written ? 0 : EOF;
}

size_t Fwrite(const void *data, size_t size, size_t count, FILE *file)
{
    if (!size || !count) {
        return 0;
    }
    Stream &stream = From(file);
    if (!data || count > size_t(-1) / size) {
        InvalidParameter();
    }
    Platform::Acquire(stream.Lock);
    Writer writer(stream);
    const bool written = writer.Put(static_cast<const char *>(data), size * count) && writer.Finish();
    const size_t accepted = writer.Accepted();
    Platform::Release(stream.Lock);
    return written ? count : accepted / size;
}

int Fflush(FILE *file)
{
    if (file) {
        Stream &stream = From(file);
        Platform::Acquire(stream.Lock);
        const bool flushed = FlushLocked(stream);
        Platform::Release(stream.Lock);
        return flushed ? 0 : EOF;
    }
    bool flushed = true;
    Platform::Acquire(filesLock);
    for (Stream *stream = files; stream; stream = stream->Next) {
        Platform::Acquire(stream->Lock);
        flushed = FlushLocked(*stream) && flushed;
        Platform::Release(stream->Lock);
    }
    Platform::Release(filesLock);
    return flushed ? 0 : EOF;
}

int Setvbuf(FILE *file, char *buffer, int mode, size_t size)
{
    Stream &stream = From(file);
    if (mode != _IONBF && mode != _IOFBF && mode != _IOLBF) {
        InvalidParameter();
    }
    if (mode != _IONBF && (size < 2 || size > size_t(INT_MAX))) {
        InvalidParameter();
    }
    if (stream.Standard && mode != _IONBF) {
        InvalidParameter(); // the standard streams stay unbuffered: there is no exit-time flush
    }
    Platform::Acquire(stream.Lock);
    (void)FlushLocked(stream);
    if (stream.OwnsBuffer) {
        Free(stream.Buffer);
    }
    stream.Buffer = nullptr;
    stream.Capacity = 0;
    stream.OwnsBuffer = false;
    stream.Chosen = true;
    int result = 0;
    if (mode != _IONBF) {
        size &= ~size_t(1);
        stream.Buffer = buffer ? buffer : static_cast<char *>(Malloc(size));
        if (stream.Buffer) {
            stream.Capacity = size;
            stream.OwnsBuffer = !buffer;
        } else {
            result = -1;
        }
    }
    Platform::Release(stream.Lock);
    return result;
}

FILE *Wfsopen(const wchar_t *path, const wchar_t *mode, int share)
{
    if (!path || !mode || !*path) {
        InvalidParameter();
    }
    if (share != _SH_DENYRW &&
        share != _SH_DENYWR &&
        share != _SH_DENYRD &&
        share != _SH_DENYNO &&
        share != _SH_SECURE) {
        InvalidParameter();
    }
    if (mode[0] != L'w' && mode[0] != L'a') {
        InvalidParameter(); // reading is not implemented
    }
    bool text = true;
    size_t next = 1;
    if (mode[1] == L'b' || mode[1] == L't') {
        text = mode[1] == L't';
        next = 2;
    }
    if (mode[next]) {
        InvalidParameter(); // update and the other mode characters are not implemented
    }
    void *handle = Platform::Open(path, mode[0] == L'a', share);
    if (!handle) {
        return nullptr;
    }
    auto *stream = static_cast<Stream *>(Malloc(sizeof(Stream)));
    if (!stream) {
        Platform::Close(handle);
        return nullptr;
    }
    *stream = {handle, {}, nullptr, 0, 0, false, false, text, 0, nullptr};
    Platform::Acquire(filesLock);
    stream->Next = files;
    files = stream;
    Platform::Release(filesLock);
    return reinterpret_cast<FILE *>(stream);
}

int Fclose(FILE *file)
{
    Stream &stream = From(file);
    if (stream.Standard) {
        InvalidParameter(); // closing a standard stream is not implemented
    }
    Platform::Acquire(filesLock);
    Stream **link = &files;
    while (*link && *link != &stream) {
        link = &(*link)->Next;
    }
    if (!*link) {
        InvalidParameter(); // not an open stream
    }
    *link = stream.Next;
    Platform::Release(filesLock);
    Platform::Acquire(stream.Lock);
    const bool flushed = FlushLocked(stream);
    Platform::Release(stream.Lock);
    const bool closed = Platform::Close(stream.Handle);
    if (stream.OwnsBuffer) {
        Free(stream.Buffer);
    }
    Free(&stream);
    return flushed && closed ? 0 : EOF;
}

int Wremove(const wchar_t *path)
{
    if (!path) {
        InvalidParameter();
    }
    return Platform::Remove(path) ? 0 : -1;
}

int Wrename(const wchar_t *from, const wchar_t *to)
{
    if (!from || !to) {
        InvalidParameter();
    }
    return Platform::Rename(from, to) ? 0 : -1;
}

/* The narrow and descriptor functions CoreCLR calls (P6.4.k3a4): fprintf for container assertions, fopen and _wfopen
 * for the GC's and PGO's diagnostic files, _fileno and _write for minipal's error log, _flushall before a debug break. */
int Vfprintf(unsigned long long options, FILE *file, const char *format, _locale_t locale, va_list args)
{
    Stream &stream = From(file);
    if (!format) {
        InvalidParameter();
    }
    const Locale &resolved = Resolve(locale);
    Platform::Acquire(stream.Lock);
    Writer writer(stream);
    NarrowStreamOutput output(writer);
    int result = Format(output, options, false, format, resolved, args);
    if (!writer.Finish()) {
        result = -1;
    }
    Platform::Release(stream.Lock);
    return result;
}

/* fopen and _wfopen open as _fsopen and _wfsopen with _SH_DENYNO, as in UCRT; a narrow path is in the platform's ANSI
 * code page. */
FILE *Fopen(const char *path, const char *mode)
{
    if (!path || !mode || !*path) {
        InvalidParameter();
    }
    wchar_t wideMode[16];
    size_t length = 0;
    for (; mode[length]; ++length) {
        if (length + 1 == sizeof(wideMode) / sizeof(wideMode[0]) || static_cast<unsigned char>(mode[length]) > 0x7F) {
            InvalidParameter(); // no mode the subset implements has more or other characters
        }
        wideMode[length] = wchar_t(mode[length]);
    }
    wideMode[length] = 0;
    wchar_t *widePath = Platform::WidePath(path);
    if (!widePath) {
        return nullptr;
    }
    FILE *file = Wfsopen(widePath, wideMode, _SH_DENYNO);
    const int error = errno;
    Platform::Free(widePath);
    errno = error;
    return file;
}

FILE *Wfopen(const wchar_t *path, const wchar_t *mode)
{
    return Wfsopen(path, mode, _SH_DENYNO);
}

/* The standard streams are descriptors 0 to 2. Files have no descriptors here: asking for one is not implemented. */
int Fileno(FILE *file)
{
    Stream &stream = From(file);
    if (!stream.Standard) {
        InvalidParameter();
    }
    return int(stream.Standard - 1);
}

/* _write to standard output or error, in the text mode UCRT opens them in: CR before every LF; the result counts the
 * caller's bytes. The standard streams are unbuffered, so their order with stdio holds. Other descriptors are not
 * implemented. */
int WriteDescriptor(int descriptor, const void *data, unsigned count)
{
    if (descriptor != 1 && descriptor != 2) {
        InvalidParameter();
    }
    if (!count) {
        return 0;
    }
    if (!data) {
        InvalidParameter();
    }
    Stream &stream = standard[descriptor];
    Platform::Acquire(stream.Lock);
    Writer writer(stream);
    const bool written = writer.Put(static_cast<const char *>(data), count) && writer.Finish();
    Platform::Release(stream.Lock);
    return written ? int(count) : -1;
}

/* Flushes every file and counts the open streams, the three standard ones and the files, as UCRT does. */
int Flushall()
{
    int count = 3;
    Platform::Acquire(filesLock);
    for (Stream *stream = files; stream; stream = stream->Next) {
        Platform::Acquire(stream->Lock);
        (void)FlushLocked(*stream);
        Platform::Release(stream->Lock);
        ++count;
    }
    Platform::Release(filesLock);
    return count;
}

void InitializeStdioOptions()
{
    // What the startup code of a UCRT module sets: legacy wide specifiers and standard rounding for printf, legacy
    // wide specifiers for scanf. The storage is the module's own, in the headers' inline functions.
    *__local_stdio_printf_options() |=
        _CRT_INTERNAL_PRINTF_LEGACY_WIDE_SPECIFIERS | _CRT_INTERNAL_PRINTF_STANDARD_ROUNDING;
    *__local_stdio_scanf_options() |= _CRT_INTERNAL_SCANF_LEGACY_WIDE_SPECIFIERS;
}

} // namespace WitCrt

#ifndef WITCRT_REFERENCE
extern "C" FILE *__cdecl __acrt_iob_func(unsigned index)
{
    return WitCrt::Iob(index);
}

extern "C" int __cdecl __stdio_common_vfwprintf(
    unsigned __int64 options, FILE *stream, const wchar_t *format, _locale_t locale, va_list args)
{
    return WitCrt::Vfwprintf(options, stream, format, locale, args);
}

extern "C" wint_t __cdecl fputwc(wchar_t value, FILE *stream)
{
    return WitCrt::Fputwc(value, stream);
}

extern "C" int __cdecl fputc(int value, FILE *stream)
{
    return WitCrt::Fputc(value, stream);
}

extern "C" int __cdecl fputs(const char *text, FILE *stream)
{
    return WitCrt::Fputs(text, stream);
}

extern "C" size_t __cdecl fwrite(const void *data, size_t size, size_t count, FILE *stream)
{
    return WitCrt::Fwrite(data, size, count, stream);
}

extern "C" int __cdecl fflush(FILE *stream)
{
    return WitCrt::Fflush(stream);
}

extern "C" int __cdecl setvbuf(FILE *stream, char *buffer, int mode, size_t size)
{
    return WitCrt::Setvbuf(stream, buffer, mode, size);
}

extern "C" FILE *__cdecl _wfsopen(const wchar_t *path, const wchar_t *mode, int share)
{
    return WitCrt::Wfsopen(path, mode, share);
}

extern "C" int __cdecl fclose(FILE *stream)
{
    return WitCrt::Fclose(stream);
}

extern "C" int __cdecl _wremove(const wchar_t *path)
{
    return WitCrt::Wremove(path);
}

extern "C" int __cdecl _wrename(const wchar_t *from, const wchar_t *to)
{
    return WitCrt::Wrename(from, to);
}

extern "C" int __cdecl __stdio_common_vfprintf(
    unsigned __int64 options, FILE *stream, const char *format, _locale_t locale, va_list args)
{
    return WitCrt::Vfprintf(options, stream, format, locale, args);
}

extern "C" FILE *__cdecl fopen(const char *path, const char *mode)
{
    return WitCrt::Fopen(path, mode);
}

extern "C" FILE *__cdecl _wfopen(const wchar_t *path, const wchar_t *mode)
{
    return WitCrt::Wfopen(path, mode);
}

extern "C" int __cdecl _fileno(FILE *stream)
{
    return WitCrt::Fileno(stream);
}

extern "C" int __cdecl _write(int descriptor, const void *data, unsigned count)
{
    return WitCrt::WriteDescriptor(descriptor, data, count);
}

extern "C" int __cdecl _flushall(void)
{
    return WitCrt::Flushall();
}

/* Reading is not implemented. */
extern "C" char *__cdecl fgets(char *, int, FILE *)
{
    WitCrt::InvalidParameter();
}

/* Called by the module's startup before any formatted output. */
extern "C" void wit_crt_initialize_stdio_options(void)
{
    WitCrt::InitializeStdioOptions();
}
#endif
