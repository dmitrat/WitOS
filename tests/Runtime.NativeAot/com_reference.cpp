#include <windows.h>
#include <objbase.h>
#include <stdio.h>
static DWORD WINAPI worker(void*){
 APTTYPE t;APTTYPEQUALIFIER q;
 if(CoGetApartmentType(&t,&q)!=S_OK||t!=APTTYPE_MTA||q!=APTTYPEQUALIFIER_IMPLICIT_MTA)return 1;
 CoUninitialize();if(CoGetApartmentType(&t,&q)!=S_OK||q!=APTTYPEQUALIFIER_IMPLICIT_MTA)return 2;
 if(CoInitializeEx(nullptr,0)!=S_OK||CoGetApartmentType(&t,&q)!=S_OK||q!=APTTYPEQUALIFIER_NONE)return 3;
 CoUninitialize();if(CoGetApartmentType(&t,&q)!=S_OK||q!=APTTYPEQUALIFIER_IMPLICIT_MTA)return 4;
 return 0;
}
int main(){
 APTTYPE t=(APTTYPE)99;APTTYPEQUALIFIER q=(APTTYPEQUALIFIER)99;
 HRESULT h=CoGetApartmentType(&t,&q);printf("initial=%08lx type=%d qualifier=%d\n",(unsigned long)h,t,q);
 if(h!=CO_E_NOTINITIALIZED||t!=APTTYPE_CURRENT||q!=APTTYPEQUALIFIER_NONE)return 1;
 t=(APTTYPE)99;q=(APTTYPEQUALIFIER)99;
 h=CoGetApartmentType(nullptr,&q);printf("null=%08lx qualifier=%d\n",(unsigned long)h,q);
 if(h!=E_INVALIDARG||q!=99)return 2;
 if(CoInitializeEx(nullptr,COINIT_MULTITHREADED)!=S_OK||CoInitializeEx(nullptr,COINIT_MULTITHREADED)!=S_FALSE)return 3;
 if(CoGetApartmentType(&t,&q)!=S_OK||t!=APTTYPE_MTA||q!=APTTYPEQUALIFIER_NONE)return 4;
 if(CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED)!=RPC_E_CHANGED_MODE)return 5;
 CoUninitialize();if(CoGetApartmentType(&t,&q)!=S_OK)return 6;
 CoUninitialize();if(CoGetApartmentType(&t,&q)!=CO_E_NOTINITIALIZED)return 7;
 CoUninitialize();if(CoInitializeEx(nullptr,COINIT_DISABLE_OLE1DDE|COINIT_SPEED_OVER_MEMORY)!=S_OK)return 8;
 HANDLE child=CreateThread(nullptr,0,worker,nullptr,0,nullptr);DWORD result=99;
 if(!child||WaitForSingleObject(child,INFINITE)!=WAIT_OBJECT_0||!GetExitCodeThread(child,&result)||result||!CloseHandle(child))return 9;
 CoUninitialize();puts("PASS: Windows explicit and implicit MTA lifecycle reference");return 0;
}
