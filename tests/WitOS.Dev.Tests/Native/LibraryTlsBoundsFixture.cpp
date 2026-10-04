/* Bounds of the WitOS dynamic TLS support (P6.4.c): a thread of a DLL runs WIT_NATIVE_TLS_MAX_INITIALIZERS
 * namespace-scope initializers and registers WIT_NATIVE_TLS_MAX_DESTRUCTORS destructors; with BEYOND defined, one more
 * ends the process with __fastfail instead of losing work. INITIALIZERS selects the initializer table, otherwise
 * function-local thread_local objects register destructors without initializer entries. Windows sets up no TLS for a
 * DLL without imports, so Touch reports through the sink. */
extern "C" __declspec(dllimport) void SinkRecord(unsigned long long event);

static volatile int seed;
static int live;

#define EIGHT(X, a) X(a##0) X(a##1) X(a##2) X(a##3) X(a##4) X(a##5) X(a##6) X(a##7)
#if defined(BEYOND)
#define ALL(X) EIGHT(X, 1) EIGHT(X, 2) EIGHT(X, 3) EIGHT(X, 4) X(50)
#else
#define ALL(X) EIGHT(X, 1) EIGHT(X, 2) EIGHT(X, 3) EIGHT(X, 4)
#endif

#if defined(INITIALIZERS)
#define OBJECT(n) thread_local int value##n = ++live + seed;
ALL(OBJECT)

/* The linker keeps a thread_local initializer only with its referenced variable. */
#define USE(n) sum += value##n;

extern "C" __declspec(dllexport) int Touch()
{
    int sum = 0;
    ALL(USE)
    SinkRecord((unsigned long long)live);
    return sum ? live : -1;
}
#else
struct Counted {
    Counted()
    {
        ++live;
    }

    ~Counted()
    {
        --live;
    }
};

#define OBJECT(n) \
    static Counted &object##n() \
    { \
        static thread_local Counted value; \
        return value; \
    }
ALL(OBJECT)

#define USE(n) object##n();

extern "C" __declspec(dllexport) int Touch()
{
    ALL(USE)
    SinkRecord((unsigned long long)live);
    return live;
}
#endif
