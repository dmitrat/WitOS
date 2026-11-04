#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/time.h>
#include <time.h>
#include <unistd.h>

/* The runtime-time scenario's /bin/init (plan step H7.4): UTC as a program sees it once the time service answered,
 * which the root task waits for before it starts /bin/init. It prints UTC, which the host judges against its own time
 * and the runner's time server, and checks that the libc's clocks agree, that UTC runs with the monotonic clock, and
 * that a file written on /data carries the clock's time, since the namespace service stamps it with its own reading. */

#define PREFIX "[TIME-CHECK] "
#define YEAR_2026 1767225600LL /* 2026-01-01T00:00:00Z */
#define NANOSECONDS 1000000000LL
#define SKEW_NANOSECONDS (NANOSECONDS / 2) /* two reads of two clocks a process may be preempted between */
#define FILE_SKEW_SECONDS 60
#define CHECK_FILE "/data/time-check.txt"

static int failures;

static void check(int passed, const char *what)
{
    if (!passed) {
        printf(PREFIX "FAILED: %s\n", what);
        ++failures;
    }
}

static long long difference(const struct timespec *later, const struct timespec *earlier)
{
    return (long long)(later->tv_sec - earlier->tv_sec) * NANOSECONDS + (later->tv_nsec - earlier->tv_nsec);
}

int main(void)
{
    struct timespec utc, utc_later, monotonic, monotonic_later;
    if (clock_gettime(CLOCK_REALTIME, &utc) != 0) {
        printf(PREFIX "CLOCK_REALTIME: %s\n" PREFIX "failed\n", strerror(errno));
        return 1;
    }
    struct tm parts;
    char text[64];
    gmtime_r(&utc.tv_sec, &parts);
    strftime(text, sizeof(text), "%Y-%m-%dT%H:%M:%SZ", &parts);
    printf(PREFIX "UTC %lld.%09ld %s\n", (long long)utc.tv_sec, utc.tv_nsec, text);
    check(utc.tv_sec >= YEAR_2026, "UTC after 2026");

    /* time() and gettimeofday() read the same clock. */
    const time_t now = time(0);
    check(now >= utc.tv_sec && now <= utc.tv_sec + 5, "time() agrees with CLOCK_REALTIME");
    struct timeval value;
    check(gettimeofday(&value, 0) == 0 && value.tv_sec >= utc.tv_sec && value.tv_sec <= utc.tv_sec + 5,
        "gettimeofday() agrees with CLOCK_REALTIME");

    /* A second of the monotonic clock is a second of UTC: the kernel's UTC is the set time plus the monotonic time. */
    const struct timespec second = {1, 0};
    clock_gettime(CLOCK_MONOTONIC, &monotonic);
    clock_gettime(CLOCK_REALTIME, &utc);
    nanosleep(&second, 0);
    clock_gettime(CLOCK_MONOTONIC, &monotonic_later);
    clock_gettime(CLOCK_REALTIME, &utc_later);
    const long long passed = difference(&monotonic_later, &monotonic), utc_passed = difference(&utc_later, &utc);
    check(passed >= NANOSECONDS, "the monotonic clock passed the second slept");
    check(utc_passed >= passed - SKEW_NANOSECONDS && utc_passed <= passed + SKEW_NANOSECONDS,
        "UTC runs with the monotonic clock");
    printf(PREFIX "a second slept: %lld ns monotonic, %lld ns of UTC\n", passed, utc_passed);

    /* A file on /data carries the clock's time. */
    const int file = open(CHECK_FILE, O_CREAT | O_TRUNC | O_WRONLY, 0644);
    struct stat status;
    check(file >= 0, "open " CHECK_FILE);
    if (file >= 0) {
        check(write(file, text, strlen(text)) == (ssize_t)strlen(text), "write " CHECK_FILE);
        check(close(file) == 0, "close " CHECK_FILE);
        if (stat(CHECK_FILE, &status) == 0) {
            const long long skew = (long long)status.st_mtime - (long long)utc_later.tv_sec;
            printf(PREFIX CHECK_FILE " modified %lld s from UTC\n", skew);
            check(skew >= -FILE_SKEW_SECONDS && skew <= FILE_SKEW_SECONDS, "the file's time is the clock's");
        } else {
            check(0, "stat " CHECK_FILE);
        }
    }
    printf(PREFIX "%s\n", failures ? "failed" : "passed");
    return failures ? 1 : 0;
}
