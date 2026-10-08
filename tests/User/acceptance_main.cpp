#include "acceptance.h"
#include "witos/memory_info.h"
#include "witos/syscall.h"
#include "witos/user_abi.h"
#include <cerrno>
#include <csetjmp>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <stdexcept>
#include <thread>
#include <vector>

/* The acceptance of phase S (plan step S5.4): a dynamic C++ program under musl's dynamic linker with the shared C++
 * runtime (libc++.so.1, libc++abi.so.1, libunwind.so.1) and its own library, lib/libacceptance.so. It catches an
 * exception the library throws by its own type and by the standard bases, unwinds a library frame with an exception of
 * its own, runs threads that throw and catch across the modules and keep the library's thread_local objects, which
 * libc++abi destroys at each thread's exit, carries an exception between threads with exception_ptr, frees memory the
 * library allocated, and recovers from a SIGSEGV through its handler and siglongjmp; then it reports the kernel
 * reservations its process holds. A failed check exits with 1. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#else
#define ISA_NAME "aarch64"
#endif

namespace {
int checks;
sigjmp_buf escape;
volatile sig_atomic_t fault_signal;

void check(bool condition, const char *what)
{
    ++checks;
    if (!condition) {
        std::printf("[PHASE-S] FAILED: %s\n", what);
        std::exit(1);
    }
}

int throwing_callback(int value)
{
    throw std::out_of_range(value > 0 ? "positive" : "other");
}

void on_fault(int sig)
{
    fault_signal = sig;
    siglongjmp(escape, 1);
}

/* A thread: the library's thread_local counter is its own, and an exception crosses the modules inside it. */
int worker(int add)
{
    int caught = 0;
    try {
        acceptance_throw(add);
    } catch (const AcceptanceError &error) {
        caught = error.Code;
    }
    return acceptance_thread_local(add) * 100 + caught;
}
} // namespace

int main()
{
    std::printf("[PHASE-S] started on " ISA_NAME "\n");

    /* An exception of the library's type, caught by that type and by its standard bases. */
    try {
        acceptance_throw(7);
        check(false, "no throw");
    } catch (const AcceptanceError &error) {
        check(error.Code == 7 && std::strcmp(error.what(), "from the library") == 0,
            "the library's exception by its type");
    }
    try {
        acceptance_throw(8);
    } catch (const std::runtime_error &error) {
        check(
            dynamic_cast<const AcceptanceError *>(&error) != nullptr, "the library's exception by std::runtime_error");
    }
    try {
        acceptance_throw(9);
    } catch (const std::exception &error) {
        check(std::strcmp(error.what(), "from the library") == 0, "the library's exception by std::exception");
    }

    /* The program's exception unwinds a library frame, whose destructor runs. */
    try {
        acceptance_call(throwing_callback, 1);
        check(false, "no throw through the library");
    } catch (const std::out_of_range &error) {
        check(std::strcmp(error.what(), "positive") == 0 && acceptance_unwound() == 1, "unwinding through the library");
    }

    /* Threads: exceptions across the modules in each, the library's thread_local per thread and destroyed at exit. */
    check(acceptance_thread_local(5) == 5, "the main thread's thread_local");
    int results[2] = {0, 0};
    {
        std::thread first([&results] { results[0] = worker(1); });
        std::thread second([&results] { results[1] = worker(2); });
        first.join();
        second.join();
    }
    check(results[0] == 101 && results[1] == 202, "exceptions and thread_local in two threads");
    check(acceptance_destroyed() == 2 && acceptance_thread_local(0) == 5,
        "thread_local destructors at the threads' exits");

    /* An exception carried from a thread to the main thread. */
    std::exception_ptr carried;
    std::thread carrier([&carried] {
        try {
            acceptance_throw(11);
        } catch (...) {
            carried = std::current_exception();
        }
    });
    carrier.join();
    try {
        std::rethrow_exception(carried);
    } catch (const AcceptanceError &error) {
        check(error.Code == 11, "exception_ptr between threads");
    }

    /* Memory the library allocated, freed by the program; the containers of the shared libc++. */
    std::vector<std::string> texts;
    for (int i = 1; i <= 64; ++i) {
        texts.push_back(acceptance_text(i * 17));
    }
    check(texts.back().size() == 64 * 17 && texts.front() == std::string(17, 'x'), "strings of the library");
    texts.clear();

    /* A synchronous signal: a write to an unmapped page reaches the handler, which leaves with siglongjmp. */
    struct sigaction action;
    std::memset(&action, 0, sizeof(action));
    action.sa_handler = on_fault;
    check(sigaction(SIGSEGV, &action, nullptr) == 0, "sigaction");
    if (sigsetjmp(escape, 1) == 0) {
        *reinterpret_cast<volatile int *>(16) = 1;
        check(false, "no fault");
    }
    check(fault_signal == SIGSEGV, "a SIGSEGV handled and left with siglongjmp");

    WitUserMemoryInfo memory;
    WitU64 copied = 0;
    std::memset(&memory, 0, sizeof(memory));
    check(wit_syscall(WIT_CALL_MEMORY_QUERY, reinterpret_cast<WitU64>(&memory), sizeof(memory), WIT_MEMORY_INFO_VERSION,
              &copied) == WIT_STATUS_OK,
        "MEMORY_QUERY");
    std::printf("[PHASE-S] dynamic C++ program on " ISA_NAME ": %d checks passed, %u of %u reservations\n", checks,
        memory.ReservationCount, memory.ReservationCapacity);
    return 0;
}
