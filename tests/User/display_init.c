#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>

/* The display scenario's /bin/init (plan step C2.2): the first process the root task's process manager starts, with
 * the terminal service's endpoint in its start message — the terminal whose main console is the display and whose mirror
 * is the virtio console the runner reads. Every line it prints is drawn on the screen and reaches the runner's
 * transcript; the runner types what the lines ask for on the keyboard through QMP. It checks that the streams are a
 * terminal of the display's grid, reads a line typed on the keyboard and prints it back, reports its checks, and then
 * waits for Enter: the runner takes the screendump of this screen first, and the host renders the frame it expects from
 * the transcript. A failed check exits with 1, which the root task reports as a failed boot. */

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
        printf("[DISPLAY-INIT] FAILED: %s\n", what);
        fflush(stdout);
        exit(1);
    }
}

int main(void)
{
    char line[256];
    printf("[DISPLAY-INIT] started on " ISA_NAME "\n");

    /* The streams are the terminal, sized by the display. */
    check(isatty(0) && isatty(1) && isatty(2), "isatty of the standard streams");
    struct winsize size;
    check(ioctl(1, TIOCGWINSZ, &size) == 0 && size.ws_col == COLUMNS && size.ws_row == ROWS,
        "TIOCGWINSZ is the display's grid");

    /* A line typed on the keyboard, in canonical mode, printed back. */
    printf("[DISPLAY-INIT] type a line\n");
    check(fgets(line, sizeof(line), stdin) != 0, "fgets of a line");
    line[strcspn(line, "\r\n")] = 0;
    check(strcmp(line, "screen") == 0, "the line typed on the keyboard");
    printf("[DISPLAY-INIT] echo: %s\n", line);

    printf("[DISPLAY-INIT] display on " ISA_NAME ": %d checks passed\n", checks);
    fflush(stdout);
    /* Nothing more is drawn until the runner, with its screendump taken, presses Enter. */
    (void)fgets(line, sizeof(line), stdin);
    return 0;
}
