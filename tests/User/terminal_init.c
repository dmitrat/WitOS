#define _GNU_SOURCE
#include <errno.h>
#include <poll.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>

/* /bin/init of the terminal scenario (plan step N4.3b): the first process the root task's process manager starts, with
 * the terminal service's endpoint in its start message, so that its standard streams are the terminal. Every line it
 * prints goes to the terminal, whose transcript the runner keeps; the runner types what the lines ask for. It checks
 * isatty of the three streams, the size, the attributes, a line read in canonical mode and printed back, a key read in
 * raw mode, a read ended by SIGINT from Ctrl-C through a handler, the end of the file from Ctrl-D, and poll's readiness
 * before and after a line is typed. The last line names the ISA and the checks; a failed check exits with 1, which the
 * root task reports as a failed boot. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#else
#define ISA_NAME "aarch64"
#endif

static int checks;
static volatile sig_atomic_t interrupts;

static void check(int condition, const char *what)
{
    ++checks;
    if (!condition) {
        printf("[TERMINAL-INIT] FAILED: %s\n", what);
        fflush(stdout);
        exit(1);
    }
}

static void on_interrupt(int sig)
{
    (void)sig;
    ++interrupts;
}

int main(void)
{
    char line[256];
    printf("[TERMINAL-INIT] started on " ISA_NAME "\n");

    /* The streams are a terminal with a size. */
    check(isatty(0) && isatty(1) && isatty(2), "isatty of the standard streams");
    struct winsize size;
    check(ioctl(1, TIOCGWINSZ, &size) == 0 && size.ws_col == 80 && size.ws_row == 24, "TIOCGWINSZ");
    struct termios attributes;
    check(tcgetattr(0, &attributes) == 0 &&
            (attributes.c_lflag & ICANON) &&
            (attributes.c_lflag & ECHO) &&
            (attributes.c_lflag & ISIG) &&
            (attributes.c_iflag & ICRNL) &&
            attributes.c_cc[VINTR] == 3 &&
            attributes.c_cc[VMIN] == 1,
        "tcgetattr: canonical with echo and signals");

    /* A line in canonical mode, printed back. */
    printf("[TERMINAL-INIT] type a line\n");
    check(fgets(line, sizeof(line), stdin) != 0, "fgets of a line");
    line[strcspn(line, "\r\n")] = 0;
    check(strcmp(line, "hello from init") == 0, "the line typed");
    printf("[TERMINAL-INIT] echo: %s\n", line);

    /* One key in raw mode: no canonical mode, no echo, one byte at a time, as .NET reads a key. */
    struct termios raw = attributes;
    raw.c_lflag &= ~(ICANON | ECHO | IEXTEN);
    raw.c_iflag &= ~(IXON | IXOFF | ICRNL | INLCR | IGNCR);
    raw.c_cc[VMIN] = 1;
    raw.c_cc[VTIME] = 0;
    check(tcsetattr(0, TCSANOW, &raw) == 0, "tcsetattr to raw");
    struct termios got;
    check(tcgetattr(0, &got) == 0 && !(got.c_lflag & ICANON) && !(got.c_lflag & ECHO),
        "tcgetattr reads the raw attributes back");
    printf("[TERMINAL-INIT] press a key\n");
    char key = 0;
    check(read(0, &key, 1) == 1 && key == 'k', "one key in raw mode");
    check(tcsetattr(0, TCSAFLUSH, &attributes) == 0, "tcsetattr back to canonical");

    /* Ctrl-C with ISIG: SIGINT through the handler, and the waiting read ends with EINTR. */
    struct sigaction action;
    memset(&action, 0, sizeof(action));
    action.sa_handler = on_interrupt;
    check(sigaction(SIGINT, &action, 0) == 0, "a SIGINT handler");
    printf("[TERMINAL-INIT] press Ctrl-C\n");
    const long interrupted = read(0, line, sizeof(line));
    const int saved = errno;
    for (int i = 0; i < 500 && !interrupts; ++i) {
        const struct timespec tick = {0, 10000000L};
        nanosleep(&tick, 0);
    }
    check(interrupted == -1 && saved == EINTR, "the read ended with EINTR");
    check(interrupts == 1, "SIGINT ran the handler once");

    /* Ctrl-D on an empty line: the end of the file. */
    printf("[TERMINAL-INIT] press Ctrl-D\n");
    check(read(0, line, sizeof(line)) == 0, "the end of the file");

    /* poll: nothing before, a line after. */
    struct pollfd input = {0, POLLIN, 0};
    check(poll(&input, 1, 0) == 0, "poll finds no input before a line");
    printf("[TERMINAL-INIT] type x\n");
    check(poll(&input, 1, 10000) == 1 && (input.revents & POLLIN), "poll finds the line typed");
    check(read(0, line, sizeof(line)) == 2 && line[0] == 'x' && line[1] == '\n', "the line poll found");
    struct pollfd output = {1, POLLOUT, 0};
    check(poll(&output, 1, 0) == 1 && (output.revents & POLLOUT), "an output is always writable");

    /* The standard error is the terminal too. */
    check(write(2, "[TERMINAL-INIT] standard error\n", 31) == 31, "a write to the standard error");

    printf("[TERMINAL-INIT] terminal on " ISA_NAME ": %d checks passed\n", checks);
    return 0;
}
