#include <errno.h>
#include <limits.h>
#include <stdint.h>
#include "crt.h"

/* The wide printf engine (P6.4.h): UCRT's flags, widths, precisions, lengths, integer, character, string and pointer
 * conversions and legacy wide specifiers, under the buffer contracts of __stdio_common_vswprintf and
 * __stdio_common_vsnwprintf_s. As in UCRT, %n and the w, L and T lengths on integers are invalid parameters; the
 * secure functions also reject an unknown conversion, modifiers on %% and a specification the format ends in, which
 * the others write literally, as %, or not at all. Floating-point conversions and %Z end the process as unimplemented.
 * A width, precision or count beyond INT_MAX fails with -1 and errno unchanged, as in UCRT. */
namespace WitCrt {

namespace {

enum class Size {
    Default,
    Char, // hh
    Short, // h
    Long, // l
    LongLong, // ll
    Int32, // I32
    Int64, // I64
    Pointer, // I, z, t, j
    Wide, // w
    LongDouble, // L
    Text // T
};

struct Spec {
    bool Left = false, Plus = false, Space = false, Alternate = false, Zero = false;
    bool Modified = false; // any flag, width, precision or length
    int Width = 0;
    int Precision = -1; // absent
    Size Length = Size::Default;
    wchar_t Type = 0; // 0 when the format ends inside the specification
};

class Formatter {
public:
    Formatter(Output &output, unsigned long long options, bool secure, const Locale &locale, va_list args)
        : output_(output), options_(options), secure_(secure), locale_(locale)
    {
        va_copy(args_, args);
    }

    ~Formatter()
    {
        va_end(args_);
    }

    int Run(const wchar_t *format)
    {
        while (*format) {
            if (*format != L'%') {
                const wchar_t *start = format;
                while (*format && *format != L'%') {
                    ++format;
                }
                if (!Put(start, size_t(format - start))) {
                    return -1;
                }
                continue;
            }
            ++format;
            Spec spec;
            if (!Parse(format, spec)) {
                return -1;
            }
            if (!spec.Type) {
                if (secure_) {
                    InvalidParameter();
                }
                break;
            }
            if (!Convert(spec)) {
                return -1;
            }
        }
        return int(count_);
    }

private:
    static constexpr size_t CHUNK = 32, DIGITS = 24;

    Output &output_;
    unsigned long long options_;
    bool secure_;
    const Locale &locale_;
    va_list args_;
    size_t count_ = 0;

    bool Put(const wchar_t *text, size_t count)
    {
        if (count > size_t(INT_MAX) - count_) {
            return false;
        }
        count_ += count;
        return !count || output_.Write(text, count);
    }

    bool Repeat(wchar_t value, size_t count)
    {
        if (count > size_t(INT_MAX) - count_) {
            return false;
        }
        wchar_t chunk[CHUNK];
        for (size_t i = 0; i < CHUNK; ++i) {
            chunk[i] = value;
        }
        while (count) {
            const size_t step = count < CHUNK ? count : CHUNK;
            if (!Put(chunk, step)) {
                return false;
            }
            count -= step;
        }
        return true;
    }

    /* Whether a conversion of this many characters keeps the count within INT_MAX; checked before it writes. */
    bool Fits(size_t total) const
    {
        return total <= size_t(INT_MAX) - count_;
    }

    /* The padding of a character or string, with zeros under the 0 flag, as UCRT pads them. */
    bool PadBefore(const Spec &spec, size_t length)
    {
        const size_t padding = size_t(spec.Width) > length ? size_t(spec.Width) - length : 0;
        return Fits(length + padding) && (spec.Left || Repeat(spec.Zero ? L'0' : L' ', padding));
    }

    bool PadAfter(const Spec &spec, size_t length)
    {
        const size_t padding = size_t(spec.Width) > length ? size_t(spec.Width) - length : 0;
        return !spec.Left || Repeat(L' ', padding);
    }

    /* Reads a decimal number; false when it exceeds INT_MAX. */
    static bool Number(const wchar_t *&format, int &result)
    {
        long long value = 0;
        while (*format >= L'0' && *format <= L'9') {
            value = value * 10 + (*format++ - L'0');
            if (value > INT_MAX) {
                return false;
            }
        }
        result = int(value);
        return true;
    }

