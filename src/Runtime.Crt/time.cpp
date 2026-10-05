#include <errno.h>
#include "crt.h"

/* Calendar time (P6.4.h): _time64 from the platform's UTC clock, which the guest does not have (it returns -1, as C
 * specifies for an unavailable time); _gmtime64_s over UCRT's range; wcsftime in the C locale with UCRT's # flag. %z
 * and %Z need a time zone, which does not exist here, and end the process as unimplemented. The STL's time facets
 * (P6.4.i3) read the C locale's day and month names and format through _Strftime and _Wcsftime, which, like UCRT's
 * strftime, formats the widened format and narrows the result. */
namespace WitCrt {

namespace {

constexpr long long MIN_TIME = -43200, MAX_TIME = 32536850399; // UCRT's range for _gmtime64_s

constexpr const wchar_t *DAYS[] = {L"Sunday", L"Monday", L"Tuesday", L"Wednesday", L"Thursday", L"Friday", L"Saturday"};
constexpr const wchar_t *MONTHS[] = {L"January", L"February", L"March", L"April", L"May", L"June", L"July", L"August",
    L"September", L"October", L"November", L"December"};

/* The record _Gettnames hands out: the names are always the C locale's, so it only identifies itself. */
struct TimeNames {
    unsigned long long Magic;
};

constexpr unsigned long long TIME_NAMES = 0x53454D414E54494DULL; // "MITNAMES"

/* ":Sun:Sunday:Mon:Monday:..." or ":Jan:January:...", as UCRT's _Getdays and _Getmonths list them. */
template <typename Char> Char *NameList(const wchar_t *const *names, size_t count)
{
    size_t length = 1;
    for (size_t i = 0; i < count; ++i) {
        for (const wchar_t *name = names[i]; *name; ++name) {
            ++length;
        }
        length += 5; // two colons and the abbreviation
    }
    auto *list = static_cast<Char *>(Malloc(length * sizeof(Char)));
    if (!list) {
        return nullptr;
    }
    Char *next = list;
    for (size_t i = 0; i < count; ++i) {
        *next++ = ':';
        for (size_t j = 0; j < 3; ++j) {
            *next++ = Char(names[i][j]);
        }
        *next++ = ':';
        for (const wchar_t *name = names[i]; *name; ++name) {
            *next++ = Char(*name);
        }
    }
    *next = 0;
    return list;
}

bool Leap(long long year)
{
    return year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
}

/* Days from 1970-01-01 to the first day of the year. */
long long YearStart(long long year)
{
    const long long y = year - 1;
    return 365 * (year - 1970) + (y / 4 - y / 100 + y / 400) - (1969 / 4 - 1969 / 100 + 1969 / 400);
}

/* The ISO 8601 week-numbering year and week of a date. */
void IsoWeek(const struct tm &time, int &year, int &week)
{
    const auto weeks = [](int y) {
        const auto start = [](int z) {
            return (z + z / 4 - z / 100 + z / 400) % 7;
        };
        return start(y) == 4 || start(y - 1) == 3 ? 53 : 52;
    };
    year = time.tm_year + 1900;
    week = (time.tm_yday - (time.tm_wday + 6) % 7 + 10) / 7;
    if (week < 1) {
        week = weeks(--year);
    } else if (week > weeks(year)) {
        ++year;
        week = 1;
    }
}

/* The output of wcsftime with UCRT's accounting of the room left: a character of literal text or of a name may take
 * the last slot, a number must leave a slot for the terminator, and a number under the # flag keeps its last digits
 * when it does not fit. The first failure ends the conversion with ERANGE. */
class Writer {
public:
    Writer(wchar_t *buffer, size_t count, bool record) : buffer_(buffer), count_(count), left_(count), record_(record)
    {}

    /* Whether the names come from a record of _Gettnames, whose Windows formats give %c and %r. */
    bool Record() const
    {
        return record_;
    }

    bool Failed() const
    {
        return failed_;
    }

    void Literal(wchar_t value)
    {
        if (failed_ || !left_) {
            failed_ = true;
            return;
        }
        buffer_[count_ - left_--] = value;
    }

    void Field(const wchar_t *text, size_t length)
    {
        if (failed_ || left_ <= length) {
            failed_ = true;
            return;
        }
        for (size_t i = 0; i < length; ++i) {
            buffer_[count_ - left_--] = text[i];
        }
    }

