#include "witos/platform.h"
#include "witos/x64_instructions.h"

/* The MC146818 real-time clock of the q35 board (plan step K6), at the CMOS index and data ports, read once for the
 * UTC domain. QEMU keeps the clock in UTC (its default -rtc base), in BCD unless status register B says binary, in
 * 24-hour form unless B says 12-hour, and the century in CMOS index 0x32. A reading waits for the update-in-progress
 * flag to clear and repeats until two consecutive readings agree; an implausible date reports no clock. */

#define CMOS_INDEX 0x70U
#define CMOS_DATA 0x71U
#define RTC_SECONDS 0x00U
#define RTC_MINUTES 0x02U
#define RTC_HOURS 0x04U
#define RTC_DAY 0x07U
#define RTC_MONTH 0x08U
#define RTC_YEAR 0x09U
#define RTC_STATUS_A 0x0AU
#define RTC_STATUS_B 0x0BU
#define RTC_CENTURY 0x32U
#define STATUS_A_UPDATE_IN_PROGRESS 0x80U
#define STATUS_B_24_HOUR 0x02U
#define STATUS_B_BINARY 0x04U
#define FIELD_COUNT 8U

static WitU8 cmos_read(WitU8 index)
{
    wit_x64_out8(CMOS_INDEX, index);
    return wit_x64_in8(CMOS_DATA);
}

static void read_fields(WitU8 fields[FIELD_COUNT])
{
    static const WitU8 indices[FIELD_COUNT] = {
        RTC_SECONDS, RTC_MINUTES, RTC_HOURS, RTC_DAY, RTC_MONTH, RTC_YEAR, RTC_CENTURY, RTC_STATUS_B};
    for (WitU32 i = 0; i < FIELD_COUNT; ++i) {
        fields[i] = cmos_read(indices[i]);
    }
}

static WitU64 decode(WitU8 value, int bcd)
{
    return bcd ? (WitU64)(value & 15) + (WitU64)(value >> 4) * 10 : value;
}

/* Days since 1970-01-01 of a proleptic Gregorian date (Howard Hinnant's days_from_civil). */
static WitU64 days_from_civil(WitU64 year, WitU64 month, WitU64 day)
{
    const WitU64 y = month <= 2 ? year - 1 : year;
    const WitU64 era = y / 400;
    const WitU64 yoe = y - era * 400;
    const WitU64 doy = (153 * (month > 2 ? month - 3 : month + 9) + 2) / 5 + day - 1;
    const WitU64 doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return era * 146097 + doe - 719468;
}

int wit_platform_realtime_seconds(WitU64 *seconds)
{
    WitU8 first[FIELD_COUNT], second[FIELD_COUNT];
    WitU32 attempt;
    *seconds = 0;
    for (attempt = 0; attempt < 1000000 && (cmos_read(RTC_STATUS_A) & STATUS_A_UPDATE_IN_PROGRESS); ++attempt) {
    }
    if (attempt == 1000000) {
        return 0;
    }
    read_fields(first);
    for (attempt = 0; attempt < 16; ++attempt) {
        read_fields(second);
        int same = 1;
        for (WitU32 i = 0; i < FIELD_COUNT; ++i) {
            same = same && first[i] == second[i];
        }
        if (same) {
            break;
        }
        for (WitU32 i = 0; i < FIELD_COUNT; ++i) {
            first[i] = second[i];
        }
    }
    if (attempt == 16) {
        return 0;
    }
    const int bcd = (first[7] & STATUS_B_BINARY) == 0;
    const WitU64 second_of_minute = decode(first[0], bcd);
    const WitU64 minute = decode(first[1], bcd);
    WitU64 hour = decode((WitU8)(first[2] & 0x7F), bcd);
    if (!(first[7] & STATUS_B_24_HOUR)) {
        hour %= 12;
        if (first[2] & 0x80) {
            hour += 12;
        }
    }
    const WitU64 day = decode(first[3], bcd);
    const WitU64 month = decode(first[4], bcd);
    WitU64 year = decode(first[5], bcd);
    const WitU64 century = decode(first[6], bcd);
    year += century ? century * 100 : (year < 70 ? 2000 : 1900);
    if (second_of_minute > 59 ||
        minute > 59 ||
        hour > 23 ||
        day < 1 ||
        day > 31 ||
        month < 1 ||
        month > 12 ||
        year < 1970 ||
        year > 2200) {
        return 0;
    }
    *seconds = days_from_civil(year, month, day) * 86400 + hour * 3600 + minute * 60 + second_of_minute;
    return 1;
}
