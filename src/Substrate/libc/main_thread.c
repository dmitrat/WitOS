/* musl compiles its own sources with -w; WitOS's part takes -Wall -Wextra -Werror, which one of musl's inline
 * functions does not pass. */
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wunused-parameter"
#include "pthread_impl.h"
#pragma clang diagnostic pop

/* The main thread's id in musl's own record of it (plan step E4.3): the process's, once the start message named it, as
 * on Linux, where the main thread's id is the process's. A dynamic program's musl read the library's first id from
 * set_tid_address when its dynamic linker installed the thread pointer, before the start message; a static program
 * installs the pointer after it and reads the process's id then. A file of its own, since musl's pthread_impl.h brings
 * syscall macros the rest of WitOS's part cannot take. */
hidden void __wit_main_thread_id(int tid)
{
    __pthread_self()->tid = tid;
}
