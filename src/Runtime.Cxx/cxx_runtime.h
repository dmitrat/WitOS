#pragma once
/* Internal state and platform services of the WitOS C++ runtime (P6.4.e). The runtime is linked statically into
 * every module that uses C++ exceptions; each copy keeps its own per-thread state, and exceptions cross modules
 * through the image base that the exception record carries. */
#include "exception_data.h"

namespace WitCxx {

/* A catch whose funclet runs: the frame that holds its try block, that frame's state while the catch runs (before the
 * try) and the exception object it handles. The frame keeps the IP inside the try, so its handler takes the state
 * from here. When an exception leaves the catch, the record stays, marked with that exception, until the exception's
 * catch begins: the unwind still passes the frame after the catch's own cleanup. */
struct ActiveCatch {
    u64 Establisher;
    i32 State;
    void *Object;
    const ThrowInfo *Info;
    u64 ImageBase;
    void *Leaving; // the exception whose unwind left this catch, or null while the catch runs
};

constexpr u32 CATCH_CAPACITY = 64; // nested catches and catches being left on one thread

struct ThreadState {
    ActiveCatch Catches[CATCH_CAPACITY];
    u32 CatchCount;
    int Uncaught;
    void *Unwinding; // the object of the C++ exception whose catch the unwinder is reaching, if any
};

ThreadState &Thread();

[[noreturn]] void Fatal();
u64 ImageBaseOf(const void *address);
[[noreturn]] void Raise(const ULONG_PTR *arguments);
void Unwind(u64 frame, u64 ip, EXCEPTION_RECORD *record, CONTEXT *context, PUNWIND_HISTORY_TABLE history);

/* The running catch whose object a rethrow raises again: the innermost one that no exception left. */
const ActiveCatch *CurrentCatch();

/* Drops the records of catches that the exception with this object left. */
void ForgetLeft(void *object);

/* Calls the exception object's destructor unless a running catch still handles it. */
void Destroy(void *object, const ThrowInfo *info, u64 imageBase);

} // namespace WitCxx
