/* A DLL with real C++ thread_local objects (P6.4.c): constructors and destructors record into the sink, so the host and
 * the guest can compare when each thread builds and destroys its objects. It links library_dynamic_tls.cpp, the WitOS
 * stand-in for the CRT's dynamic TLS support, and imports only the sink. Record: who:8 | object:8 | serial:16 | low 32
 * bits of the thread's anchor address, which names the thread. */
extern "C" __declspec(dllimport) void SinkRecord(unsigned long long event);

#define WHO_CONSTRUCT 4ULL
#define WHO_DESTRUCT 5ULL

static int constructions; // shared by every thread: serials order construction across threads
thread_local int tls_anchor = 0;

static void record(unsigned long long who, int object, int serial)
{
    SinkRecord(who << 56 |
        (unsigned long long)(object & 0xFF) << 48 |
        ((unsigned long long)serial & 0xFFFF) << 32 |
        ((unsigned long long)&tls_anchor & 0xFFFFFFFF));
}

struct Tracked {
    int object, serial;

    explicit Tracked(int identity) : object(identity), serial(++constructions)
    {
        record(WHO_CONSTRUCT, object, serial);
    }

    ~Tracked()
    {
        record(WHO_DESTRUCT, object, serial);
    }
};

thread_local Tracked first_object(1);
thread_local Tracked second_object(2);

/* The first access of a thread that was not attached initializes its objects on demand. */
extern "C" __declspec(dllexport) int TouchObjects()
{
    return first_object.serial * 256 + second_object.serial;
}

/* The calling thread's own record, in the same encoding, so a host can name the thread of every other record. */
extern "C" __declspec(dllexport) void TlsObjectsRecord(unsigned long long who)
{
    record(who, 0, 0);
}
