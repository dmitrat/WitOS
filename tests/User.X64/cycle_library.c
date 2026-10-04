#ifdef CYCLE_A
#define EXPORT CycleA
#define IMPORT CycleB
#else
#define EXPORT CycleB
#define IMPORT CycleA
#endif
__declspec(dllimport) int IMPORT(int);
int cycle_anchor;
int *cycle_pointer = &cycle_anchor;

__declspec(dllexport) int EXPORT(int n)
{
    return (n ? IMPORT(n - 1) : 0) + 1 + *cycle_pointer;
}