    /* Parses the specification after a %; false for a width or precision beyond INT_MAX. */
    bool Parse(const wchar_t *&format, Spec &spec)
    {
        for (;; ++format) {
            if (*format == L'-') {
                spec.Left = true;
            } else if (*format == L'+') {
                spec.Plus = true;
            } else if (*format == L' ') {
                spec.Space = true;
            } else if (*format == L'#') {
                spec.Alternate = true;
            } else if (*format == L'0') {
                spec.Zero = true;
            } else {
                break;
            }
            spec.Modified = true;
        }
        if (*format == L'*') {
            ++format;
            const int width = va_arg(args_, int);
            if (width == INT_MIN) {
                return false;
            }
            spec.Left = spec.Left || width < 0;
            spec.Width = width < 0 ? -width : width;
            spec.Modified = true;
        } else if (*format >= L'0' && *format <= L'9') {
            if (!Number(format, spec.Width)) {
                return false;
            }
            spec.Modified = true;
        }
        if (*format == L'.') {
            ++format;
            spec.Modified = true;
            if (*format == L'*') {
                ++format;
                const int precision = va_arg(args_, int);
                spec.Precision = precision < 0 ? -1 : precision;
            } else if (!Number(format, spec.Precision)) {
                return false;
            }
        }
        switch (*format) {
        case L'h':
            spec.Length = format[1] == L'h' ? Size::Char : Size::Short;
            format += spec.Length == Size::Char ? 2 : 1;
            break;
        case L'l':
            spec.Length = format[1] == L'l' ? Size::LongLong : Size::Long;
            format += spec.Length == Size::LongLong ? 2 : 1;
            break;
        case L'I':
            if (format[1] == L'3' && format[2] == L'2') {
                spec.Length = Size::Int32;
                format += 3;
            } else if (format[1] == L'6' && format[2] == L'4') {
                spec.Length = Size::Int64;
                format += 3;
            } else {
                spec.Length = Size::Pointer;
                ++format;
            }
            break;
        case L'j':
        case L'z':
        case L't':
            spec.Length = Size::Pointer;
            ++format;
            break;
        case L'w':
            spec.Length = Size::Wide;
            ++format;
            break;
        case L'L':
            spec.Length = Size::LongDouble;
            ++format;
            break;
        case L'T':
            spec.Length = Size::Text;
            ++format;
            break;
        default:
            break;
        }
        spec.Modified = spec.Modified || spec.Length != Size::Default;
        spec.Type = *format;
        if (spec.Type) {
            ++format;
        }
        return true;
    }

    /* UCRT's choice between a wide and a narrow character or string. */
    bool Wide(const Spec &spec) const
    {
        switch (spec.Length) {
        case Size::Short:
            return false;
        case Size::Long:
        case Size::Wide:
        case Size::Text:
            return true;
        default:
            break;
        }
        const bool naturallyWide = (options_ & PRINTF_LEGACY_WIDE_SPECIFIERS) != 0;
        const bool natural = spec.Type == L'c' || spec.Type == L's';
        return naturallyWide == natural;
    }

    bool Convert(const Spec &spec)
    {
        switch (spec.Type) {
        case L'%':
            if (spec.Modified && secure_) {
                InvalidParameter();
            }
            return Put(L"%", 1);
        case L'd':
        case L'i':
        case L'u':
        case L'o':
        case L'x':
        case L'X':
            if (spec.Length == Size::Wide || spec.Length == Size::LongDouble || spec.Length == Size::Text) {
                InvalidParameter();
            }
            return Integer(spec);
        case L'p': {
            Spec pointer = spec; // UCRT ignores lengths and the # flag here
            pointer.Type = L'X';
            pointer.Precision = 2 * sizeof(void *);
            pointer.Alternate = false;
            pointer.Length = Size::Int64;
            return Integer(pointer);
        }
        case L'c':
        case L'C':
            return Character(spec);
        case L's':
        case L'S':
            return String(spec);
        case L'n':
            InvalidParameter(); // disabled in UCRT too
        case L'e':
        case L'E':
        case L'f':
        case L'F':
        case L'g':
        case L'G':
        case L'a':
        case L'A':
        case L'Z':
            InvalidParameter(); // floating point and counted strings are not implemented
        default:
            if (secure_) {
                InvalidParameter();
            }
            return Put(&spec.Type, 1); // UCRT writes an unknown conversion character alone
        }
    }

