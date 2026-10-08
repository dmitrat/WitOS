#define _GNU_SOURCE
#include "witos/memory_info.h"
#include "witos/syscall.h"
#include "witos/user_abi.h"
#include <dlfcn.h>
#include <link.h>
#include <pthread.h>
#include <semaphore.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The dynamic program of the spawn scenario (plan step S5.3, /bin/dynamic): a position-independent executable whose
 * PT_INTERP names musl's dynamic linker, libc.so, in the boot package. The root task's loader maps both and starts the
 * linker, which takes the start message, maps the program's DT_NEEDED library from the package, binds and relocates
 * everything and enters the program; the root task also runs the linker as a command that loads the program itself.
 * The program checks the library's data, functions, constructor and thread-local variable, dladdr and
 * dl_iterate_phdr, a TLS module opened with dlopen and reached from a thread that existed before it and from one
 * created after it, dlsym and dlerror, and a signal; then it reports the kernel reservations its mappings take and
 * exits with argv[1]. A failed check exits with 1. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#else
#define ISA_NAME "aarch64"
#endif

extern int dynamic_counter;
int dynamic_constructed(void);
int dynamic_answer(int value);
int *dynamic_tls_address(void);

static int checks;
static volatile sig_atomic_t signalled;
static sem_t plugin_ready;
static int (*plugin_add)(int);

static void check(int condition, const char *what)
{
    ++checks;
    if (!condition) {
        printf("[DYNAMIC] FAILED: %s (%s)\n", what, dlerror() ? "dlerror set" : "no dlerror");
        exit(1);
    }
}

static void *library_thread(void *argument)
{
    (void)argument;
    int *own = dynamic_tls_address();
    const int initial = *own;
    *own = 9;
    return (void *)(long)(initial * 10 + *own);
}

/* A thread that exists before dlopen: the module's TLS reaches it through what dlopen installed. */
static void *early_thread(void *argument)
{
    (void)argument;
    while (sem_wait(&plugin_ready) != 0) {
    }
    return (void *)(long)plugin_add(3);
}

static void *late_thread(void *argument)
{
    (void)argument;
    return (void *)(long)plugin_add(2);
}

static int count_module(struct dl_phdr_info *info, size_t size, void *data)
{
    (void)size;
    int *found = data;
    if (info->dlpi_name && strstr(info->dlpi_name, "libdynamic.so")) {
        found[0] = 1;
    }
    if (info->dlpi_name && strstr(info->dlpi_name, "ld-musl-")) {
        found[1] = 1;
    }
    ++found[2];
    return 0;
}

static void on_signal(int sig)
{
    signalled = sig;
}

int main(int argc, char **argv)
{
    printf("[DYNAMIC] started on " ISA_NAME "\n");
    check(argc == 2, "the arguments");

    /* The needed library: its constructor ran, its data and functions are bound, its TLS is per thread. */
    check(dynamic_constructed() == 1, "the library's constructor");
    check(dynamic_answer(2) == 42, "a function of the library");
    ++dynamic_counter;
    check(dynamic_answer(2) == 43, "the library's data written by the program");
    check(*dynamic_tls_address() == 5, "the library's TLS in the main thread");
    pthread_t thread;
    void *result = 0;
    check(pthread_create(&thread, 0, library_thread, 0) == 0 && pthread_join(thread, &result) == 0, "a thread");
    check((long)result == 59 && *dynamic_tls_address() == 5, "the library's TLS per thread");
    Dl_info info;
    check(dladdr((void *)dynamic_answer, &info) && info.dli_fname && strcmp(info.dli_fname, "/lib/libdynamic.so") == 0,
        "dladdr of the library's function");
    int found[3] = {0, 0, 0};
    dl_iterate_phdr(count_module, found);
    check(found[0] && found[1] && found[2] >= 3, "dl_iterate_phdr");
    check(dlsym(RTLD_DEFAULT, "dynamic_answer") == (void *)dynamic_answer, "dlsym of a loaded symbol");

    /* A TLS module opened after a thread started. */
    pthread_t early = 0, late = 0;
    check(sem_init(&plugin_ready, 0, 0) == 0 && pthread_create(&early, 0, early_thread, 0) == 0, "the early thread");
    check(dlopen("libmissing.so", RTLD_NOW) == 0 && dlerror() != 0, "dlopen of a missing library");
    void *plugin = dlopen("libplugin.so", RTLD_NOW);
    check(plugin != 0, "dlopen of the plugin");
    plugin_add = (int (*)(int))dlsym(plugin, "plugin_tls_add");
    const int *value = dlsym(plugin, "plugin_value");
    check(plugin_add != 0 && value != 0 && *value == 100, "dlsym of the plugin's symbols");
    check(dlsym(plugin, "plugin_missing") == 0 && dlerror() != 0, "dlsym of a missing symbol");
    check(plugin_add(1) == 12, "the plugin's TLS in the main thread");
    sem_post(&plugin_ready);
    check(
        pthread_join(early, &result) == 0 && (long)result == 14, "the plugin's TLS in a thread older than the module");
    check(pthread_create(&late, 0, late_thread, 0) == 0 && pthread_join(late, &result) == 0 && (long)result == 13,
        "the plugin's TLS in a thread younger than the module");
    check(plugin_add(0) == 12, "the main thread's copy kept");
    check(dlopen("libplugin.so", RTLD_NOW) == plugin, "a second dlopen of the same library");

    /* A signal through the fault callback the linker's start registered. */
    struct sigaction action;
    memset(&action, 0, sizeof(action));
    action.sa_handler = on_signal;
    check(sigaction(SIGUSR1, &action, 0) == 0 && raise(SIGUSR1) == 0 && signalled == SIGUSR1, "a raised signal");

    WitUserMemoryInfo memory;
    WitU64 copied = 0;
    memset(&memory, 0, sizeof(memory));
    check(wit_syscall(WIT_CALL_MEMORY_QUERY, (WitU64)&memory, sizeof(memory), WIT_MEMORY_INFO_VERSION, &copied) ==
            WIT_STATUS_OK,
        "MEMORY_QUERY");
    printf("[DYNAMIC] dynamic program on " ISA_NAME ": %d checks passed, %u of %u reservations, exiting with %s\n",
        checks, memory.ReservationCount, memory.ReservationCapacity, argv[1]);
    return atoi(argv[1]);
}
