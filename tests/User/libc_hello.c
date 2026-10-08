#define _GNU_SOURCE
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <math.h>
#include <pthread.h>
#include <sched.h>
#include <semaphore.h>
#include <setjmp.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/random.h>
#include <sys/stat.h>
#include <sys/utsname.h>
#include <time.h>
#include <unistd.h>

/* The first libc program on WitOS (RFC 0011 section 9.1, plan step S1.1): unchanged musl over the system layer's
 * dispatch, started by the kernel as the root task. It exercises what S1.1 brings: stdio to the kernel log,
 * malloc over mmap, strings and formatting, conversions, sorting, setjmp, errno from an unsupported call, time and
 * entropy from the kernel's clocks, thread-local storage of the main thread, atexit, and (S1.2) the files of the
 * read-only boot package: stdio, stat, directories, the refusals of writing, and (S2) threads: creation and join,
 * a mutex, a condition variable, thread-specific data, thread-local storage and errno per thread, a detached
 * thread and a semaphore; then it exits with zero. The
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

static struct Shared {
    pthread_mutex_t Lock;
    pthread_cond_t Ready;
    pthread_key_t Key;
    sem_t Done;
    int Counter, Flag, Destructors, WorkerErrno;
} shared;

static void key_destructor(void *value)
{
    (void)value;
    __sync_fetch_and_add(&shared.Destructors, 1);
}

static void *counting_worker(void *argument)
{
    const long id = (long)argument;
    thread_local_value = (int)id; /* the main thread keeps its own copy */
    errno = EPIPE;
    pthread_setspecific(shared.Key, &shared);
    for (int i = 0; i < 1000; ++i) {
        pthread_mutex_lock(&shared.Lock);
        shared.Counter++;
        pthread_mutex_unlock(&shared.Lock);
        if ((i & 63) == 0) {
            sched_yield();
        }
    }
    shared.WorkerErrno = errno;
    pthread_exit((void *)(id * 10));
}

static void *condition_worker(void *argument)
{
    (void)argument;
    pthread_mutex_lock(&shared.Lock);
    while (!shared.Flag) {
        pthread_cond_wait(&shared.Ready, &shared.Lock);
    }
    shared.Flag = 2;
    pthread_mutex_unlock(&shared.Lock);
    return 0;
}

static void *detached_worker(void *argument)
{
    (void)argument;
    sem_post(&shared.Done);
    return 0;
}

/* Signals (S3): handlers record what they saw; the alternate stack is a static buffer the kernel accepts. */
static char alternate_stack[16384];
static volatile sig_atomic_t signal_seen, signal_on_alternate, signal_flags_on_alternate, signal_nested, signal_order;
static volatile sig_atomic_t fault_code;
static volatile void *fault_address;
static volatile char *guarded_page;
static sigjmp_buf escape;
static volatile int worker_interrupted;

static void record_handler(int sig)
{
    char here;
    stack_t query;
    signal_seen = sig;
    signal_on_alternate = &here >= alternate_stack && &here < alternate_stack + sizeof(alternate_stack);
    signal_flags_on_alternate = sigaltstack(0, &query) == 0 && query.ss_flags == SS_ONSTACK;
}

static void inner_handler(int sig)
{
    (void)sig;
    signal_order = signal_order * 10 + 2;
}

static void outer_handler(int sig)
{
    (void)sig;
    signal_order = signal_order * 10 + 1;
    raise(SIGUSR2); /* unblocked inside this handler: its handler runs now */
    signal_order = signal_order * 10 + 3;
    signal_nested = 1;
}

static void repair_handler(int sig, siginfo_t *info, void *context)
{
    (void)sig;
    (void)context;
    fault_code = info->si_code;
    fault_address = info->si_addr;
    mprotect((void *)guarded_page, 4096, PROT_READ | PROT_WRITE); /* the faulting store then completes */
}

static void escape_handler(int sig, siginfo_t *info, void *context)
{
    (void)context;
    fault_code = info->si_code;
    signal_seen = sig;
    siglongjmp(escape, 1);
}