    bool Integer(const Spec &spec)
    {
        const bool isSigned = spec.Type == L'd' || spec.Type == L'i';
        const bool wide64 = spec.Length == Size::LongLong || spec.Length == Size::Int64 || spec.Length == Size::Pointer;
        uint64_t value;
        bool negative = false;
        if (isSigned) {
            int64_t number;
            if (wide64) {
                number = va_arg(args_, long long);
            } else {
                number = va_arg(args_, int);
                if (spec.Length == Size::Char) {
                    number = static_cast<signed char>(number);
                } else if (spec.Length == Size::Short) {
                    number = static_cast<short>(number);
                }
            }
            negative = number < 0;
            value = negative ? 0 - uint64_t(number) : uint64_t(number);
        } else if (wide64) {
            value = va_arg(args_, unsigned long long);
        } else {
            value = va_arg(args_, unsigned int);
            if (spec.Length == Size::Char) {
                value = static_cast<unsigned char>(value);
            } else if (spec.Length == Size::Short) {
                value = static_cast<unsigned short>(value);
            }
        }
        const unsigned base = spec.Type == L'o' ? 8 : spec.Type == L'x' || spec.Type == L'X' ? 16 : 10;
        const wchar_t *alphabet = spec.Type == L'X' ? L"0123456789ABCDEF" : L"0123456789abcdef";
        wchar_t text[DIGITS];
        size_t length = 0;
        for (uint64_t rest = value; rest; rest /= base) {
            text[DIGITS - ++length] = alphabet[rest % base];
        }
        const wchar_t *digits = text + DIGITS - length;
        const size_t precision = spec.Precision < 0 ? 1 : size_t(spec.Precision);
        size_t zeros = precision > length ? precision - length : 0;
        if (spec.Alternate && spec.Type == L'o' && !zeros && (!length || digits[0] != L'0')) {
            zeros = 1;
        }
        wchar_t prefix[2];
        size_t prefixLength = 0;
        if (negative) {
            prefix[prefixLength++] = L'-';
        } else if (isSigned && spec.Plus) {
            prefix[prefixLength++] = L'+';
        } else if (isSigned && spec.Space) {
            prefix[prefixLength++] = L' ';
        }
        if (spec.Alternate && base == 16 && value) {
            prefix[prefixLength++] = L'0';
            prefix[prefixLength++] = spec.Type;
        }
        const size_t used = prefixLength + zeros + length;
        size_t padding = size_t(spec.Width) > used ? size_t(spec.Width) - used : 0;
        if (!Fits(used + padding)) {
            return false;
        }
        if (spec.Zero && !spec.Left && spec.Precision < 0) {
            zeros += padding;
            padding = 0;
        }
        return (spec.Left || Repeat(L' ', padding)) &&
            Put(prefix, prefixLength) &&
            Repeat(L'0', zeros) &&
            Put(digits, length) &&
            (!spec.Left || Repeat(L' ', padding));
    }

    bool Character(const Spec &spec)
    {
        const int argument = va_arg(args_, int);
        wchar_t wide[2];
        int produced = 1;
        if (Wide(spec)) {
            wide[0] = wchar_t(argument);
        } else {
            const char narrow = char(argument);
            if (ToWide(locale_, &narrow, 1, wide, produced) < 0) {
                errno = EILSEQ; // UCRT writes nothing for a character its locale cannot convert, and goes on
                return true;
            }
        }
        return PadBefore(spec, size_t(produced)) && Put(wide, size_t(produced)) && PadAfter(spec, size_t(produced));
    }

    /* Converts the next character of a narrow string without reading past its terminator. */
    int NextNarrow(const char *bytes, wchar_t wide[2], int &count) const
    {
        size_t available = 1;
        while (available < 4 && bytes[available - 1]) {
            ++available;
        }
        return ToWide(locale_, bytes, available, wide, count);
    }

    bool String(const Spec &spec)
    {
        const size_t limit = spec.Precision < 0 ? size_t(INT_MAX) : size_t(spec.Precision);
        if (Wide(spec)) {
            const wchar_t *value = va_arg(args_, const wchar_t *);
            if (!value) {
                value = L"(null)";
            }
            size_t length = 0;
            while (length < limit && value[length]) {
                ++length;
            }
            return PadBefore(spec, length) && Put(value, length) && PadAfter(spec, length);
        }
        const char *value = va_arg(args_, const char *);
        if (!value) {
            value = "(null)";
        }
        // The first pass converts the characters within the precision, so the padding is known before any output;
        // a character the locale cannot convert fails the call with EILSEQ, as in UCRT, before this conversion
        // writes anything (UCRT may have written padding and characters before it). The second pass converts the
        // same bytes.
        size_t characters = 0;
        for (const char *bytes = value; *bytes && characters < limit;) {
            wchar_t wide[2];
            int count = 0;
            const int used = NextNarrow(bytes, wide, count);
            if (used < 0) {
                errno = EILSEQ;
                return false;
            }
            bytes += used;
            characters += size_t(count);
        }
        if (!PadBefore(spec, characters)) {
            return false;
        }
        const char *bytes = value;
        for (size_t produced = 0; produced < characters;) {
            wchar_t wide[2];
            int count = 0;
            bytes += NextNarrow(bytes, wide, count);
            if (!Put(wide, size_t(count))) {
                return false;
            }
            produced += size_t(count);
        }
        return PadAfter(spec, characters);
    }
};

/* A bounded buffer. A secure function stops at the first character that does not fit, as UCRT's does: the rest of
 * the format is then neither written nor parsed. The others go on counting. */
class BufferOutput final : public Output {
public:
    BufferOutput(wchar_t *buffer, size_t capacity, bool stop) : buffer_(buffer), capacity_(capacity), stop_(stop) {}

