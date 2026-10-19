#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <poll.h>
#include <pthread.h>
#include <sched.h>
#include <signal.h>
#include <spawn.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/auxv.h>
#include <sys/epoll.h>
#include <sys/mman.h>
#include <sys/resource.h>
#include <sys/sysinfo.h>
#include <sys/wait.h>
#include <unistd.h>

/* /bin/init of the process scenario (plan step S6.1): the first process the root task's process manager starts. It
 * starts /bin/child (tests/User/process_child.c) with posix_spawn and posix_spawnp and waits with waitpid and wait:
 * exit statuses, the environment it passes, a grandchild started through a child's own capability to the manager, a
 * death by SIGABRT, more children at once than one kernel wait holds, a WNOHANG poll of a running child, and the
 * refusals of a missing file and a file that is no program (S6.1); its own current directory through chdir and fchdir,
 * a child that inherits it or starts where a chdir action puts it, a program path relative to that directory, and the
 * standard streams a child starts without or rebinds through file actions, with the actions that are not there refused
 * (S6.2); pipes as a child's standard streams, the end of the file across the processes, a transfer many times the
 * pipe's buffer, poll on a pipe a child closed, epoll over pipe ends, SIGCHLD, waitid and kill of a child (N4.2). What
 * the system layer
 * reports of the machine and of the process (R2.1): the physical memory through sysinfo
 * and sysconf, the address space of anonymous memory as RLIMIT_AS, the processors threads run on, the main thread's
 * stack as musl measures it, a thread's name, and a read-only page of the program made writable, as NativeAOT's runtime
 * makes the page of its GS cookie. The last line names the ISA and the checks; a failed check exits with 1, which the
 * root task reports as a failed boot. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#elif defined(__aarch64__)
#define ISA_NAME "aarch64"
#else
#define ISA_NAME "riscv64"
#endif

#define CHILD "/bin/child"
#define AT_ONCE 5 /* children at once: the root task, init and five fill seven of the kernel's eight processes */
#define TRANSFER (512UL * 1024UL) /* bytes through a child, many times the pipe's buffer (N4.2) */

extern char **environ;
static int checks;
static volatile sig_atomic_t sigchld_count;

static void count_sigchld(int sig)
{
    (void)sig;
    ++sigchld_count;
}

/* Reads until the end of the file: the bytes read, or -1. */
static long read_all(int fd, unsigned char *buffer, size_t size)
{
    size_t total = 0;
    for (;;) {
        const ssize_t got = read(fd, buffer + total, size - total);
        if (got < 0) {
            return -1;
        }
        if (got == 0 || total + (size_t)got == size) {
            return (long)(total + (size_t)got);
        }
        total += (size_t)got;
    }
}

/* Writes TRANSFER bytes of the pattern (byte i is i & 0xFF) to the descriptor given and closes it. */
static void *fill(void *argument)
{
    const int fd = *(const int *)argument;
    unsigned char block[4096];
    for (unsigned long total = 0; total < TRANSFER;) {
        const size_t piece = TRANSFER - total < sizeof(block) ? (size_t)(TRANSFER - total) : sizeof(block);
        for (size_t i = 0; i < piece; ++i) {
            block[i] = (unsigned char)((total + i) & 0xFF);
        }
        for (size_t done = 0; done < piece;) {
            const ssize_t written = write(fd, block + done, piece - done);
            if (written <= 0) {
                return (void *)1;
            }
            done += (size_t)written;
        }
        total += piece;
    }
    close(fd);
    return 0;
}

static void check(int condition, const char *what)
{
    ++checks;
    if (!condition) {
        printf("[INIT] FAILED: %s\n", what);
        exit(1);
    }
}

/* Starts the child with the arguments and returns its pid. */
static pid_t start(char *const argv[], char *const envp[])
{
    pid_t pid = 0;
    const int error = posix_spawn(&pid, CHILD, 0, 0, argv, envp);
    if (error) {
        printf("[INIT] posix_spawn: %s\n", strerror(error));
    }
    check(error == 0 && pid > 1, "posix_spawn");
    return pid;
}

static int finish(pid_t pid)
{
    int status = -1;
    check(waitpid(pid, &status, 0) == pid, "waitpid");
    return status;
}

