/* C++ exception scenarios (P6.4.e, S4): written for the frozen line's C++ runtime against vcruntime's trace, they run
 * unchanged in their Itanium form over layer 2's LLVM runtimes (tests/User/cxx_main.cpp) and natively on Linux in CI's
 * itanium-reference job. Every observable step is a trace token through cxx_trace, which the harness provides; the
 * source uses no library, so only the exception runtime differs between the builds. */
extern "C" void cxx_trace(const char *text);
extern "C" int __cdecl __uncaught_exceptions();

namespace {

void trace(const char *label, long long value)
{
    char text[48];
    unsigned at = 0;
    while (*label && at < 24) {
        text[at++] = *label++;
    }
    text[at++] = ':';
    unsigned long long magnitude = value < 0 ? 0ULL - (unsigned long long)value : (unsigned long long)value;
    if (value < 0) {
        text[at++] = '-';
    }
    char digits[20];
    unsigned count = 0;
    do {
        digits[count++] = (char)('0' + magnitude % 10);
        magnitude /= 10;
    } while (magnitude);
    while (count) {
        text[at++] = digits[--count];
    }
    text[at] = 0;
    cxx_trace(text);
}

int live;

struct Tracker {
    int id;

    explicit Tracker(int identity) : id(identity)
    {
        trace("+", id);
    }

    ~Tracker()
    {
        trace("-", id);
    }
};

struct Base {
    int code;

    explicit Base(int value) : code(value)
    {
        ++live;
    }

    Base(const Base &other) : code(other.code)
    {
        ++live;
        trace("copy", code);
    }

    virtual ~Base()
    {
        --live;
        trace("drop", code);
    }
};

struct Derived : Base {
    explicit Derived(int value) : Base(value) {}
};

struct Other {};

struct Left {
    int left = 11;
};

struct Right {
    int right = 22;
};

struct Both : Left, Right {
    int both = 33;
};

struct VirtualBase {
    int value = 190;
};

struct VirtualLeft : virtual VirtualBase {
    int left = 191;
};

struct VirtualRight : virtual VirtualBase {
    int right = 192;
};

struct Diamond : VirtualLeft, VirtualRight {
    int diamond = 193;

    Diamond() = default;

    Diamond(const Diamond &other) : VirtualBase(other), VirtualLeft(other), VirtualRight(other), diamond(other.diamond)
    {
        trace("copy", diamond);
    }
};

struct Plain {
    int a, b, c;
};

struct Probe {
    ~Probe()
    {
        trace("unwinding", __uncaught_exceptions());
    }
};

__declspec(noinline) void throw_int(int value)
{
    throw value;
}

__declspec(noinline) void throw_derived(int value)
{
    throw Derived(value);
}

__declspec(noinline) void level3()
{
    Tracker tracker(3);
    throw_int(30);
}

__declspec(noinline) void level2()
{
    Tracker tracker(2);
    level3();
}

__declspec(noinline) void no_match()
{
    try {
        Tracker tracker(151);
        throw_derived(150);
    } catch (Other &) {
        trace("S15other", 0);
    }
}

__declspec(noinline) void rethrow_current()
{
    throw;
}

__declspec(noinline) void level_derived()
{
    Tracker tracker(171);
    throw_derived(172);
}

void s1()
{
    try {
        throw_int(7);
    } catch (int value) {
        trace("S1", value);
    }
}

void s2()
{
    try {
        throw_derived(5);
    } catch (Other &) {
        trace("S2other", 0);
    } catch (Base &base) {
        trace("S2", base.code);
    }
}

void s3()
{
    try {
        throw_derived(6);
    } catch (Base base) {
        trace("S3", base.code);
    }
}

void s4()
{
    try {
        throw Other();
    } catch (...) {
        trace("S4", 1);
    }
}

void s5()
{
    try {
        Tracker tracker(1);
        level2();
    } catch (int value) {
        trace("S5", value);
    }
}

void s6()
{
    try {
        try {
            Tracker tracker(61);
            throw_derived(60);
        } catch (int) {
            trace("S6inner", 0);
        }
    } catch (Base &base) {
        trace("S6", base.code);
    }
}

void s7()
{
    try {
        try {
            throw_derived(70);
        } catch (Base &base) {
            base.code = 71;
            throw;
        }
    } catch (Base &base) {
        trace("S7", base.code);
    }
}

void s8()
{
    try {
        try {
            throw_derived(80);
        } catch (Base &base) {
            Tracker tracker(81);
            throw Derived(base.code + 2);
        }
    } catch (Base &base) {
        trace("S8", base.code);
    }
}

void s9()
{
    Both both;
    try {
        throw &both;
    } catch (Right *right) {
        trace("S9", right->right);
        trace("S9adjust", (const char *)right - (const char *)&both);
    }
}

void s10()
{
    const Derived constant(100);
    try {
        throw static_cast<const Base *>(&constant);
    } catch (Base *) {
        trace("S10mutable", 0);
    } catch (const Base *base) {
        trace("S10", base->code);
    }
}

void s11()
{
    for (int i = 0; i < 4; ++i) {
        try {
            if (i & 1) {
                throw_int(110 + i);
            }
            trace("S11ok", i);
        } catch (int value) {
            trace("S11", value);
        }
    }
}

void s12()
{
    try {
        throw_int(120);
    } catch (int value) {
        try {
            throw_derived(value + 1);
        } catch (Base &base) {
            trace("S12", base.code);
        }
        trace("S12after", value);
    }
}

void s13()
{
    try {
        Probe probe;
        throw_int(130);
    } catch (int value) {
        trace("S13", value);
        trace("S13caught", __uncaught_exceptions());
    }
}

void s14()
{
    try {
        throw_derived(140);
    } catch (Derived derived) {
        trace("S14", derived.code);
    }
}

void s15()
{
    try {
        no_match();
    } catch (Base &base) {
        trace("S15", base.code);
    }
}

void s16()
{
    try {
        try {
            throw_derived(160);
        } catch (Base &) {
            rethrow_current();
        }
    } catch (Derived &derived) {
        trace("S16", derived.code);
    }
}

void s17()
{
    try {
        Tracker tracker(170);
        level_derived();
    } catch (Base base) {
        trace("S17", base.code);
    }
}

void s18()
{
    try {
        try {
            throw_int(180);
        } catch (int) {
            try {
                throw_derived(181);
            } catch (Base &) {
                Tracker tracker(182);
                throw_int(183);
            }
        }
    } catch (int value) {
        trace("S18", value);
    }
}

void s19()
{
    try {
        throw Diamond();
    } catch (VirtualBase &base) {
        trace("S19", base.value);
    }
    try {
        throw Diamond();
    } catch (VirtualRight right) {
        trace("S19right", right.right + right.value);
    }
}

void s20()
{
    try {
        throw Plain{200, 201, 202};
    } catch (Plain plain) {
        trace("S20", plain.a + plain.b + plain.c);
    }
}

void s21()
{
    try {
        try {
            throw_derived(210);
        } catch (...) {
            trace("S21inner", 0);
            throw;
        }
    } catch (Base &base) {
        trace("S21", base.code);
    }
}

} // namespace

extern "C" int cxx_exceptions_run()
{
    s1();
    s2();
    s3();
    s4();
    s5();
    s6();
    s7();
    s8();
    s9();
    s10();
    s11();
    s12();
    s13();
    s14();
    s15();
    s16();
    s17();
    s18();
    s19();
    s20();
    s21();
    trace("live", live);
    return live;
}
