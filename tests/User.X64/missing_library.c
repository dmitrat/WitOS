__declspec(dllimport) int MissingSymbol(void);
int missing_anchor;int* missing_pointer=&missing_anchor;
__declspec(dllexport) int MissingCall(void){return MissingSymbol()+*missing_pointer;}
