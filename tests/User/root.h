#ifndef WITOS_TESTS_ROOT_H
#define WITOS_TESTS_ROOT_H
#include "witos/types.h"
#include "witos/user_abi.h"

/* What the root task fixture's sources share (tests/User/root.c and root_mechanisms.c): the counted system call
 * whose status must be the expected one, a check of a condition with its failure code, and the end of the task. */

#if defined(__x86_64__)
/* A function the kernel enters directly, with no return address and the stack pointer 16-byte aligned. */
#define ENTRY_ATTRIBUTES __attribute__((force_align_arg_pointer))
#else
#define ENTRY_ATTRIBUTES
#endif

/* Hidden: the sources link into one static image, so calls between them stay direct, with no GOT for the flat image to
 * carry (the build passes -fno-plt). */
#define ROOT_SHARED __attribute__((visibility("hidden")))

ROOT_SHARED WIT_NORETURN void failed(WitU64 status);
ROOT_SHARED WitU64 expect(WitU64 number, WitU64 a0, WitU64 a1, WitU64 a2, WitU64 expected);
ROOT_SHARED void check(int condition, WitU64 code);

/* The mechanisms layer 2 has no other consumer of yet (plan step K8.2): waits on several objects, a suspended
 * thread's context, memory pressure and the reset of committed pages. */
ROOT_SHARED void root_mechanisms(void);

#endif
