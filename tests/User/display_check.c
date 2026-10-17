#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>

/* The display scenario's check of the terminal on the screen (plan steps C2.2 and C2.3): the first command of
 * tests/User/apps_init.c's /etc/apps in a boot whose terminal has the display as its main console and the virtio console
 * the runner reads as its mirror, before PowerShell runs there. Every line it prints is drawn on the screen and reaches
 * the runner's transcript; the runner types what the lines ask for on the keyboard through QMP. It checks that the
 * streams are a terminal of the display's grid, reads a line typed on the keyboard and prints it back, and reports its
 * checks. A failed check exits with 1, which apps_init counts as a failed command. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#elif defined(__aarch64__)
#define ISA_NAME "aarch64"
#else
#define ISA_NAME "riscv64"
#endif

#define COLUMNS 100 /* 800 pixels of 8 */
#define ROWS 37 /* 600 pixels of 16 */

static int checks;

static void check(int condition, const char *what)
{
    ++checks;
    if (!condition) {
        printf("[DISPLAY-CHECK] FAILED: %s\n", what);
        fflush(stdout);
        exit(1);
    }
}

int main(void)
{
    char line[256];
    printf("[DISPLAY-CHECK] started on " ISA_NAME "\n");

    /* The streams are the terminal, sized by the display. */
    check(isatty(0) && isatty(1) && isatty(2), "isatty of the standard streams");
    struct winsize size;
    check(ioctl(1, TIOCGWINSZ, &size) == 0 && size.ws_col == COLUMNS && size.ws_row == ROWS,
        "TIOCGWINSZ is the display's grid");

    /* A line typed on the keyboard, in canonical mode, printed back. */
    printf("[DISPLAY-CHECK] type a line\n");
    check(fgets(line, sizeof(line), stdin) != 0, "fgets of a line");
    line[strcspn(line, "\r\n")] = 0;
    check(strcmp(line, "screen") == 0, "the line typed on the keyboard");
    printf("[DISPLAY-CHECK] echo: %s\n", line);

    printf("[DISPLAY-CHECK] display on " ISA_NAME ": %d checks passed\n", checks);
    fflush(stdout);
    return 0;
}
