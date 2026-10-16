#include <stdio.h>

/* The display scenario's /bin/init (plan step C2.1): the device manager drew the test pattern and the keyboard driver
 * serves its device before this program starts; it has nothing to do with either yet (the display service and the
 * terminal on the screen are C2.2) and reports that it ran, on the serial log, since the scenario has no terminal. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#elif defined(__aarch64__)
#define ISA_NAME "aarch64"
#else
#define ISA_NAME "riscv64"
#endif

int main(void)
{
    printf(
        "[DISPLAY-INIT] display scenario on " ISA_NAME ": init ran beside the framebuffer and the keyboard driver\n");
    fflush(stdout);
    return 0;
}
