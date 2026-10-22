/* The layout of the kernel's records that guest-threads reads from a guest's memory (plan step P3): compiled by the
 * pinned clang for the architecture's kernel triple with the kernel's own headers to LLVM IR, never linked, so that
 * every offset is the compiler's and none is written by hand. The tool reads the constant array's values in order. */
#include "witos/types.h"
#include "user.h"
#if defined(__aarch64__)
#include "a64.h"
#endif

#define OFFSET(type, field) ((unsigned long long)__builtin_offsetof(type, field))

const unsigned long long wit_layout[] = {
    sizeof(WitUserProcess),
    OFFSET(WitUserProcess, Id),
    OFFSET(WitUserProcess, State),
    OFFSET(WitUserProcess, Threads),
    OFFSET(WitUserProcess, ThreadTop),
    OFFSET(WitUserProcess, CurrentThread),
    sizeof(WitUserThread),
    OFFSET(WitUserThread, State),
    OFFSET(WitUserThread, WaitKind),
    OFFSET(WitUserThread, WaitHandle),
    OFFSET(WitUserThread, WaitHandles),
    OFFSET(WitUserThread, WaitCount),
    OFFSET(WitUserThread, ActivationCount),
    OFFSET(WitUserThread, Deadline),
    OFFSET(WitUserThread, Handle),
    OFFSET(WitUserThread, Processor),
    OFFSET(WitUserThread, SuspendCount),
    OFFSET(WitUserThread, Context),
    OFFSET(WitUserThread, StackBottom),
    OFFSET(WitUserThread, StackTop),
    OFFSET(WitUserThread, Affinity),
    WIT_PROCESS_CAPACITY,
    WIT_PROCESSOR_CAPACITY,
    WIT_WAIT_ANY_CAPACITY,
#if defined(__aarch64__)
    OFFSET(WitA64Frame, X),
    OFFSET(WitA64Frame, Sp),
    OFFSET(WitA64Frame, Elr),
    OFFSET(WitA64Frame, Spsr),
    0, /* reserved: keeps the count of entries the same on every architecture */
#else
    0,
    0,
    0,
    0,
    0,
#endif
    OFFSET(WitUserProcess, Space.Root),
};
