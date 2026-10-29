#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <termios.h>
#include <unistd.h>

/* The libc's pseudo-terminals (plan step E4.1): /bin/pty-check, which the runtime-terminal scenario's /bin/init starts,
 * a process whose start message carried the terminal service's endpoint. It does what a terminal emulator does — as
 * Arlecchino.Commander's POSIX terminal does it: posix_openpt, grantpt, unlockpt, ptsname, a size, and posix_spawn of a
 * child with the slave at its standard descriptors (addopen and adddup2), trying POSIX_SPAWN_SETSID first — and talks
 * to the child through the master: the child sees a terminal of the master's size, its output arrives through ONLCR, a
 * line typed at the master reaches its canonical read with an echo and without one, a new size reaches it as SIGWINCH,
 * a grandchild it starts inherits the pseudo-terminal, Ctrl-C reaches it as SIGINT, and once it exits the master reads
 * EIO and poll says POLLHUP. Then openpty's way within one process: the slave opened by its name, bytes both ways, and
 * the master's EIO once the slave closed; and /dev/pts of no pseudo-terminal is ENOENT. */

#define WAIT_MILLISECONDS 60000

static int passed, failed;
static volatile sig_atomic_t resized, interrupted;

static void check(int condition, const char *what)
{
    if (condition) {
        ++passed;
    } else {
        ++failed;
        printf("[PTY] FAILED: %s (errno %d: %s)\n", what, errno, strerror(errno));
    }
}

/* The master's bytes until text appeared in them, or the wait ended: the bytes gathered so far in seen. */
static int expect(int master, char *seen, size_t room, size_t *length, const char *text)
{
    for (int waited = 0; waited < WAIT_MILLISECONDS; waited += 100) {
        seen[*length] = 0;
        if (strstr(seen, text)) {
            return 1;
        }
        struct pollfd p = {master, POLLIN, 0};
        if (poll(&p, 1, 100) > 0 && (p.revents & POLLIN)) {
            const ssize_t got = read(master, seen + *length, room - 1 - *length);
            if (got > 0) {
                *length += (size_t)got;
            } else if (got < 0 && errno != EAGAIN && errno != EINTR) {
                seen[*length] = 0;
                return strstr(seen, text) != 0;
            }
        }
    }
    seen[*length] = 0;
    return strstr(seen, text) != 0;
}

/* The grandchild: its standard streams are the pseudo-terminal its parent's are. */
static int grandchild(void)
{
    struct winsize size;
    const int sized = ioctl(1, TIOCGWINSZ, &size) == 0;
    printf("grandchild tty=%d%d%d size=%dx%d\n", isatty(0), isatty(1), isatty(2), sized ? size.ws_col : 0,
        sized ? size.ws_row : 0);
    fflush(stdout);
    return 0;
}

static void on_winch(int signal)
{
    (void)signal;
    resized = 1;
}

static void on_interrupt(int signal)
{
    (void)signal;
    interrupted = 1;
}

/* The child on the slave side: what a program under a terminal emulator sees. */
static int child(void)
{
    char line[256];
    struct winsize size;
    signal(SIGWINCH, on_winch);
    signal(SIGINT, on_interrupt);
    setvbuf(stdout, 0, _IOLBF, 0);
    const int sized = ioctl(0, TIOCGWINSZ, &size) == 0;
    printf("ready tty=%d%d%d size=%dx%d\n", isatty(0), isatty(1), isatty(2), sized ? size.ws_col : 0,
        sized ? size.ws_row : 0);
    for (int round = 0; round < 2; ++round) {
        if (!fgets(line, sizeof(line), stdin)) {
            printf("no line\n");
            return 3;
        }
        line[strcspn(line, "\n")] = 0;
        printf("got %s\n", line);
    }
    printf("waiting for size\n");
    for (int waited = 0; !resized && waited < WAIT_MILLISECONDS; waited += 10) {
        usleep(10000);
    }
    ioctl(0, TIOCGWINSZ, &size);
    printf("winch=%d size=%dx%d\n", (int)resized, size.ws_col, size.ws_row);
    char *const arguments[] = {"/bin/pty-check", "grandchild", 0};
    pid_t pid = 0;
    int status = -1;
    if (posix_spawn(&pid, "/bin/pty-check", 0, 0, arguments, environ) == 0) {
        waitpid(pid, &status, 0);
    }
    printf("grandchild exited %d\n", WIFEXITED(status) ? WEXITSTATUS(status) : -1);
    printf("waiting for interrupt\n");
    for (int waited = 0; !interrupted && waited < WAIT_MILLISECONDS; waited += 10) {
        usleep(10000);
    }
    printf("interrupted=%d\n", (int)interrupted);
    return interrupted ? 42 : 4;
}