int main(void)
{
    printf("[INIT] started on " ISA_NAME "\n");
    check(getenv("PATH") && strcmp(getenv("PATH"), "/bin:/usr/bin") == 0, "the root task's environment");
    /* The program's path in the auxiliary vector (R3.2), where .NET looks for its executable without /proc. */
    const char *executable = (const char *)getauxval(AT_EXECFN);
    char resolved[PATH_MAX];
    check(executable &&
            strcmp(executable, "/bin/init") == 0 &&
            realpath(executable, resolved) &&
            strcmp(resolved, "/bin/init") == 0,
        "AT_EXECFN names the program");

    /* An exit status. */
    char *exit_three[] = {"child", "exit", "3", 0};
    int status = finish(start(exit_three, environ));
    check(WIFEXITED(status) && WEXITSTATUS(status) == 3, "an exit status");

    /* posix_spawnp finds the program through PATH. */
    char *exit_zero[] = {"child", "exit", "0", 0};
    pid_t pid = 0;
    check(posix_spawnp(&pid, "child", 0, 0, exit_zero, environ) == 0, "posix_spawnp");
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "posix_spawnp's child");

    /* The environment a child receives is the one passed. */
    char *environment_check[] = {"child", "env", "WITOS_TEST", "spawned", 0};
    char *environment[] = {"WITOS_TEST=spawned", 0};
    status = finish(start(environment_check, environment));
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "the environment passed");

    /* A grandchild through the child's own capability to the manager. */
    char *spawning[] = {"child", "spawn", 0};
    status = finish(start(spawning, environ));
    check(WIFEXITED(status) && WEXITSTATUS(status) == 5, "a grandchild");

    /* A death by a signal's default action. */
    char *aborting[] = {"child", "abort", 0};
    status = finish(start(aborting, environ));
    check(WIFSIGNALED(status) && WTERMSIG(status) == SIGABRT, "a death by SIGABRT");

    /* More children at once than one kernel wait holds: wait reaps each, then reports none left. */
    int seen = 0;
    for (int i = 0; i < AT_ONCE; ++i) {
        char code[4];
        snprintf(code, sizeof(code), "%d", 10 + i);
        char *exiting[] = {"child", "exit", code, 0};
        start(exiting, environ);
    }
    for (int i = 0; i < AT_ONCE; ++i) {
        check(wait(&status) > 1 && WIFEXITED(status), "wait");
        seen |= 1 << (WEXITSTATUS(status) - 10);
    }
    check(seen == (1 << AT_ONCE) - 1 && wait(&status) == -1 && errno == ECHILD, "every child reaped once");

    /* WNOHANG returns at once while the child runs. */
    char *sleeping[] = {"child", "sleep", "50", "9", 0};
    pid = start(sleeping, environ);
    check(waitpid(pid, &status, WNOHANG) == 0, "WNOHANG while the child runs");
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 9, "the slept child");

    /* Refusals. */
    check(posix_spawn(&pid, "/bin/none", 0, 0, exit_zero, environ) == ENOENT, "a missing file");
    check(posix_spawn(&pid, "/test/hello.txt", 0, 0, exit_zero, environ) == ENOEXEC, "a file that is no program");

    /* The current directory (S6.2): chdir, relative paths and "..", fchdir, and the refusals. */
    char directory[PATH_MAX];
    check(getcwd(directory, sizeof(directory)) && strcmp(directory, "/") == 0, "the initial directory");
    check(chdir("/test") == 0 && getcwd(directory, sizeof(directory)) && strcmp(directory, "/test") == 0, "chdir");
    check(access("hello.txt", F_OK) == 0 && access("dir/a.txt", F_OK) == 0, "relative paths");
    check(chdir("dir") == 0 && access("../hello.txt", F_OK) == 0 && chdir("..") == 0, "a relative chdir and ..");
    check(chdir("hello.txt") == -1 && errno == ENOTDIR && chdir("/none") == -1 && errno == ENOENT, "chdir refusals");
    const int dir = open("/test/dir", O_RDONLY | O_DIRECTORY);
    check(dir >= 0 &&
            fchdir(dir) == 0 &&
            getcwd(directory, sizeof(directory)) &&
            strcmp(directory, "/test/dir") == 0 &&
            chdir("/test") == 0,
        "fchdir");
    check(openat(dir, "../hello.txt", O_RDONLY) >= 0, "a path from a directory descriptor through ..");

    /* A child inherits the directory, a chdir or fchdir action moves it, and a relative program path starts there. */
    char *in_test[] = {"child", "cwd", "/test", "hello.txt", 0};
    status = finish(start(in_test, environ));
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "a child in the inherited directory");
    posix_spawn_file_actions_t actions;
    char *in_dir[] = {"child", "cwd", "/test/dir", "a.txt", 0};
    check(posix_spawn_file_actions_init(&actions) == 0 &&
            posix_spawn_file_actions_addchdir_np(&actions, "dir") == 0 &&
            posix_spawn(&pid, CHILD, &actions, 0, in_dir, environ) == 0,
        "a chdir action");
    posix_spawn_file_actions_destroy(&actions);
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "a child where the chdir action put it");
    const int root = open("/", O_RDONLY | O_DIRECTORY);
    char *in_root[] = {"child", "cwd", "/", "bin/child", 0};
    check(root >= 0 &&
            posix_spawn_file_actions_init(&actions) == 0 &&
            posix_spawn_file_actions_addfchdir_np(&actions, root) == 0 &&
            posix_spawn(&pid, "bin/child", &actions, 0, in_root, environ) == 0,
        "an fchdir action and a program path relative to it");
    posix_spawn_file_actions_destroy(&actions);
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "a child in the root through a relative program path");

    /* Standard streams as the log capability, one by one: closed, rebound, and the actions that are not there. */
    char *streams[] = {"child", "streams", 0};
    status = finish(start(streams, environ));
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "both streams inherited");
    check(posix_spawn_file_actions_init(&actions) == 0 &&
            posix_spawn_file_actions_addclose(&actions, 1) == 0 &&
            posix_spawn_file_actions_addclose(&actions, 7) == 0 &&
            posix_spawn(&pid, CHILD, &actions, 0, streams, environ) == 0,
        "a closed output");
    posix_spawn_file_actions_destroy(&actions);
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 1, "a child without standard output");
    check(posix_spawn_file_actions_init(&actions) == 0 &&
            posix_spawn_file_actions_addclose(&actions, 2) == 0 &&
            posix_spawn_file_actions_adddup2(&actions, 1, 2) == 0 &&
            posix_spawn(&pid, CHILD, &actions, 0, streams, environ) == 0,
        "an error rebound to the output");
    posix_spawn_file_actions_destroy(&actions);
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "a child with its error rebound");
    check(posix_spawn_file_actions_init(&actions) == 0 &&
            posix_spawn_file_actions_adddup2(&actions, dir, 1) == 0 &&
            posix_spawn(&pid, CHILD, &actions, 0, streams, environ) == ENOSYS,
        "a descriptor as a stream refused");
    posix_spawn_file_actions_destroy(&actions);
    check(posix_spawn_file_actions_init(&actions) == 0 &&
            posix_spawn_file_actions_addopen(&actions, 0, "/test/hello.txt", O_RDONLY, 0) == 0 &&
            posix_spawn(&pid, CHILD, &actions, 0, streams, environ) == ENOSYS,
        "a file as a stream refused");
    posix_spawn_file_actions_destroy(&actions);
    char byte;
    check(close(0) == 0 && read(0, &byte, 1) == -1 && errno == EBADF && close(0) == -1 && errno == EBADF,
        "the own standard input closed");
    char *closed_input[] = {"child", "stdin", 0};
    status = finish(start(closed_input, environ));
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "a child inherits the closed input");
    check(waitpid(-1, &status, WNOHANG) == -1 && errno == ECHILD, "no child left");

    /* Pipes between processes (N4.2): a child's output read through a pipe, then the end of the file; its input
     * written through one and copied back; both outputs on one pipe; a large transfer through a copying child; and
     * poll's view of a far end that closed. */
    int out[2], in[2];
    unsigned char buffer[256];
    check(pipe(out) == 0, "a pipe");
    check(posix_spawn_file_actions_init(&actions) == 0 &&
            posix_spawn_file_actions_adddup2(&actions, out[1], 1) == 0 &&
            posix_spawn(&pid, CHILD, &actions, 0, streams, environ) == 0,
        "a pipe as the child's output");
    posix_spawn_file_actions_destroy(&actions);
    check(close(out[1]) == 0 &&
            read_all(out[0], buffer, sizeof(buffer)) == 24 &&
            memcmp(buffer, "[CHILD] standard output\n", 24) == 0,
        "the child's output through the pipe, then the end of the file");
    check(close(out[0]) == 0, "the read end closed");
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "the child with a pipe for its output");
    char *copying[] = {"child", "cat", 0};
    check(pipe(in) == 0 &&
            pipe(out) == 0 &&
            posix_spawn_file_actions_init(&actions) == 0 &&
            posix_spawn_file_actions_adddup2(&actions, in[0], 0) == 0 &&
            posix_spawn_file_actions_adddup2(&actions, out[1], 1) == 0 &&
            posix_spawn(&pid, CHILD, &actions, 0, copying, environ) == 0,
        "pipes as the child's input and output");
    posix_spawn_file_actions_destroy(&actions);
    check(close(in[0]) == 0 && close(out[1]) == 0, "the caller's far ends closed");
    check(write(in[1], "hello through a pipe\n", 21) == 21 &&
            read(out[0], buffer, sizeof(buffer)) == 21 &&
            memcmp(buffer, "hello through a pipe\n", 21) == 0,
        "a line through the child");
    check(close(in[1]) == 0 && read_all(out[0], buffer, sizeof(buffer)) == 0 && close(out[0]) == 0,
        "the end of the input ends the child's output");
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "the copying child");

    /* epoll over pipe ends (N4.2): what System.IO.Pipes' asynchronous reads need under CoreCLR. A local pipe: the read
     * end registered edge-triggered reports EPOLLIN once per write, a level-triggered one while bytes remain, the
     * write end EPOLLOUT at its registration, a closed writer EPOLLHUP; a file is EPERM, the instance in itself
     * EINVAL, a closed descriptor EBADF. A shared pipe: the copying child's answer reaches an edge-triggered wait, and
     * its exit reaches it as EPOLLHUP. */
    {
        int local[2], ep = -1, file;
        struct epoll_event ev, got[4];
        check(pipe(local) == 0 && (ep = epoll_create1(EPOLL_CLOEXEC)) >= 0, "an epoll instance");
        ev.events = EPOLLIN | EPOLLET;
        ev.data.u64 = 1;
        check(epoll_ctl(ep, EPOLL_CTL_ADD, local[0], &ev) == 0, "the read end registered edge-triggered");
        check(epoll_ctl(ep, EPOLL_CTL_ADD, local[0], &ev) == -1 && errno == EEXIST, "a second registration is EEXIST");
        check(epoll_wait(ep, got, 4, 0) == 0, "nothing to read yet");
        check(write(local[1], "x", 1) == 1 &&
                epoll_wait(ep, got, 4, 1000) == 1 &&
                got[0].data.u64 == 1 &&
                got[0].events == EPOLLIN,
            "a write is an edge");
        check(epoll_wait(ep, got, 4, 0) == 0, "no second report of one edge");
        check(write(local[1], "y", 1) == 1 && epoll_wait(ep, got, 4, 1000) == 1, "another write is another edge");
        ev.events = EPOLLIN;
        check(epoll_ctl(ep, EPOLL_CTL_MOD, local[0], &ev) == 0 &&
                epoll_wait(ep, got, 4, 0) == 1 &&
                epoll_wait(ep, got, 4, 0) == 1,
            "level-triggered while bytes remain");
        check(read(local[0], buffer, 2) == 2 && epoll_wait(ep, got, 4, 0) == 0, "nothing after the read");
        ev.events = EPOLLOUT | EPOLLET;
        ev.data.u64 = 2;
        check(epoll_ctl(ep, EPOLL_CTL_ADD, local[1], &ev) == 0 &&
                epoll_wait(ep, got, 4, 0) == 1 &&
                got[0].data.u64 == 2 &&
                got[0].events == EPOLLOUT,
            "a write end is writable at its registration");
        check(epoll_ctl(ep, EPOLL_CTL_DEL, local[1], 0) == 0 &&
                epoll_ctl(ep, EPOLL_CTL_DEL, local[1], 0) == -1 &&
                errno == ENOENT,
            "the write end removed once");
        check(close(local[1]) == 0 &&
                epoll_wait(ep, got, 4, 1000) == 1 &&
                got[0].data.u64 == 1 &&
                (got[0].events & EPOLLHUP),
            "a closed writer is a hangup");
        file = open(CHILD, O_RDONLY);
        check(file >= 0 && epoll_ctl(ep, EPOLL_CTL_ADD, file, &ev) == -1 && errno == EPERM, "a file is EPERM");
        check(epoll_ctl(ep, EPOLL_CTL_ADD, ep, &ev) == -1 && errno == EINVAL, "the instance in itself is EINVAL");
        check(epoll_ctl(ep, EPOLL_CTL_ADD, 999, &ev) == -1 && errno == EBADF, "a closed descriptor is EBADF");
        check(close(file) == 0 && close(local[0]) == 0, "the local pipe closed");
        check(pipe(in) == 0 &&
                pipe(out) == 0 &&
                posix_spawn_file_actions_init(&actions) == 0 &&
                posix_spawn_file_actions_adddup2(&actions, in[0], 0) == 0 &&
                posix_spawn_file_actions_adddup2(&actions, out[1], 1) == 0 &&
                posix_spawn(&pid, CHILD, &actions, 0, copying, environ) == 0,
            "the copying child under epoll");
        posix_spawn_file_actions_destroy(&actions);
        check(close(in[0]) == 0 && close(out[1]) == 0, "the caller's far ends closed again");
        ev.events = EPOLLIN | EPOLLET;
        ev.data.u64 = 3;
        check(epoll_ctl(ep, EPOLL_CTL_ADD, out[0], &ev) == 0 && epoll_wait(ep, got, 4, 0) == 0,
            "the child's output registered");
        check(write(in[1], "through epoll\n", 14) == 14 &&
                epoll_wait(ep, got, 4, 5000) == 1 &&
                got[0].data.u64 == 3 &&
                (got[0].events & EPOLLIN) &&
                read(out[0], buffer, sizeof(buffer)) == 14 &&
                memcmp(buffer, "through epoll\n", 14) == 0,
            "the child's answer through an edge-triggered wait");
        check(close(in[1]) == 0 &&
                epoll_wait(ep, got, 4, 5000) == 1 &&
                got[0].data.u64 == 3 &&
                (got[0].events & EPOLLHUP),
            "the child's exit is a hangup");
        status = finish(pid);
        check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "the copying child under epoll ended");
        check(close(out[0]) == 0 && close(ep) == 0, "the instance closed");
    }
    check(pipe(out) == 0 &&
            posix_spawn_file_actions_init(&actions) == 0 &&
            posix_spawn_file_actions_adddup2(&actions, out[1], 1) == 0 &&
            posix_spawn_file_actions_adddup2(&actions, 1, 2) == 0 &&
            posix_spawn(&pid, CHILD, &actions, 0, streams, environ) == 0,
        "both outputs on one pipe");
    posix_spawn_file_actions_destroy(&actions);
    check(close(out[1]) == 0 &&
            read_all(out[0], buffer, sizeof(buffer)) == 47 &&
            memcmp(buffer, "[CHILD] standard output\n[CHILD] standard error\n", 47) == 0 &&
            close(out[0]) == 0,
        "the child's two outputs through one pipe");
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "the child with both outputs on a pipe");
    /* A transfer many times the pipe's buffer: a thread fills the child's input while the child copies it back. */
    pthread_t filler;
    check(pipe(in) == 0 &&
            pipe(out) == 0 &&
            posix_spawn_file_actions_init(&actions) == 0 &&
            posix_spawn_file_actions_adddup2(&actions, in[0], 0) == 0 &&
            posix_spawn_file_actions_adddup2(&actions, out[1], 1) == 0 &&
            posix_spawn(&pid, CHILD, &actions, 0, copying, environ) == 0 &&
            close(in[0]) == 0 &&
            close(out[1]) == 0 &&
            pthread_create(&filler, 0, fill, &in[1]) == 0,
        "a large transfer started");
    posix_spawn_file_actions_destroy(&actions);
    unsigned long total = 0;
    int pattern_held = 1;
    for (;;) {
        unsigned char block[4096];
        const ssize_t got = read(out[0], block, sizeof(block));
        check(got >= 0, "a read of the large transfer");
        if (got == 0) {
            break;
        }
        for (ssize_t i = 0; i < got; ++i) {
            pattern_held &= block[i] == (unsigned char)((total + (unsigned long)i) & 0xFF);
        }
        total += (unsigned long)got;
    }
    check(pthread_join(filler, 0) == 0 && total == TRANSFER && pattern_held && close(out[0]) == 0,
        "the large transfer through the child, byte for byte");
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "the child that copied the large transfer");
    /* The child writes nothing and ends: poll reports the far end's close as a hang-up. */
    char *quiet[] = {"child", "sleep", "50", "0", 0};
    check(pipe(out) == 0 &&
            posix_spawn_file_actions_init(&actions) == 0 &&
            posix_spawn_file_actions_adddup2(&actions, out[1], 1) == 0 &&
            posix_spawn(&pid, CHILD, &actions, 0, quiet, environ) == 0 &&
            close(out[1]) == 0,
        "a quiet child with a pipe");
    posix_spawn_file_actions_destroy(&actions);
    struct pollfd polled = {out[0], POLLIN, 0};
    check(poll(&polled, 1, 5000) == 1 && polled.revents == POLLHUP && close(out[0]) == 0,
        "poll sees the far end's close");
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "the quiet child");
    /* A file as a stream stays refused with the pipes there. */
    check(posix_spawn_file_actions_init(&actions) == 0 &&
            posix_spawn_file_actions_adddup2(&actions, dir, 0) == 0 &&
            posix_spawn(&pid, CHILD, &actions, 0, streams, environ) == ENOSYS,
        "a directory as the input refused");
    posix_spawn_file_actions_destroy(&actions);

    /* SIGCHLD (N4.2): a handler runs once when a child ends, before or after the wait reaps it. */
    struct sigaction on_child;
    memset(&on_child, 0, sizeof(on_child));
    on_child.sa_handler = count_sigchld;
    check(sigaction(SIGCHLD, &on_child, 0) == 0, "a SIGCHLD handler");
    pid = start(exit_zero, environ);
    for (int i = 0; i < 500 && !sigchld_count; ++i) {
        const struct timespec tick = {0, 10000000L};
        nanosleep(&tick, 0);
    }
    check(sigchld_count == 1, "SIGCHLD at the child's end");
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0 && sigchld_count == 1, "SIGCHLD once per child");
    on_child.sa_handler = SIG_DFL;
    check(sigaction(SIGCHLD, &on_child, 0) == 0, "the default SIGCHLD action again");

    /* waitid (N4.2): a report without reaping, the reap after it, WNOHANG, and the waits that are not there. */
    char *exit_seven[] = {"child", "exit", "7", 0};
    siginfo_t info;
    pid = start(exit_seven, environ);
    memset(&info, 0xFF, sizeof(info));
    check(waitid(P_ALL, 0, &info, WEXITED | WNOWAIT) == 0 &&
            info.si_pid == pid &&
            info.si_signo == SIGCHLD &&
            info.si_code == CLD_EXITED &&
            info.si_status == 7,
        "waitid with WNOWAIT reports the child without reaping it");
    memset(&info, 0, sizeof(info));
    check(waitid(P_PID, pid, &info, WEXITED | WNOHANG) == 0 && info.si_pid == pid && info.si_code == CLD_EXITED,
        "waitid reaps the child");
    check(waitid(P_ALL, 0, &info, WEXITED | WNOHANG) == -1 && errno == ECHILD, "waitid without a child");
    pid = start(sleeping, environ);
    memset(&info, 0xFF, sizeof(info));
    check(waitid(P_ALL, 0, &info, WEXITED | WNOHANG) == 0 && info.si_pid == 0 && info.si_signo == 0,
        "waitid with WNOHANG while the child runs");
    check(waitid(P_ALL, 0, &info, WSTOPPED) == -1 && errno == EINVAL, "a wait for a stop alone is refused");
    check(waitid(P_PGID, 1, &info, WEXITED | WNOHANG) == -1 && errno == ECHILD, "no process group has a child");
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 9, "the slept child after waitid");

    /* kill (N4.2): 0 finds a child, SIGKILL ends it as a death by that signal, and any other signal is refused. */
    char *long_sleep[] = {"child", "sleep", "10000", "0", 0};
    pid = start(long_sleep, environ);
    check(kill(pid, 0) == 0, "kill with 0 finds the child");
    check(kill(pid, SIGTERM) == -1 && errno == EPERM, "a signal to a child is refused");
    check(kill(999, 0) == -1 && errno == ESRCH, "no such child");
    check(kill(pid, SIGKILL) == 0, "SIGKILL");
    status = finish(pid);
    check(WIFSIGNALED(status) && WTERMSIG(status) == SIGKILL, "the killed child");
    check(kill(pid, 0) == -1 && errno == ESRCH, "the reaped child is gone");

    /* The system layer's figures (R2.1). */
    struct sysinfo memory;
    check(sysinfo(&memory) == 0 &&
            memory.mem_unit == 1 &&
            memory.totalram > 0 &&
            memory.freeram > 0 &&
            memory.freeram <= memory.totalram &&
            memory.totalswap == 0,
        "sysinfo");
    check(sysconf(_SC_PHYS_PAGES) == (long)(memory.totalram / (unsigned long)sysconf(_SC_PAGESIZE)),
        "sysconf(_SC_PHYS_PAGES)");
    struct rlimit limit;
    check(getrlimit(RLIMIT_AS, &limit) == 0 && limit.rlim_cur == limit.rlim_max && limit.rlim_cur >= (1UL << 30),
        "getrlimit(RLIMIT_AS)");
    check(getrlimit(RLIMIT_NOFILE, &limit) == 0 && limit.rlim_cur == 1024 && limit.rlim_max == 1024,
        "getrlimit(RLIMIT_NOFILE): the library's descriptor table");
    check(getrlimit(RLIMIT_CORE, &limit) == -1 && errno == ENOSYS, "a limit the system layer does not report");
    cpu_set_t processors;
    check(sched_getaffinity(0, sizeof(processors), &processors) == 0 &&
            CPU_ISSET(0, &processors) &&
            sysconf(_SC_NPROCESSORS_ONLN) == CPU_COUNT(&processors),
        "sched_getaffinity");
    /* The mask the process has is accepted, a mask of no processor threads run on is not (R3.2). */
    cpu_set_t none;
    CPU_ZERO(&none);
    CPU_SET(CPU_SETSIZE - 1, &none);
    check(sched_setaffinity(0, sizeof(processors), &processors) == 0 &&
            sched_setaffinity(0, sizeof(none), &none) == -1 &&
            errno == EINVAL,
        "sched_setaffinity");
    /* The kernel honours a thread's affinity (P1.4): pinned to the first processor, the thread reads that mask back,
     * then every processor again. */
    if (CPU_COUNT(&processors) > 1) {
        cpu_set_t first, read_back;
        CPU_ZERO(&first);
        CPU_SET(0, &first);
        check(sched_setaffinity(0, sizeof(first), &first) == 0 &&
                sched_getaffinity(0, sizeof(read_back), &read_back) == 0 &&
                CPU_COUNT(&read_back) == 1 &&
                CPU_ISSET(0, &read_back) &&
                sched_setaffinity(0, sizeof(processors), &processors) == 0 &&
                sched_getaffinity(0, sizeof(read_back), &read_back) == 0 &&
                CPU_EQUAL(&read_back, &processors),
            "sched_setaffinity pins a thread and sets it free");
        /* getcpu names the processor a pinned thread runs on, each in turn (P2). */
        int pinned = 1;
        for (int processor = 0; processor < CPU_COUNT(&processors); ++processor) {
            cpu_set_t one;
            CPU_ZERO(&one);
            CPU_SET(processor, &one);
            pinned = pinned && sched_setaffinity(0, sizeof(one), &one) == 0 && sched_getcpu() == processor;
        }
        check(pinned && sched_setaffinity(0, sizeof(processors), &processors) == 0,
            "sched_getcpu follows a pinned thread");
    }
    const int running_on = sched_getcpu();
    check(running_on >= 0 && CPU_ISSET(running_on, &processors), "sched_getcpu: a processor of the mask");
    /* One priority for every thread, which Linux reports of a normal thread (R7.1): SCHED_OTHER at 0. */
    struct sched_param priority = {.sched_priority = 1};
    int policy = -1;
    check(pthread_getschedparam(pthread_self(), &policy, &priority) == 0 &&
            policy == SCHED_OTHER &&
            priority.sched_priority == 0 &&
            sched_get_priority_min(SCHED_OTHER) == 0 &&
            sched_get_priority_max(SCHED_OTHER) == 0 &&
            sched_get_priority_max(SCHED_FIFO) == -1 &&
            errno == EINVAL,
        "a thread's policy and priority");
    check(pthread_setschedparam(pthread_self(), SCHED_OTHER, &priority) == 0 &&
            pthread_setschedparam(pthread_self(), SCHED_FIFO, &priority) == ENOSYS,
        "setting the normal policy changes nothing; another is refused");
    pthread_attr_t attributes;
    void *stack = 0;
    size_t stack_size = 0;
    check(pthread_getattr_np(pthread_self(), &attributes) == 0 &&
            pthread_attr_getstack(&attributes, &stack, &stack_size) == 0 &&
            stack_size >= 2 * 1024 * 1024 && /* the process manager's 2 MiB of adjacent objects (R7.2b) */
            (char *)&attributes > (char *)stack &&
            (char *)&attributes < (char *)stack + stack_size,
        "the main thread's stack");
    char name[16];
    check(pthread_setname_np(pthread_self(), "init-main") == 0 &&
            pthread_getname_np(pthread_self(), name, sizeof(name)) == 0 &&
            strcmp(name, "init-main") == 0,
        "a thread's name");
    static const int constant = 42;
    const uintptr_t page = (uintptr_t)sysconf(_SC_PAGESIZE);
    void *readonly = (void *)((uintptr_t)&constant & ~(page - 1));
    check(mprotect(readonly, page, PROT_READ | PROT_WRITE) == 0 &&
            mprotect(readonly, page, PROT_READ) == 0 &&
            *(const volatile int *)&constant == 42,
        "a read-only page of the program made writable");

    /* More processes at once than the twelve slots before P1.5: twenty sleeping children beside the root task and
     * init, every one still running once the last started, then each reaped with its code. */
    enum {
        CROWD = 20
    };

    pid_t crowd[CROWD];
    char *napping[] = {"child", "sleep", "3000", "5", 0};
    int crowd_started = 0, crowd_running = 0, crowd_reaped = 0, crowd_error = 0, crowd_odd = -1, crowd_odd_status = 0;
    for (int i = 0; i < CROWD; ++i) {
        const int error = posix_spawn(&crowd[crowd_started], CHILD, 0, 0, napping, environ);
        crowd_started += error == 0;
        crowd_error = error ? error : crowd_error;
    }
    for (int i = 0; i < crowd_started; ++i) {
        int crowd_status = 0;
        crowd_running += waitpid(crowd[i], &crowd_status, WNOHANG) == 0;
    }
    for (int i = 0; i < crowd_started; ++i) {
        int crowd_status = 0;
        const int reaped = waitpid(crowd[i], &crowd_status, 0) == crowd[i] &&
            WIFEXITED(crowd_status) &&
            WEXITSTATUS(crowd_status) == 5;
        crowd_reaped += reaped;
        if (!reaped && crowd_odd < 0) {
            crowd_odd = i;
            crowd_odd_status = crowd_status;
        }
    }
    if (crowd_started != CROWD || crowd_running != CROWD || crowd_reaped != CROWD) {
        printf(
            "[INIT] the crowd: %d started (last error %d), %d running at once, %d reaped; child %d ended with status "
            "0x%x\n",
            crowd_started, crowd_error, crowd_running, crowd_reaped, crowd_odd, crowd_odd_status);
    }
    check(crowd_started == CROWD && crowd_running == CROWD && crowd_reaped == CROWD,
        "twenty children at once, more than twelve processes");

    printf("[INIT] process manager on " ISA_NAME ": %d checks passed\n", checks);
    return 0;
}