    bool Write(const wchar_t *text, size_t count) override
    {
        for (size_t i = 0; i < count; ++i) {
            if (written_ == capacity_) {
                overflowed_ = true;
                return !stop_;
            }
            buffer_[written_++] = text[i];
        }
        return true;
    }

    bool Full() const
    {
        return written_ == capacity_;
    }

    size_t Written() const
    {
        return written_;
    }

    bool Overflowed() const
    {
        return overflowed_;
    }

private:
    wchar_t *buffer_;
    size_t capacity_;
    bool stop_;
    size_t written_ = 0;
    bool overflowed_ = false;
};

} // namespace

int Format(
    Output &output, unsigned long long options, bool secure, const wchar_t *format, const Locale &locale, va_list args)
{
    Formatter formatter(output, options, secure, locale, args);
    return formatter.Run(format);
}

int Vswprintf(
    unsigned long long options, wchar_t *buffer, size_t count, const wchar_t *format, _locale_t locale, va_list args)
{
    if (!format || (!buffer && count)) {
        InvalidParameter();
    }
    // Only the standard snprintf contract needs the whole length past a full buffer; the others stop there.
    const bool counting = !buffer;
    BufferOutput output(buffer, count, !counting && !(options & PRINTF_STANDARD_SNPRINTF));
    const int result = Format(output, options, false, format, Resolve(locale), args);
    if (counting) {
        return result;
    }
    // A full buffer decides the result before a failure does, as in UCRT; a failure keeps what came before it, except
    // under the standard snprintf contract, where it empties the string.
    if (!output.Full()) {
        buffer[result < 0 && (options & PRINTF_STANDARD_SNPRINTF) ? 0 : output.Written()] = 0;
        return result < 0 ? -1 : result;
    }
    if (options & PRINTF_STANDARD_SNPRINTF) {
        if (count) {
            buffer[count - 1] = 0;
        }
        return result;
    }
    if (options & PRINTF_LEGACY_NULL_TERMINATION) {
        return result >= 0 ? result : -1; // it fit exactly, without a terminator
    }
    if (!count) {
        return -1;
    }
    buffer[count - 1] = 0;
    return -2; // too small; the headers report -1
}

int Vsnwprintf_s(unsigned long long options, wchar_t *buffer, size_t size, size_t limit, const wchar_t *format,
    _locale_t locale, va_list args)
{
    if (!format) {
        InvalidParameter();
    }
    if (!limit && !buffer && !size) {
        return 0;
    }
    if (!buffer || !size) {
        InvalidParameter();
    }
    if (limit >= size) {
        buffer[size - 1] = 0; // UCRT terminates the whole buffer first unless the limit is shorter
    }
    // The characters allowed before the terminator; one more may be written, and the next one stops the format.
    const size_t allowed = limit < size ? limit : size - 1;
    BufferOutput output(buffer, allowed + 1, true);
    const int result = Format(output, options, true, format, Resolve(locale), args);
    if (!output.Overflowed() && output.Written() <= allowed) {
        buffer[result < 0 ? 0 : output.Written()] = 0; // a failure empties the string
        return result < 0 ? -1 : result;
    }
    if (limit == _TRUNCATE || limit < size) {
        buffer[allowed] = 0;
        return -1;
    }
    buffer[0] = 0;
    InvalidParameter(); // too small, and truncation was not requested
}
} // namespace WitCrt

#ifndef WITCRT_REFERENCE
extern "C" int __cdecl __stdio_common_vswprintf(
    unsigned __int64 options, wchar_t *buffer, size_t count, const wchar_t *format, _locale_t locale, va_list args)
{
    return WitCrt::Vswprintf(options, buffer, count, format, locale, args);
}

extern "C" int __cdecl __stdio_common_vsnwprintf_s(unsigned __int64 options, wchar_t *buffer, size_t size, size_t limit,
    const wchar_t *format, _locale_t locale, va_list args)
{
    return WitCrt::Vsnwprintf_s(options, buffer, size, limit, format, locale, args);
}
#endif
