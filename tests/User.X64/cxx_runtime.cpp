/* C++ runtime scenarios (P6.4.g): allocation, arrays, std::exception, thread-safe statics, GS cookies and guarded
 * indirect calls. Built with /GS and /guard:cf and run after tests/User.X64/cxx_exceptions.cpp by the same harnesses;
 * static objects are never destroyed in the trace, since both harnesses print it before the process exits. */
#include <new>
#include <stdexcept>
#include "cxx_trace.h"

using CxxTrace::Number;
using CxxTrace::Text;

namespace {

int serial;

struct Counted {
    int id;

    Counted() : id(++serial)
    {
        Number("+", id);
    }

    ~Counted()
    {
        Number("-", id);
    }
};

int built;

/* The third element of an array throws from its constructor. */
struct Thrower {
    int id;

    Thrower() : id(++built)
    {
        if (id == 3) {
            throw id;
        }
        Number("t", id);
    }

    ~Thrower()
    {
        Number("~t", id);
    }
};

Counted &shared()
{
    static Counted value;
    return value;
}

int attempts;

int flaky()
{
    if (++attempts == 1) {
        throw attempts;
    }
    return 70 + attempts;
}

int &retried()
{
    static int value = flaky();
    return value;
}

__declspec(noinline) void consume(volatile char *buffer, int size)
{
    for (int i = 0; i < size; ++i) {
        buffer[i] = (char)(buffer[i] + i);
    }
}

__declspec(noinline) void guarded(int code)
{
    char buffer[64] = {};
    Counted counted;
    consume(buffer, sizeof(buffer));
    if (code) {
        throw code;
    }
}

int twice(int value)
{
    return 2 * value;
}

void g1()
{
    int *value = new int(41);
    Number("G1", *value + 1);
    delete value;
}

void g2()
{
    Counted *array = new Counted[3];
    Number("G2", array[2].id - array[0].id);
    delete[] array;
}

void g3()
{
    volatile size_t huge = (size_t)1 << 62;
    try {
        char *never = new char[huge];
        Number("G3new", never != nullptr);
    } catch (const std::bad_alloc &failure) {
        Text("G3", failure.what());
    }
}

void g4()
{
    volatile size_t huge = (size_t)1 << 62;
    char *never = new (std::nothrow) char[huge];
    Number("G4", never == nullptr);
}

void g5()
{
    try {
        throw std::runtime_error("runtime failure");
    } catch (std::runtime_error copied) {
        Text("G5", copied.what());
    }
}

void g6()
{
    Number("G6", shared().id);
    Number("G6again", shared().id);
}

void g7()
{
    try {
        Number("G7value", retried());
    } catch (int attempt) {
        Number("G7first", attempt);
    }
    Number("G7", retried());
}

void g8()
{
    try {
        guarded(80);
    } catch (int value) {
        Number("G8", value);
    }
}

void g9()
{
    int (*volatile function)(int) = &twice;
    Number("G9", function(45));
}

void g10()
{
    built = 0;
    try {
        Thrower *array = new Thrower[4];
        delete[] array;
    } catch (int value) {
        Number("G10", value);
    }
}

} // namespace

extern "C" void cxx_runtime_run()
{
    g1();
    g2();
    g3();
    g4();
    g5();
    g6();
    g7();
    g8();
    g9();
    g10();
}
