#define _GNU_SOURCE
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The driver of musl's libc-test on WitOS (plan step S1.3): the selected tests of tests/User/libc-test.json are
 * compiled unchanged, each with its main renamed, into this one static program, which the kernel starts as the root
 * task. A test reports through t_printf, which sets t_status and writes to standard output — the kernel log — and
 * returns t_status; the driver resets the status before every test, prints one line per test and a summary the
 * boot scenario requires, and exits with the number of failures. libc-test's own runner forks a process per test
 * with a stack limit and a timeout; neither exists here yet (S2, S6), so a test that faults ends the program. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#else
#define ISA_NAME "aarch64"
#endif

extern volatile int t_status;

typedef struct Test {
    const char *Name;
    int (*Main)(int, char **);
} Test;

#include "libc_test_table.h" /* generated: const Test libc_tests[]; LIBC_TEST_COUNT */

int main(int argc, char **argv)
{
    int passed = 0;
    printf("[LIBC-TEST] musl 1.2.5 on " ISA_NAME ": %d tests\n", LIBC_TEST_COUNT);
    for (int i = 0; i < LIBC_TEST_COUNT; ++i) {
        t_status = 0;
        errno = 0; /* libc-test's runner starts every test in a fresh process, whose errno is zero */
        fflush(stdout);
        const int result = libc_tests[i].Main(argc, argv);
        fflush(stdout);
        if (result == 0 && t_status == 0) {
            ++passed;
            printf("[LIBC-TEST] %s: ok\n", libc_tests[i].Name);
        } else {
            printf("[LIBC-TEST] %s: FAILED (result %d, status %d)\n", libc_tests[i].Name, result, t_status);
        }
    }
    if (passed == LIBC_TEST_COUNT) {
        printf("[LIBC-TEST] musl 1.2.5 on " ISA_NAME ": all %d tests passed\n", passed);
    } else {
        printf("[LIBC-TEST] musl 1.2.5 on " ISA_NAME ": %d of %d passed\n", passed, LIBC_TEST_COUNT);
    }
    return LIBC_TEST_COUNT - passed;
}
