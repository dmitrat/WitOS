/* A DLL with static TLS, two PE TLS callbacks and an entry point. Every call records who ran, the reason, the calling
 * thread's TLS value and the low half of its TLS address, which names the thread without any OS import. The host and the
 * guest compare the loader's order from these records. The only import is the sink, which also gives Windows the import
 * its loader needs to set up the TLS of a CRT-free DLL. */
__declspec(dllimport) void SinkRecord(unsigned long long event);

#define WHO_FIRST 1ULL
#define WHO_SECOND 2ULL
#define WHO_ENTRY 3ULL

#pragma section(".tls", long, read, write)
#pragma section(".tls$ZZZ", long, read, write)
__declspec(allocate(".tls")) char _tls_start = 0;
__declspec(allocate(".tls$ZZZ")) char _tls_end = 0;
unsigned _tls_index;

__declspec(thread) int tls_marker = 731;

/* who:8 | reason:8 | TLS value:16 | low 32 bits of the TLS address */
static void record(unsigned long long who, unsigned long reason)
{
    SinkRecord(who << 56 |
        (unsigned long long)(reason & 0xFF) << 48 |
        ((unsigned long long)tls_marker & 0xFFFF) << 32 |
        ((unsigned long long)&tls_marker & 0xFFFFFFFF));
}

static void __stdcall first_callback(void *base, unsigned long reason, void *reserved)
{
    (void)base;
    (void)reserved;
    record(WHO_FIRST, reason);
}

static void __stdcall second_callback(void *base, unsigned long reason, void *reserved)
{
    (void)base;
    (void)reserved;
    record(WHO_SECOND, reason);
}

typedef void(__stdcall *TlsCallback)(void *, unsigned long, void *);

/* The callback list is the null-terminated array between the A and Z sentinels, as the CRT lays it out. */
#pragma section(".CRT$XLA", long, read)
#pragma section(".CRT$XLB", long, read)
#pragma section(".CRT$XLZ", long, read)
__declspec(allocate(".CRT$XLA")) const TlsCallback tls_callbacks_begin = 0;
__declspec(allocate(".CRT$XLB")) const TlsCallback tls_callbacks[] = {first_callback, second_callback};
__declspec(allocate(".CRT$XLZ")) const TlsCallback tls_callbacks_end = 0;

typedef struct TlsDirectory {
    const void *Start, *End, *Index;
    const TlsCallback *Callbacks;
    unsigned ZeroFill, Characteristics;
} TlsDirectory;

const TlsDirectory _tls_used = {&_tls_start, &_tls_end, &_tls_index, &tls_callbacks_begin + 1, 0, 0};

__declspec(dllexport) int TlsMarker(void)
{
    return tls_marker;
}

__declspec(dllexport) void TlsMarkerSet(int value)
{
    tls_marker = value;
}

/* The calling thread's own record, in the same encoding, so a host can name the thread of every other record. */
__declspec(dllexport) void TlsMarkerRecord(unsigned long long who)
{
    record(who, 0);
}

int CallbackEntry(void *base, unsigned long reason, void *reserved)
{
    (void)base;
    (void)reserved;
    record(WHO_ENTRY, reason);
    return 1;
}
