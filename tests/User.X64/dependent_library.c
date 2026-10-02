__declspec(dllimport) int LibraryAdd(int,int);
__declspec(dllimport) int LibraryOrdinal(void);
__declspec(dllimport) int LibraryData;
extern void* __imp_LibraryAdd;
int dependent_anchor;int* dependent_pointer=&dependent_anchor;
__declspec(dllexport) int DependentAdd(int a,int b){return LibraryAdd(a,b)+LibraryOrdinal()-LibraryData+*dependent_pointer;}
__declspec(dllexport) void* DependentIatSlot(void){return &__imp_LibraryAdd;}

void* _AddressOfReturnAddress(void);
#pragma intrinsic(_AddressOfReturnAddress)
__declspec(dllexport) __declspec(noinline) int DependentInspect(int(*callback)(void*,void*),int value)
{
    volatile int saved[8];saved[0]=value;
    void** returned=(void**)_AddressOfReturnAddress();
    return callback(returned,*returned)+saved[0];
}