static void emulator(void)
{
    char seen[4096];
    size_t length = 0;
    struct stat st;
    struct winsize size = {30, 100, 0, 0};
    const int master = posix_openpt(O_RDWR | O_NOCTTY);
    check(master >= 0, "posix_openpt");
    if (master < 0) {
        return;
    }
    check(grantpt(master) == 0 && unlockpt(master) == 0, "grantpt and unlockpt");
    const char *name = ptsname(master);
    check(name && strncmp(name, "/dev/pts/", 9) == 0, "ptsname names /dev/pts/<number>");
    printf("[PTY] master %d, slave %s\n", master, name ? name : "(none)");
    check(fstat(master, &st) == 0 && S_ISCHR(st.st_mode), "the master is a character device");
    check(isatty(master), "isatty of the master");
    check(ioctl(master, TIOCSWINSZ, &size) == 0, "TIOCSWINSZ on the master");

    posix_spawn_file_actions_t actions;
    posix_spawnattr_t attributes;
    posix_spawn_file_actions_init(&actions);
    posix_spawnattr_init(&attributes);
    posix_spawn_file_actions_addopen(&actions, 0, name ? name : "/dev/pts/0", O_RDWR, 0);
    posix_spawn_file_actions_adddup2(&actions, 0, 1);
    posix_spawn_file_actions_adddup2(&actions, 0, 2);
    char *const arguments[] = {"/bin/pty-check", "child", 0};
    pid_t pid = 0;
    posix_spawnattr_setflags(&attributes, POSIX_SPAWN_SETSID);
    int spawned = posix_spawn(&pid, "/bin/pty-check", &actions, &attributes, arguments, environ);
    if (spawned != 0) {
        posix_spawnattr_setflags(&attributes, 0); /* no sessions: as Commander, start it without one */
        spawned = posix_spawn(&pid, "/bin/pty-check", &actions, &attributes, arguments, environ);
    }
    posix_spawn_file_actions_destroy(&actions);
    posix_spawnattr_destroy(&attributes);
    check(spawned == 0 && pid > 0, "posix_spawn with the slave at the standard descriptors");
    if (spawned != 0) {
        close(master);
        return;
    }

    check(expect(master, seen, sizeof(seen), &length, "ready tty=111 size=100x30\r\n"),
        "the child sees a terminal of the master's size, through ONLCR");
    check(write(master, "hello\r", 6) == 6, "a line typed at the master");
    check(expect(master, seen, sizeof(seen), &length, "hello\r\ngot hello\r\n"),
        "the echo, then the child's canonical read");

    struct termios modes;
    check(tcgetattr(master, &modes) == 0 && (modes.c_lflag & ECHO), "tcgetattr through the master");
    modes.c_lflag &= ~(tcflag_t)ECHO;
    check(tcsetattr(master, TCSANOW, &modes) == 0, "tcsetattr through the master: no echo");
    length = 0;
    check(write(master, "quiet\r", 6) == 6, "a line typed without echo");
    check(expect(master, seen, sizeof(seen), &length, "got quiet\r\n") && !strstr(seen, "quiet\r\ngot"),
        "the line arrives without its echo");

    check(expect(master, seen, sizeof(seen), &length, "waiting for size\r\n"), "the child waits for a size");
    size.ws_col = 120;
    size.ws_row = 40;
    check(ioctl(master, TIOCSWINSZ, &size) == 0, "a new size on the master");
    check(expect(master, seen, sizeof(seen), &length, "winch=1 size=120x40\r\n"),
        "the new size reaches the child as SIGWINCH");
    check(expect(master, seen, sizeof(seen), &length, "grandchild tty=111 size=120x40\r\n"),
        "a grandchild inherits the pseudo-terminal");
    check(expect(master, seen, sizeof(seen), &length, "grandchild exited 0\r\n"), "the grandchild exits");

    check(expect(master, seen, sizeof(seen), &length, "waiting for interrupt\r\n"), "the child waits for Ctrl-C");
    check(write(master, "\x03", 1) == 1, "Ctrl-C typed at the master");
    check(expect(master, seen, sizeof(seen), &length, "interrupted=1\r\n"), "Ctrl-C reaches the child as SIGINT");

    int status = -1;
    check(waitpid(pid, &status, 0) == pid && WIFEXITED(status) && WEXITSTATUS(status) == 42, "the child exits with 42");
    char rest[256];
    ssize_t got = 0;
    for (int waited = 0; waited < WAIT_MILLISECONDS; waited += 100) {
        struct pollfd p = {master, POLLIN, 0};
        poll(&p, 1, 100);
        got = read(master, rest, sizeof(rest));
        if (got < 0 && errno == EIO) {
            break;
        }
    }
    check(got < 0 && errno == EIO, "the master reads EIO once the slave side went");
    struct pollfd hung = {master, POLLIN, 0};
    check(poll(&hung, 1, 0) == 1 && (hung.revents & POLLHUP), "poll of the master says POLLHUP");
    check(close(master) == 0, "the master closes");
    printf("[PTY] emulator: the child ran on %s\n", name ? name : "(none)");
}

