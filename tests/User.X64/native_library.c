int library_value=731;
int* library_pointer=&library_value;
int LibraryAdd(int a,int b){return a+b+*library_pointer-731;}
int LibraryOrdinal(void){return *library_pointer;}
unsigned long long* library_trace_target;
