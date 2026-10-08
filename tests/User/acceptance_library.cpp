#include "acceptance.h"
#include <atomic>

/* The C++ library of the phase S acceptance (plan step S5.4, lib/libacceptance.so): it throws, unwinds and keeps
 * thread-local objects through the shared libc++, libc++abi and libunwind its program uses too. */

namespace {
std::atomic<int> unwound;
std::atomic<int> destroyed;

struct Guard {
    ~Guard()
    {
        ++unwound;
    }
};

struct Counter {
    int Value = 0;

    ~Counter()
    {
        ++destroyed;
    }
};

thread_local Counter counter;
} // namespace

AcceptanceError::AcceptanceError(const std::string &what, int code) : std::runtime_error(what), Code(code) {}

AcceptanceError::~AcceptanceError() = default;

void acceptance_throw(int code)
{
    throw AcceptanceError("from the library", code);
}

int acceptance_call(int (*callback)(int), int value)
{
    Guard guard;
    return callback(value) + 1;
}

int acceptance_unwound()
{
    return unwound.load();
}

int acceptance_thread_local(int add)
{
    counter.Value += add;
    return counter.Value;
}

int acceptance_destroyed()
{
    return destroyed.load();
}

std::string acceptance_text(int count)
{
    return std::string(static_cast<std::string::size_type>(count), 'x');
}
