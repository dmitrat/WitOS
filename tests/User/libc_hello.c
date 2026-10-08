#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <math.h>
#include <setjmp.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/random.h>
#include <sys/utsname.h>
#include <time.h>
#include <unistd.h>

/* The first libc program on WitOS (RFC 0011 section 9.1, plan step S1.1): unchanged musl over the system layer's
 * dispatch, started by the kernel as the root task. It exercises what S1.1 brings: stdio to the kernel log,
 * malloc over mmap, strings and formatting, conversions, sorting, setjmp, errno from an unsupported call, time and
 * entropy from the kernel's clocks, thread-local storage of the main thread, atexit; then it exits with zero. The
 * last line names the library and the ISA for the boot scenario's check. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#else
#define ISA_NAME "aarch64"
#endif

extern const char __libc_version[] __attribute__((__weak__)); /* musl's internal version string, when visible */

static int checks, failures;
static __thread int thread_local_value = 41;
static jmp_buf jump;

static void check(int condition, const char *what)
{
    ++checks;
    if (!condition) {
        ++failures;
        printf("[LIBC] FAILED: %s\n", what);
    }
}

static int compare_ints(const void *left, const void *right)
{
    const int a = *(const int *)left, b = *(const int *)right;
    return (a > b) - (a < b);
}

static void at_exit(void)
{
    printf("[LIBC] musl %d.%d.%d on " ISA_NAME ": %d checks passed, %d failed\n", 1, 2, 5, checks - failures, failures);
}

int main(void)
{
    char buffer[128];
    struct utsname name;
    struct timespec monotonic, realtime;
    unsigned char random[16] = {0};

    printf("[LIBC] hello from musl on WitOS\n");
    check(atexit(at_exit) == 0, "atexit");

    /* Formatting and strings. */
    check(snprintf(buffer, sizeof(buffer), "%d %u %ld %x %s %c", -42, 42U, 1234567890123L, 0xBEEF, "text", 'z') == 32 &&
            strcmp(buffer, "-42 42 1234567890123 beef text z") == 0,
        "snprintf integers and strings");
    check(snprintf(buffer, sizeof(buffer), "%.3f %g %e", 3.14159, 0.5, 12345.678) == 22 &&
            strcmp(buffer, "3.142 0.5 1.234568e+04") == 0,
        "snprintf floating point");
    check(strlen("WitOS") == 5 && memcmp("abc", "abd", 3) < 0 && strstr("kernel-common", "common") != 0,
        "string functions");
    strcpy(buffer, "copy");
    strcat(buffer, "-cat");
    check(strcmp(buffer, "copy-cat") == 0, "strcpy and strcat");

    /* Conversions. */
    check(strtol("-123", 0, 10) == -123 && strtoul("ff", 0, 16) == 255 && atoi("77") == 77, "integer conversions");
    check(fabs(strtod("2.5e3", 0) - 2500.0) < 1e-9 && fabs(atof("-0.125") + 0.125) < 1e-12, "floating conversions");
    check(fabs(sqrt(2.0) - 1.4142135623730951) < 1e-15 && fabs(pow(2.0, 10.0) - 1024.0) < 1e-12, "math");

    /* The allocator over mmap: small, large, realloc, calloc. */
    int *small = malloc(16 * sizeof(int));
    check(small != 0, "malloc small");
    for (int i = 0; i < 16; ++i) {
        small[i] = 15 - i;
    }
    qsort(small, 16, sizeof(int), compare_ints);
    check(small[0] == 0 && small[15] == 15, "qsort");
    const int key = 7;
    check(bsearch(&key, small, 16, sizeof(int), compare_ints) == &small[7], "bsearch");
    small = realloc(small, 1024 * sizeof(int));
    check(small != 0 && small[7] == 7, "realloc keeps data");
    char *large = malloc(3 * 1024 * 1024);
    check(large != 0, "malloc large");
    if (large) {
        memset(large, 0xA5, 3 * 1024 * 1024);
        check(large[0] == (char)0xA5 && large[3 * 1024 * 1024 - 1] == (char)0xA5, "large block is writable");
    }
    long *zeros = calloc(4096, sizeof(long));
    check(zeros != 0 && zeros[0] == 0 && zeros[4095] == 0, "calloc zeroes");
    free(zeros);
    free(large);
    free(small);
    void *page = mmap(0, 8192, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    check(page != MAP_FAILED, "mmap anonymous");
    if (page != MAP_FAILED) {
        memset(page, 1, 8192);
        check(mprotect(page, 4096, PROT_READ) == 0, "mprotect");
        check(munmap(page, 8192) == 0, "munmap");
    }
    check(mmap(0, 4096, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0) == MAP_FAILED &&
            errno == EACCES,
        "executable anonymous memory refused");

    /* setjmp and longjmp. */
    volatile int jumped = 0;
    if (setjmp(jump) == 0) {
        jumped = 1;
        longjmp(jump, 7);
    } else {
        check(jumped == 1, "longjmp returns to setjmp");
    }

    /* errno from a call the system layer does not have yet: honest ENOSYS, nothing pretended. */
    errno = 0;
    check(getppid() == 0, "getppid");
    check(open("/missing", 0) == -1 && errno == ENOSYS, "open before files reports ENOSYS, not a missing file");

    /* Clocks and entropy. */
    check(clock_gettime(CLOCK_MONOTONIC, &monotonic) == 0 && (monotonic.tv_sec > 0 || monotonic.tv_nsec > 0),
        "monotonic clock");
    check(clock_gettime(CLOCK_REALTIME, &realtime) == 0 && realtime.tv_sec > 1767225600L, "real-time clock after 2026");
    check(time(0) >= realtime.tv_sec, "time()");
    check(getrandom(random, sizeof(random), 0) == (ssize_t)sizeof(random), "getrandom");
    int nonzero = 0;
    for (unsigned i = 0; i < sizeof(random); ++i) {
        nonzero |= random[i];
    }
    check(nonzero != 0, "entropy is not zero");
    struct timespec nap = {0, 2000000};
    check(nanosleep(&nap, 0) == 0, "nanosleep");
    struct timespec after;
    check(clock_gettime(CLOCK_MONOTONIC, &after) == 0 &&
            (after.tv_sec > monotonic.tv_sec ||
                (after.tv_sec == monotonic.tv_sec && after.tv_nsec >= monotonic.tv_nsec + 2000000L)),
        "nanosleep advanced the clock");

    /* Thread-local storage of the main thread, the terminal check and the system name. */
    thread_local_value += 1;
    check(thread_local_value == 42, "thread-local variable");
    check(isatty(1) == 1 && isatty(7) == 0, "standard output is the log terminal");
    check(uname(&name) == 0 && strcmp(name.sysname, "WitOS") == 0 && strcmp(name.machine, ISA_NAME) == 0, "uname");
    fprintf(stderr, "[LIBC] standard error reaches the log too\n");
    return failures ? 1 : 0;
}
