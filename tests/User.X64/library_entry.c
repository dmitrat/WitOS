__declspec(dllimport) int LibraryData;
__declspec(dllimport) unsigned long long *LibraryTraceTarget;
#ifdef INIT_PARENT
__declspec(dllimport) int InitValue(void);
#endif
extern char __ImageBase;
extern unsigned short LibraryCurrentCs(void);
int entry_anchor;
int *entry_local_pointer = &entry_anchor;
#ifdef INIT_PARENT
#define ATTACH_DIGIT 5
#define DETACH_DIGIT 6
#else
#ifdef INIT_FAIL
#define ATTACH_DIGIT 3
#define DETACH_DIGIT 4
#else
#define ATTACH_DIGIT 1
#define DETACH_DIGIT 2
#endif
#endif
int LibraryEntry(void *base, unsigned reason, void *reserved)
{
    if (base != &__ImageBase || (reason == 1 && reserved) || LibraryCurrentCs() != 0x33) {
        return 0;
    }
    if (reason == 1) {
#ifdef INIT_PARENT
        if (InitValue() != 7311) {
            return 0;
        }
#endif
        LibraryData = LibraryData * 10 + ATTACH_DIGIT;
        if (LibraryTraceTarget) {
            *LibraryTraceTarget = (unsigned long long)LibraryData;
        }
#ifdef INIT_FAIL
        return 0;
#endif
    } else if (!reason) {
        LibraryData = LibraryData * 10 + DETACH_DIGIT + (reserved ? 2 : 0);
        if (LibraryTraceTarget) {
            *LibraryTraceTarget = (unsigned long long)LibraryData;
        }
    } else {
        return 0;
    }
    return 1;
}
#ifdef INIT_PARENT
__declspec(dllexport) int ParentValue(void)
{
    return LibraryData + *entry_local_pointer;
}
#else
__declspec(dllexport) int InitValue(void)
{
    return LibraryData + *entry_local_pointer;
}
#endif