    /* A number with at least `digits` digits; under the # flag, without leading zeros. */
    void Number(long long value, int digits, bool alternate, wchar_t pad = L'0')
    {
        wchar_t text[24];
        size_t length = 0;
        for (unsigned long long rest = (unsigned long long)value; length == 0 || rest; rest /= 10) {
            text[sizeof(text) / sizeof(text[0]) - ++length] = wchar_t(L'0' + rest % 10);
        }
        const wchar_t *end = text + sizeof(text) / sizeof(text[0]);
        if (alternate) {
            if (failed_ || left_ < 2) {
                failed_ = true;
                return;
            }
            const size_t kept = length < left_ - 1 ? length : left_ - 1;
            Field(end - kept, kept);
            return;
        }
        while (length < size_t(digits)) {
            text[sizeof(text) / sizeof(text[0]) - ++length] = pad;
        }
        Field(end - length, length);
    }

    size_t Finish()
    {
        if (failed_ || !left_) {
            buffer_[0] = 0;
            errno = ERANGE;
            return 0;
        }
        buffer_[count_ - left_] = 0;
        return count_ - left_;
    }

private:
    wchar_t *buffer_;
    size_t count_;
    size_t left_;
    bool record_;
    bool failed_ = false;
};

void Require(bool valid)
{
    if (!valid) {
        InvalidParameter();
    }
}

void Convert(Writer &out, wchar_t type, bool alternate, const struct tm &time);

void Composite(Writer &out, const wchar_t *format, bool alternate, const struct tm &time)
{
    for (; *format && !out.Failed(); ++format) {
        if (*format == L'%') {
            Convert(out, *++format, alternate, time);
        } else {
            out.Literal(*format);
        }
    }
}

/* Names go out character by character, as literal text does. */
void Name(Writer &out, const wchar_t *name, bool abbreviated)
{
    for (size_t i = 0; name[i] && (!abbreviated || i < 3); ++i) {
        out.Literal(name[i]);
    }
}

void Convert(Writer &out, wchar_t type, bool alternate, const struct tm &time)
{
    const long long year = time.tm_year + 1900LL;
    switch (type) {
    case L'a':
    case L'A':
        Require(time.tm_wday >= 0 && time.tm_wday <= 6);
        Name(out, DAYS[time.tm_wday], type == L'a');
        break;
    case L'b':
    case L'h':
    case L'B':
        Require(time.tm_mon >= 0 && time.tm_mon <= 11);
        Name(out, MONTHS[time.tm_mon], type != L'B');
        break;
    case L'c':
        Composite(out,
            alternate          ? L"%A, %B %d, %Y %H:%M:%S"
                : out.Record() ? L"%m/%d/%y %H:%M:%S" // the record's short date and time
                               : L"%a %b %e %H:%M:%S %Y",
            false, time);
        break;
    case L'C':
        Require(year >= 0 && year <= 9999);
        out.Number(year / 100, 2, alternate);
        break;
    case L'd':
        Require(time.tm_mday >= 1 && time.tm_mday <= 31);
        out.Number(time.tm_mday, 2, alternate);
        break;
    case L'D':
        Composite(out, L"%m/%d/%y", alternate, time);
        break;
    case L'e':
        Require(time.tm_mday >= 1 && time.tm_mday <= 31);
        out.Number(time.tm_mday, 2, alternate, L' ');
        break;
    case L'F':
        Composite(out, L"%Y-%m-%d", alternate, time);
        break;
    case L'g':
    case L'G': {
        Require(year >= 0 &&
            year <= 9999 &&
            time.tm_yday >= 0 &&
            time.tm_yday <= 365 &&
            time.tm_wday >= 0 &&
            time.tm_wday <= 6);
        int isoYear, week;
        IsoWeek(time, isoYear, week);
        // UCRT keeps the leading zeros of the week-based year under the # flag.
        if (type == L'G') {
            out.Number(isoYear, 4, false);
        } else {
            out.Number(isoYear % 100, 2, false);
        }
        break;
    }
    case L'H':
        Require(time.tm_hour >= 0 && time.tm_hour <= 23);
        out.Number(time.tm_hour, 2, alternate);
        break;
    case L'I':
        Require(time.tm_hour >= 0 && time.tm_hour <= 23);
        out.Number(time.tm_hour % 12 ? time.tm_hour % 12 : 12, 2, alternate);
        break;
    case L'j':
        Require(time.tm_yday >= 0 && time.tm_yday <= 365);
        out.Number(time.tm_yday + 1, 3, alternate);
        break;
    case L'm':
        Require(time.tm_mon >= 0 && time.tm_mon <= 11);
        out.Number(time.tm_mon + 1, 2, alternate);
        break;
    case L'M':
        Require(time.tm_min >= 0 && time.tm_min <= 59);
        out.Number(time.tm_min, 2, alternate);
        break;
    case L'n':
        out.Literal(L'\n');
        break;
    case L'p':
        Require(time.tm_hour >= 0 && time.tm_hour <= 23);
        Name(out, time.tm_hour < 12 ? L"AM" : L"PM", false);
        break;
    case L'r':
        if (out.Record()) {
            Composite(out, L"%H:%M:%S", false, time); // the record's time format, which # does not change
        } else {
            Composite(out, L"%I:%M:%S %p", alternate, time);
        }
        break;
    case L'R':
        Composite(out, L"%H:%M", alternate, time);
        break;
    case L'S':
        Require(time.tm_sec >= 0 && time.tm_sec <= 60);
        out.Number(time.tm_sec, 2, alternate);
        break;
    case L't':
        out.Literal(L'\t');
        break;
    case L'T':
        Composite(out, L"%H:%M:%S", alternate, time);
        break;
    case L'X':
        Composite(out, L"%H:%M:%S", false, time); // the # flag does not apply here
        break;
    case L'u':
        Require(time.tm_wday >= 0 && time.tm_wday <= 6);
        out.Number(time.tm_wday ? time.tm_wday : 7, 1, alternate);
        break;
    case L'U':
        Require(time.tm_yday >= 0 && time.tm_yday <= 365 && time.tm_wday >= 0 && time.tm_wday <= 6);
        out.Number((time.tm_yday + 7 - time.tm_wday) / 7, 2, alternate);
        break;
    case L'V': {
        Require(year >= 0 &&
            year <= 9999 &&
            time.tm_yday >= 0 &&
            time.tm_yday <= 365 &&
            time.tm_wday >= 0 &&
            time.tm_wday <= 6);
        int isoYear, week;
        IsoWeek(time, isoYear, week);
        out.Number(week, 2, alternate);
        break;
    }
    case L'w':
        Require(time.tm_wday >= 0 && time.tm_wday <= 6);
        out.Number(time.tm_wday, 1, alternate);
        break;
    case L'W':
        Require(time.tm_yday >= 0 && time.tm_yday <= 365 && time.tm_wday >= 0 && time.tm_wday <= 6);
        out.Number((time.tm_yday + 7 - (time.tm_wday + 6) % 7) / 7, 2, alternate);
        break;
    case L'x':
        Composite(out, alternate ? L"%A, %B %d, %Y" : L"%m/%d/%y", false, time);
        break;
    case L'y':
        Require(year >= 0 && year <= 9999);
        out.Number(year % 100, 2, alternate);
        break;
    case L'Y':
        Require(year >= 0 && year <= 9999);
        out.Number(year, 1, alternate);
        break;
    case L'%':
        out.Literal(L'%');
        break;
    default:
        InvalidParameter(); // %z and %Z need a time zone; anything else is malformed
    }
}

} // namespace

__time64_t Time64(__time64_t *result)
{
    __time64_t now;
    if (!Platform::UtcNow(now)) {
        now = -1;
    }
    if (result) {
        *result = now;
    }
    return now;
}

errno_t Gmtime64_s(struct tm *result, const __time64_t *time)
{
    if (!result) {
        InvalidParameter();
    }
    result->tm_sec = result->tm_min = result->tm_hour = result->tm_mday = result->tm_mon = result->tm_year =
        result->tm_wday = result->tm_yday = result->tm_isdst = -1;
    if (!time) {
        InvalidParameter();
    }
    const long long value = *time;
    if (value < MIN_TIME || value > MAX_TIME) {
        errno = EINVAL;
        return EINVAL;
    }
    long long days = value / 86400, seconds = value % 86400;
    if (seconds < 0) {
        seconds += 86400;
        --days;
    }
    long long year = 1970 + days / 365;
    while (YearStart(year) > days) {
        --year;
    }
    while (YearStart(year + 1) <= days) {
        ++year;
    }
    int yday = int(days - YearStart(year));
    static constexpr int LENGTHS[] = {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
    int month = 0, mday = yday;
    while (mday >= LENGTHS[month] + (month == 1 && Leap(year))) {
        mday -= LENGTHS[month] + (month == 1 && Leap(year));
        ++month;
    }
    result->tm_sec = int(seconds % 60);
    result->tm_min = int(seconds / 60 % 60);
    result->tm_hour = int(seconds / 3600);
    result->tm_mday = mday + 1;
    result->tm_mon = month;
    result->tm_year = int(year - 1900);
    result->tm_wday = int((value / 86400 + 4) % 7); // from Thursday 1970-01-01; UCRT truncates negative days to 0
    result->tm_yday = yday;
    result->tm_isdst = 0;
    return 0;
}

size_t Wcsftime(wchar_t *buffer, size_t count, const wchar_t *format, const struct tm *time, bool record)
{
    if (!buffer || !count || !format || !time) {
        InvalidParameter();
    }
    Writer out(buffer, count, record);
    for (; *format && !out.Failed(); ++format) {
        if (*format != L'%') {
            out.Literal(*format);
            continue;
        }
        ++format;
        const bool alternate = *format == L'#';
        if (alternate) {
            ++format;
        }
        if (*format == L'E' || *format == L'O') {
            ++format; // the C locale has no alternative representations
        }
        Convert(out, *format, alternate, *time);
    }
    return out.Finish();
}

size_t Strftime(char *buffer, size_t count, const char *format, const struct tm *time, bool record)
{
    if (!buffer || !count || !format || !time) {
        InvalidParameter();
    }
    // The C locale widens each byte to the character of the same value and narrows the result back.
    size_t length = 0;
    while (format[length]) {
        ++length;
    }
    auto *wideFormat = static_cast<wchar_t *>(Malloc((length + 1) * sizeof(wchar_t)));
    auto *wideBuffer =
        count <= size_t(-1) / sizeof(wchar_t) ? static_cast<wchar_t *>(Malloc(count * sizeof(wchar_t))) : nullptr;
    size_t written = 0;
    if (wideFormat && wideBuffer) {
        for (size_t i = 0; i <= length; ++i) {
            wideFormat[i] = wchar_t(static_cast<unsigned char>(format[i]));
        }
        written = Wcsftime(wideBuffer, count, wideFormat, time, record);
        for (size_t i = 0; i <= written; ++i) {
            buffer[i] = char(wideBuffer[i]);
        }
    } else {
        buffer[0] = 0;
        errno = ENOMEM;
    }
    Free(wideBuffer);
    Free(wideFormat);
    return written;
}

void *Gettnames()
{
    auto *names = static_cast<TimeNames *>(Malloc(sizeof(TimeNames)));
    if (names) {
        names->Magic = TIME_NAMES;
    }
    return names;
}

bool OwnTimeNames(const void *names)
{
    return names && static_cast<const TimeNames *>(names)->Magic == TIME_NAMES;
}

char *Getdays()
{
    return NameList<char>(DAYS, 7);
}

char *Getmonths()
{
    return NameList<char>(MONTHS, 12);
}

wchar_t *WGetdays()
{
    return NameList<wchar_t>(DAYS, 7);
}

wchar_t *WGetmonths()
{
    return NameList<wchar_t>(MONTHS, 12);
}

} // namespace WitCrt

#ifndef WITCRT_REFERENCE
extern "C" __time64_t __cdecl _time64(__time64_t *result)
{
    return WitCrt::Time64(result);
}

extern "C" errno_t __cdecl _gmtime64_s(struct tm *result, const __time64_t *time)
{
    return WitCrt::Gmtime64_s(result, time);
}

extern "C" size_t __cdecl wcsftime(wchar_t *buffer, size_t count, const wchar_t *format, const struct tm *time)
{
    return WitCrt::Wcsftime(buffer, count, format, time);
}

/* The names of a time record are the C locale's whether or not the caller passes the record _Gettnames returned. */
extern "C" size_t __cdecl _Strftime(char *buffer, size_t count, const char *format, const struct tm *time, void *names)
{
    if (names && !WitCrt::OwnTimeNames(names)) {
        WitCrt::InvalidParameter();
    }
    return WitCrt::Strftime(buffer, count, format, time, names != nullptr);
}

extern "C" size_t __cdecl _Wcsftime(
    wchar_t *buffer, size_t count, const wchar_t *format, const struct tm *time, void *names)
{
    if (names && !WitCrt::OwnTimeNames(names)) {
        WitCrt::InvalidParameter();
    }
    return WitCrt::Wcsftime(buffer, count, format, time, names != nullptr);
}

extern "C" void *__cdecl _Gettnames()
{
    return WitCrt::Gettnames();
}

extern "C" void *__cdecl _W_Gettnames()
{
    return WitCrt::Gettnames();
}

extern "C" char *__cdecl _Getdays()
{
    return WitCrt::Getdays();
}

extern "C" char *__cdecl _Getmonths()
{
    return WitCrt::Getmonths();
}

extern "C" wchar_t *__cdecl _W_Getdays()
{
    return WitCrt::WGetdays();
}

extern "C" wchar_t *__cdecl _W_Getmonths()
{
    return WitCrt::WGetmonths();
}
#endif
