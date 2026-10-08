/* The startup of a program another process started (plan step S5.2): a static position-independent executable that
 * its creator mapped at a base of its choosing (witos/start.h). musl's dlstart.c, unchanged and included as musl's
 * own crt/rcrt1.c includes it, enters at _start with the stack the creator built and applies the program's relative
 * relocations; its second stage hands the start message to the library (start.c) before musl's __libc_start_main, as
 * crt1 sets up the root task. Built into rcrt1.o with musl's options for its startup files (-DCRT -fPIC), outside
 * libc.a; the root task keeps crt1.o. */
#define START "_start"
#define _dlstart_c _start_c
#include "dlstart.c"

int main();
weak void _init();
weak void _fini();
int __libc_start_main(int (*)(), int, char **, void (*)(), void (*)(), void (*)());
void __wit_start_program(const unsigned long *stack);

hidden void __dls2(unsigned char *base, size_t *sp)
{
    (void)base;
    __wit_start_program((const unsigned long *)sp);
    __libc_start_main(main, *sp, (void *)(sp + 1), _init, _fini, 0);
}
