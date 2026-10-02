__declspec(dllimport) int LibraryAdd(int, int);
/* Genuine compiler static TLS, prepared for multi-module guest integration.
 * The pointer requires a real DIR64 template relocation before seed capture. */
int tls_anchor = 5;
__declspec(thread) int tls_value = 731;
__declspec(thread) int *tls_pointer = &tls_anchor;
__declspec(thread) unsigned char tls_zero[64];
extern unsigned _tls_index;

__declspec(dllexport) int TlsValue(void)
{
    return tls_value + *tls_pointer;
}

__declspec(dllexport) void TlsSet(int value)
{
    tls_value = value;
}

__declspec(dllexport) void *TlsAddress(void)
{
    return &tls_value;
}

__declspec(dllexport) unsigned TlsIndex(void)
{
    return _tls_index;
}

__declspec(dllexport) int TlsZero(void)
{
    unsigned value = 0;
    for (unsigned i = 0; i < sizeof(tls_zero); ++i) {
        value |= tls_zero[i];
    }
    return (int)value;
}

unsigned tls_entry_calls;

__declspec(dllexport) unsigned EntryCalls(void)
{
    return tls_entry_calls;
}

int StaticTlsEntry(void *base, unsigned reason, void *reserved)
{
    (void)base;
    (void)reason;
    (void)reserved;
    ++tls_entry_calls;
    return LibraryAdd(1, 2) == 3;
}