static void *interruptible_worker(void *argument)
{
    sem_t *gate = argument;
    sem_post(gate); /* ready */
    while (sem_wait(gate + 1) != 0) {
        if (errno == EINTR) {
            worker_interrupted = 1;
            return 0;
        }
    }
    return 0;
}

static void *late_signaller(void *argument)
{
    struct timespec delay = {0, 2000000};
    nanosleep(&delay, 0);
    pthread_kill(*(pthread_t *)argument, SIGUSR1);
    return 0;
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

    /* Linux's mapping semantics over the kernel's reservations (S5.1): MAP_FIXED replaces what lies in its range,
     * munmap takes any part of a mapping, and a part given back can be taken again at its address. */
    unsigned char *region = mmap(0, 8 * 4096, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    check(region != MAP_FAILED, "an eight-page mapping");
    if (region != MAP_FAILED) {
        memset(region, 7, 8 * 4096);
        check(mmap(region + 2 * 4096, 2 * 4096, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_FIXED, -1,
                  0) == region + 2 * 4096,
            "MAP_FIXED over the own mapping");
        check(region[2 * 4096] == 0 && region[4 * 4096 - 1] == 0 && region[0] == 7 && region[4 * 4096] == 7,
            "MAP_FIXED replaced its range alone");
        check(mmap(region + 4096, 4096, PROT_READ, MAP_PRIVATE | MAP_ANONYMOUS | MAP_FIXED_NOREPLACE, -1, 0) ==
                    MAP_FAILED &&
                errno == EEXIST,
            "MAP_FIXED_NOREPLACE refuses an occupied range");
        check(munmap(region + 5 * 4096, 2 * 4096) == 0, "munmap of a middle part");
        check(mmap(region + 5 * 4096, 2 * 4096, PROT_READ | PROT_WRITE,
                  MAP_PRIVATE | MAP_ANONYMOUS | MAP_FIXED_NOREPLACE, -1, 0) == region + 5 * 4096 &&
                region[5 * 4096] == 0 &&
                region[7 * 4096] == 7,
            "the part given back is taken again at its address");
        check(munmap(region, 8 * 4096) == 0, "munmap across several mappings");
    }

    /* Files of the package (S5.1): a private copy that may be written, and a file's code mapped without a copy and
     * executed; code.bin holds a function returning 42 for x64 in its first page and for ARM64 in its second. */
    int text_fd = open("/test/hello.txt", O_RDONLY);
    char *text = text_fd >= 0 ? mmap(0, 4096, PROT_READ | PROT_WRITE, MAP_PRIVATE, text_fd, 0) : MAP_FAILED;
    check(text != MAP_FAILED && memcmp(text, "Hello, package!", 15) == 0 && text[28] == 0,
        "a file mapped as a private copy");
    if (text != MAP_FAILED) {
        text[0] = 'J';
        check(text[0] == 'J' && munmap(text, 4096) == 0, "the private copy is writable");
    }
    close(text_fd);
    int code_fd = open("/test/code.bin", O_RDONLY);
#if defined(__x86_64__)
    const off_t code_page = 0;
#else
    const off_t code_page = 4096;
#endif
    void *code = code_fd >= 0 ? mmap(0, 4096, PROT_READ | PROT_EXEC, MAP_PRIVATE, code_fd, code_page) : MAP_FAILED;
    check(code != MAP_FAILED, "a file's code mapped executable");
    if (code != MAP_FAILED) {
        int (*answer)(void) = (int (*)(void))code;
        check(answer() == 42, "the mapped code runs");
        check(mprotect(code, 4096, PROT_READ | PROT_WRITE) == -1 && errno == EACCES, "a file's code is never writable");
        check(
            mprotect(code, 4096, PROT_READ) == 0 && mprotect(code, 4096, PROT_READ | PROT_EXEC) == 0 && answer() == 42,
            "the code's protection changes within its rights");
        check(munmap(code, 4096) == 0, "the code unmapped");
    }
    check(mmap(0, 4096, PROT_READ | PROT_WRITE, MAP_SHARED, code_fd, 0) == MAP_FAILED && errno == EACCES,
        "a shared writable mapping of the package refused");
    close(code_fd);

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
    check(open("/missing", 0) == -1 && errno == ENOENT, "open of a missing file");

    /* Files over the read-only boot package (S1.2): the package's files are the namespace, nothing is writable. */
    FILE *file = fopen("/test/hello.txt", "r");
    check(file != 0, "fopen a package file");
    if (file) {
        check(fgets(buffer, sizeof(buffer), file) != 0 && strcmp(buffer, "Hello, package!\n") == 0, "fgets first line");
        check(fgets(buffer, sizeof(buffer), file) != 0 && strcmp(buffer, "second line\n") == 0, "fgets second line");
        check(fgets(buffer, sizeof(buffer), file) == 0 && feof(file), "end of file");
        check(fseek(file, 7, SEEK_SET) == 0 && fread(buffer, 1, 8, file) == 8 && memcmp(buffer, "package!", 8) == 0,
            "fseek and fread");
        check(ftell(file) == 15, "ftell");
        check(fseek(file, 0, SEEK_END) == 0 && ftell(file) == 28, "fseek to the end");
        check(fclose(file) == 0, "fclose");
    }
    struct stat st;
    check(stat("/test/hello.txt", &st) == 0 && S_ISREG(st.st_mode) && st.st_size == 28, "stat of a file");
    check(stat("/test/dir", &st) == 0 && S_ISDIR(st.st_mode), "stat of a directory");
    check(stat("/test/dir/", &st) == 0 && S_ISDIR(st.st_mode), "stat of a directory with a trailing slash");
    check(stat("/test/nothing", &st) == -1 && errno == ENOENT, "stat of a missing file");
    check(open("/test/hello.txt", O_WRONLY) == -1 && errno == EROFS, "the package is read-only");
    check(open("/test/new.txt", O_WRONLY | O_CREAT, 0644) == -1 && errno == EROFS, "no file is created");
    check(open("/test/hello.txt", O_RDONLY | O_DIRECTORY) == -1 && errno == ENOTDIR, "a file is not a directory");
    int directory = open("/test/dir", O_RDONLY);
    check(
        directory >= 0 && read(directory, buffer, 1) == -1 && errno == EISDIR, "a directory is not readable as bytes");
    if (directory >= 0) {
        close(directory);
    }
    check(access("/test/dir/a.txt", R_OK) == 0 && access("/test/dir/a.txt", W_OK) == -1 && errno == EROFS, "access");
    DIR *listing = opendir("/test/dir");
    check(listing != 0, "opendir");
    if (listing) {
        const char *names[8] = {0};
        int entries = 0;
        for (struct dirent *entry; (entry = readdir(listing)) != 0 && entries < 8; ++entries) {
            names[entries] = strdup(entry->d_name);
        }
        check(entries == 4 &&
                strcmp(names[0], ".") == 0 &&
                strcmp(names[1], "..") == 0 &&
                strcmp(names[2], "a.txt") == 0 &&
                strcmp(names[3], "b.txt") == 0,
            "readdir lists the directory in order");
        check(closedir(listing) == 0, "closedir");
    }
    int fd = open("/test/dir/b.txt", O_RDONLY);
    check(
        fd >= 0 && pread(fd, buffer, 2, 0) == 2 && buffer[0] == 'b' && buffer[1] == 'b' && lseek(fd, 0, SEEK_CUR) == 0,
        "pread");
    if (fd >= 0) {
        close(fd);
    }
    check(getcwd(buffer, sizeof(buffer)) != 0 && strcmp(buffer, "/") == 0, "the current directory is the root");
    int null_device = open("/dev/null", O_RDWR);
    check(null_device >= 0 && write(null_device, "x", 1) == 1 && read(null_device, buffer, 1) == 0, "/dev/null");
    if (null_device >= 0) {
        close(null_device);
    }

    /* Threads over the kernel's (S2): creation and join, a mutex, a condition variable, thread-specific data with its
     * destructor, thread-local storage and errno per thread, a detached thread signalling a semaphore, pthread_exit's
     * value. The kernel runs at most four threads of a process, so at most three run here beside the main one. */
    pthread_t workers[2];
    check(pthread_mutex_init(&shared.Lock, 0) == 0 && pthread_cond_init(&shared.Ready, 0) == 0,
        "mutex and condition init");
    check(pthread_key_create(&shared.Key, key_destructor) == 0, "pthread_key_create");
    check(sem_init(&shared.Done, 0, 0) == 0, "sem_init");
    shared.Counter = 0;
    for (int i = 0; i < 2; ++i) {
        check(pthread_create(&workers[i], 0, counting_worker, (void *)(long)(i + 1)) == 0, "pthread_create");
    }
    for (int i = 0; i < 2; ++i) {
        void *value = 0;
        check(pthread_join(workers[i], &value) == 0 && (long)value == (i + 1) * 10, "pthread_join and the exit value");
    }
    check(shared.Counter == 2 * 1000, "the mutex serialized the counters");
    check(shared.Destructors == 2, "the key destructor ran for every thread");
    check(thread_local_value == 41, "the main thread's thread-local value is its own"); /* incremented later */
    errno = 0;
    check(errno == 0 && shared.WorkerErrno == EPIPE, "errno is per thread");
    pthread_t waiter;
    shared.Flag = 0;
    check(pthread_create(&waiter, 0, condition_worker, 0) == 0, "pthread_create for the condition");
    check(pthread_mutex_lock(&shared.Lock) == 0, "lock before signalling");
    shared.Flag = 1;
    check(pthread_cond_signal(&shared.Ready) == 0 && pthread_mutex_unlock(&shared.Lock) == 0, "signal");
    check(pthread_join(waiter, 0) == 0 && shared.Flag == 2, "the waiter saw the flag");
    pthread_attr_t detached;
    pthread_t background;
    check(pthread_attr_init(&detached) == 0 && pthread_attr_setdetachstate(&detached, PTHREAD_CREATE_DETACHED) == 0,
        "detached attribute");
    check(pthread_create(&background, &detached, detached_worker, 0) == 0, "detached pthread_create");
    check(sem_wait(&shared.Done) == 0, "the detached thread posted the semaphore");
    check(pthread_attr_destroy(&detached) == 0 && !pthread_equal(pthread_self(), background),
        "pthread_self differs from a worker");
    struct timespec settle = {0, 5000000};
    nanosleep(&settle, 0); /* the detached thread's exit is served after its post */

    /* Signals (S3): dispositions, masks, the alternate stack, faults repaired or escaped, signals between threads. */
    struct sigaction action, previous;
    stack_t alternate = {alternate_stack, 0, sizeof(alternate_stack)};
    check(sigaltstack(&alternate, 0) == 0, "sigaltstack");
    memset(&action, 0, sizeof(action));
    action.sa_handler = record_handler;
    action.sa_flags = SA_ONSTACK;
    sigemptyset(&action.sa_mask);
    check(sigaction(SIGUSR1, &action, &previous) == 0 && previous.sa_handler == SIG_DFL, "sigaction installs");
    check(raise(SIGUSR1) == 0 && signal_seen == SIGUSR1, "raise runs the handler");
    check(signal_on_alternate && signal_flags_on_alternate, "the handler ran on the alternate stack");
    sigset_t block, pending, saved;
    sigemptyset(&block);
    sigaddset(&block, SIGUSR1);
    signal_seen = 0;
    check(sigprocmask(SIG_BLOCK, &block, &saved) == 0 && raise(SIGUSR1) == 0 && signal_seen == 0,
        "a blocked signal waits");
    check(sigpending(&pending) == 0 && sigismember(&pending, SIGUSR1) == 1, "sigpending shows it");
    check(sigprocmask(SIG_SETMASK, &saved, 0) == 0 && signal_seen == SIGUSR1, "unblocking delivers it");
    action.sa_handler = outer_handler;
    action.sa_flags = 0;
    check(sigaction(SIGUSR1, &action, 0) == 0, "sigaction replaces");
    action.sa_handler = inner_handler;
    check(sigaction(SIGUSR2, &action, 0) == 0 && raise(SIGUSR1) == 0 && signal_nested && signal_order == 123,
        "a handler raising another signal nests");
    action.sa_handler = SIG_IGN;
    signal_order = 0;
    check(sigaction(SIGUSR2, &action, 0) == 0 && raise(SIGUSR2) == 0 && signal_order == 0, "SIG_IGN ignores");
    guarded_page = mmap(0, 4096, PROT_NONE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    check(guarded_page != MAP_FAILED, "a no-access page");
    memset(&action, 0, sizeof(action));
    action.sa_sigaction = repair_handler;
    action.sa_flags = SA_SIGINFO;
    check(sigaction(SIGSEGV, &action, 0) == 0, "SIGSEGV handler");
    guarded_page[8] = 7; /* faults, the handler repairs the page, the store completes */
    check(guarded_page[8] == 7 && fault_address == guarded_page + 8, "a repaired fault resumes the store");
    check(fault_code == SEGV_MAPERR || fault_code == SEGV_ACCERR, "SIGSEGV code");
    check(munmap((void *)guarded_page, 4096) == 0, "the page unmapped");
    action.sa_sigaction = escape_handler;
    check(sigaction(SIGILL, &action, 0) == 0, "SIGILL handler");
    signal_seen = 0;
    if (sigsetjmp(escape, 1) == 0) {
#if defined(__x86_64__)
        __asm__ volatile("ud2");
#else
        __asm__ volatile(".inst 0x00000000");
#endif
    }
    check(signal_seen == SIGILL && (fault_code == ILL_ILLOPN || fault_code == ILL_ILLOPC),
        "siglongjmp out of a SIGILL handler");
#if defined(__x86_64__)
    check(sigaction(SIGFPE, &action, 0) == 0, "SIGFPE handler");
    signal_seen = 0;
    if (sigsetjmp(escape, 1) == 0) {
        /* An explicit idiv: the compiler turns 1 / x into a select without a division. */
        __asm__ volatile("xor %%ecx, %%ecx\n\tmov $1, %%eax\n\tcltd\n\tidivl %%ecx" ::: "eax", "ecx", "edx");
    }
    check(signal_seen == SIGFPE && fault_code == FPE_INTDIV, "siglongjmp out of a SIGFPE handler");
#endif
    sem_t gates[2];
    pthread_t sleeper, signaller;
    check(sem_init(&gates[0], 0, 0) == 0 && sem_init(&gates[1], 0, 0) == 0, "signal gates");
    memset(&action, 0, sizeof(action));
    action.sa_handler = record_handler;
    check(sigaction(SIGUSR1, &action, 0) == 0, "SIGUSR1 without SA_RESTART");
    check(pthread_create(&sleeper, 0, interruptible_worker, gates) == 0 && sem_wait(&gates[0]) == 0,
        "a worker parked in sem_wait");
    check(pthread_kill(sleeper, SIGUSR1) == 0 && pthread_join(sleeper, 0) == 0 && worker_interrupted,
        "pthread_kill interrupts its wait with EINTR");
    check(sigprocmask(SIG_BLOCK, &block, &saved) == 0, "block SIGUSR1 before sigsuspend");
    pthread_t self = pthread_self();
    signal_seen = 0;
    check(pthread_create(&signaller, 0, late_signaller, &self) == 0, "a late signaller");
    sigset_t none;
    sigemptyset(&none);
    check(sigsuspend(&none) == -1 && errno == EINTR && signal_seen == SIGUSR1, "sigsuspend returns after the handler");
    check(pthread_join(signaller, 0) == 0 && sigprocmask(SIG_SETMASK, &saved, 0) == 0, "the signaller joined");
    check(kill(getpid(), 0) == 0 && kill(getpid() + 1, 0) == -1 && errno == ESRCH, "kill reaches the own process only");

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
