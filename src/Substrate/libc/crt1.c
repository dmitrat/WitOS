#include "witos_libc.h"
#include <elf.h>

/* The startup of a static program on WitOS (plan step S1.1): the kernel enters the first thread with the startup
 * descriptor in the argument register and a stack of its own, not with a Linux process stack. _start saves the
 * descriptor, then the C part hands musl's __libc_start_main a program stack image of the shape it reads: argc,
 * argv, a null, envp (empty), a null and the auxiliary vector, whose AT_PHDR points at the program headers that the
 * linker script maps with the first segment (static TLS needs PT_TLS) and whose AT_RANDOM carries the kernel's
 * entropy for the stack protector. The vector says the process is not set-uid, so musl does not poll the standard
 * descriptors; page size and the ELF header come from the image itself. */

const WitRootStartup *__wit_startup;

extern const Elf64_Ehdr __ehdr_start; /* lld's symbol for the ELF header at the image base */
int main();
__attribute__((__weak__)) void _init(void);
__attribute__((__weak__)) void _fini(void);
int __libc_start_main(int (*)(), int, char **, void (*)(), void (*)(), void (*)());

static long stack_image[32]; /* argc, argv[0], 0, 0, then the auxiliary vector */
static unsigned char random_bytes[16];
static char program_name[] = "root";

__attribute__((__noreturn__, __used__)) void _start_c(const WitRootStartup *startup)
{
    WitU64 result;
    __wit_startup = startup;
    __wit_thread_init(); /* the main thread's record and handle (S2, S3) */
    __wit_signal_init(); /* the fault callback every signal arrives through (S3) */
    wit_syscall(WIT_CALL_RANDOM, (WitU64)random_bytes, sizeof(random_bytes), 0, &result);
    long *p = stack_image;
    *p++ = 1; /* argc */
    *p++ = (long)program_name; /* argv[0] */
    *p++ = 0; /* argv end */
    *p++ = 0; /* envp end */
    *p++ = AT_PAGESZ;
    *p++ = 4096;
    *p++ = AT_PHDR;
    *p++ = (long)((const char *)&__ehdr_start + __ehdr_start.e_phoff);
    *p++ = AT_PHENT;
    *p++ = __ehdr_start.e_phentsize;
    *p++ = AT_PHNUM;
    *p++ = __ehdr_start.e_phnum;
    *p++ = AT_RANDOM;
    *p++ = (long)random_bytes;
    *p++ = AT_UID;
    *p++ = 0;
    *p++ = AT_EUID;
    *p++ = 0;
    *p++ = AT_GID;
    *p++ = 0;
    *p++ = AT_EGID;
    *p++ = 0;
    *p++ = AT_SECURE;
    *p++ = 0;
    *p++ = AT_HWCAP;
    *p++ = 0;
    *p++ = AT_NULL;
    *p = 0;
    __libc_start_main(main, 1, (char **)&stack_image[1], _init, _fini, 0);
    for (;;) {
        wit_syscall(WIT_CALL_PROCESS_EXIT, 127, 0, 0, &result);
    }
}

#if defined(__x86_64__)
/* The kernel enters with the descriptor in RDI and a 16-byte aligned stack pointer, no return address pushed. */
__asm__(".text\n"
        ".global _start\n"
        ".type _start,@function\n"
        "_start:\n"
        "    xor %ebp, %ebp\n"
        "    and $-16, %rsp\n"
        "    call _start_c\n"
        "    ud2\n");
#elif defined(__aarch64__)
__asm__(".text\n"
        ".global _start\n"
        ".type _start,%function\n"
        "_start:\n"
        "    mov x29, #0\n"
        "    mov x30, #0\n"
        "    bl _start_c\n"
        "    brk #0\n");
#else
#error "WitOS crt1: unsupported architecture"
#endif
