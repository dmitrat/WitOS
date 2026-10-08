#ifndef WITOS_TESTS_ACCEPTANCE_H
#define WITOS_TESTS_ACCEPTANCE_H
#include <stdexcept>
#include <string>

/* The interface of the phase S acceptance library (plan step S5.4, tests/User/acceptance_library.cpp, built as
 * lib/libacceptance.so) to its program (tests/User/acceptance_main.cpp). The exception type's key function, its
 * destructor, lies in the library, so its type information and virtual table are the library's: the program's catch
 * matches them across modules through the shared libc++abi. */
struct __attribute__((visibility("default"))) AcceptanceError : std::runtime_error {
    AcceptanceError(const std::string &what, int code);
    ~AcceptanceError() override;
    int Code;
};

/* Throws AcceptanceError with the code. */
[[noreturn]] void acceptance_throw(int code);
/* Calls the callback through a frame of the library that holds an object with a destructor and catches nothing: an
 * exception of the callback unwinds the library's frame and counts that destructor. */
int acceptance_call(int (*callback)(int), int value);
/* The destructors the unwinding of acceptance_call ran. */
int acceptance_unwound();
/* Adds to the calling thread's thread_local counter of the library and returns it. */
int acceptance_thread_local(int add);
/* The library's thread_local objects destroyed at their threads' exits (libc++abi's __cxa_thread_atexit). */
int acceptance_destroyed();
/* A string the library allocates and the program frees. */
std::string acceptance_text(int count);
#endif