/* openpty's way within one process: the slave opened by its name. */
static void in_process(void)
{
    char buffer[64];
    const int master = posix_openpt(O_RDWR | O_NOCTTY);
    check(master >= 0 && unlockpt(master) == 0, "a second master");
    if (master < 0) {
        return;
    }
    char name[32];
    check(ptsname_r(master, name, sizeof(name)) == 0, "ptsname_r");
    const int slave = open(name, O_RDWR | O_NOCTTY);
    check(slave >= 0 && isatty(slave), "the slave opened by its name is a terminal");
    if (slave >= 0) {
        struct winsize size = {24, 80, 0, 0};
        check(ioctl(master, TIOCSWINSZ, &size) == 0 && ioctl(slave, TIOCGWINSZ, &size) == 0 && size.ws_col == 80,
            "the size through the slave");
        check(write(slave, "abc\n", 4) == 4, "a write to the slave");
        size_t length = 0;
        char seen[128];
        check(expect(master, seen, sizeof(seen), &length, "abc\r\n"), "the slave's write at the master");
        struct termios modes;
        tcgetattr(slave, &modes);
        modes.c_lflag &= ~(tcflag_t)ECHO;
        tcsetattr(slave, TCSANOW, &modes);
        check(write(master, "xyz\n", 4) == 4, "a write to the master");
        check(
            read(slave, buffer, sizeof(buffer)) == 4 && !memcmp(buffer, "xyz\n", 4), "the master's line at the slave");
        check(close(slave) == 0, "the slave closes");
        ssize_t got = 0;
        for (int waited = 0; waited < WAIT_MILLISECONDS; waited += 100) {
            fcntl(master, F_SETFL, O_NONBLOCK);
            got = read(master, buffer, sizeof(buffer));
            if (got < 0 && errno == EIO) {
                break;
            }
            usleep(100000);
        }
        check(got < 0 && errno == EIO, "the master's EIO once its slave closed");
    }
    check(close(master) == 0, "the second master closes");
    check(open("/dev/pts/15", O_RDWR | O_NOCTTY) < 0 && errno == ENOENT, "a slave of no pseudo-terminal is ENOENT");
}

int main(int argc, char **argv)
{
    if (argc > 1 && strcmp(argv[1], "child") == 0) {
        return child();
    }
    if (argc > 1 && strcmp(argv[1], "grandchild") == 0) {
        return grandchild();
    }
    setvbuf(stdout, 0, _IOLBF, 0);
    emulator();
    in_process();
#if defined(__x86_64__)
    const char *isa = "x86_64";
#elif defined(__aarch64__)
    const char *isa = "aarch64";
#else
    const char *isa = "riscv64";
#endif
    printf("[PTY] pseudo-terminals on %s: %d checks passed, %d failed\n", isa, passed, failed);
    return failed ? 1 : 0;
}
