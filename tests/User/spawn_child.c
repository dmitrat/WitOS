#define _GNU_SOURCE
#include "witos/start.h"
#include "witos/user_layout.h"
#include <pthread.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/auxv.h>
#include <unistd.h>

/* The program the spawn scenario starts (plan step S5.2): a static position-independent executable that the root
 * task's loader (libwitos) maps into a new process, relocated by musl's dlstart.c at its start (rcrt1.c), with its
 * arguments and environment on the stack the loader built and the kernel log and the boot package as its only
 * capabilities. argv[1] names what it does: "run" checks the relocations, the auxiliary vector, the environment, a
 * package file, thread-local storage in a second thread, the heap and a signal, then exits with argv[2]; "abort"
 * aborts (the root expects 128 + SIGABRT); "wait" waits until the root task kills it. A failed check exits with 1. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#else
#define ISA_NAME "aarch64"
#endif

static int checks;
static _Thread_local int tls_value = 42;
static volatile sig_atomic_t signalled;

static int answer(void)
{
    return 42;
}

/* Data that holds addresses: relative relocations, which dlstart.c applies before anything runs. */
static int (*const functions[])(void) = {answer};
static const char *const texts[] = {"relocated"};

static void check(int condition, const char *what)
{
    ++checks;
    if (!condition) {
        printf("[CHILD] FAILED: %s\n", what);
        exit(1);
    }
}

static void *thread_main(void *argument)
{
    (void)argument;
    const int initial = tls_value;
    tls_value = 7;
    return (void *)(intptr_t)(initial * 100 + tls_value);
}

static void on_signal(int sig)
{
    signalled = sig;
}

static int run(int argc, char **argv)
{
    check(argc == 3 && strcmp(argv[0], "/bin/child") == 0, "the arguments");
    check(functions[0] == answer && functions[0]() == 42, "a relocated function pointer");
    check(strcmp(texts[0], "relocated") == 0, "a relocated string pointer");

    /* The auxiliary vector the loader built: the program headers inside the image at the code arena's start, the
     * entry, the page size and the start endpoint (closed by the library once the start message arrived). */
    const unsigned long headers = getauxval(AT_PHDR), entry = getauxval(AT_ENTRY);
    check(headers >= WIT_USER_CODE_BASE && headers < WIT_USER_CODE_BASE + (64UL << 20), "AT_PHDR in the image");
    check(entry >= WIT_USER_CODE_BASE && entry < WIT_USER_CODE_BASE + (64UL << 20), "AT_ENTRY in the image");
    check((unsigned long)&answer >= WIT_USER_CODE_BASE && (unsigned long)&answer < WIT_USER_CODE_BASE + (64UL << 20),
        "the code at the image's base");
    check(getauxval(AT_PAGESZ) == 4096 && getauxval(WIT_AT_START) != 0, "AT_PAGESZ and WIT_AT_START");
    check(getauxval(AT_BASE) == 0, "no AT_BASE: no interpreter");

    /* The environment the root task passed. */
    const char *value = getenv("WITOS_SPAWN");
    check(value && strcmp(value, "loader") == 0 && getenv("HOME") == 0, "the environment");

    /* A file of the boot package, through the delegated package. */
    char line[64] = {0};
    FILE *file = fopen("/test/hello.txt", "r");
    check(file != 0, "fopen of a package file");
    check(fgets(line, sizeof(line), file) && strcmp(line, "Hello, package!\n") == 0, "the package file's first line");
    fclose(file);

    /* Thread-local storage: the initial image in a new thread, separate values per thread. */
    pthread_t thread;
    void *result = 0;
    check(pthread_create(&thread, 0, thread_main, 0) == 0, "pthread_create");
    check(pthread_join(thread, &result) == 0 && (intptr_t)result == 4207, "the thread's TLS");
    check(tls_value == 42, "the main thread's TLS");

    /* The heap, and a signal through the fault callback the library registered at the start. */
    unsigned char *block = malloc(1 << 20);
    check(block != 0, "malloc of a MiB");
    memset(block, 0x5A, 1 << 20);
    check(block[(1 << 20) - 1] == 0x5A, "the heap's pages");
    free(block);
    struct sigaction action;
    memset(&action, 0, sizeof(action));
    action.sa_handler = on_signal;
    check(sigaction(SIGUSR1, &action, 0) == 0, "sigaction");
    check(raise(SIGUSR1) == 0 && signalled == SIGUSR1, "a raised signal");

    printf("[CHILD] %d checks passed, exiting with %s\n", checks, argv[2]);
    return atoi(argv[2]);
}

int main(int argc, char **argv)
{
    printf("[CHILD] started on " ISA_NAME " with %d arguments\n", argc);
    if (argc > 1 && strcmp(argv[1], "abort") == 0) {
        abort();
    }
    if (argc > 1 && strcmp(argv[1], "wait") == 0) {
        for (;;) {
            pause();
        }
    }
    if (argc > 1 && strcmp(argv[1], "run") == 0) {
        return run(argc, argv);
    }
    return 1;
}
