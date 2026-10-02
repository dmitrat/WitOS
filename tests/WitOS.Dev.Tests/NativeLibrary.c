#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "witos/pe.h"
static WitPeImage plan;
static unsigned tests;
static unsigned char* area;static const size_t capacity=1024*1024;
static unsigned u32(const unsigned char* p){return p[0]|((unsigned)p[1]<<8)|((unsigned)p[2]<<16)|((unsigned)p[3]<<24);}
static void put32(unsigned char* p,unsigned value){for(unsigned i=0;i<4;++i)p[i]=(unsigned char)(value>>(8*i));}
static int check(const unsigned char* file,unsigned size,int expected)
{
    DWORD old;if(!VirtualProtect(area,capacity,PAGE_READWRITE,&old))return 0;
    unsigned char* input=area+capacity-size;if(size)memcpy(input,file,size);
    if(!VirtualProtect(area,capacity,PAGE_READONLY,&old))return 0;
    WitPeStatus result=wit_pe_validate_profile(input,size,&plan,WIT_PE_LIBRARY|WIT_PE_UNWIND_RUNTIME);++tests;
    if((expected>=0&&(int)result!=expected)||(expected==-2&&result==WitPeOk)){
        printf("FAIL export corpus case=%u expected=%d actual=%d size=%u\n",tests,expected,result,size);return 0;
    }
    return 1;
}
int main(int argc,char** argv)
{
    if(argc!=2)return 1;FILE* f=0;if(fopen_s(&f,argv[1],"rb")||!f)return 2;
    fseek(f,0,SEEK_END);long length=ftell(f);rewind(f);if(length<=0||length>65536)return 3;
    unsigned char* file=malloc((size_t)length);unsigned char* changed=malloc((size_t)length);
    if(!file||!changed||fread(file,1,(size_t)length,f)!=(size_t)length)return 4;fclose(f);
    area=VirtualAlloc(0,capacity+4096,MEM_RESERVE,PAGE_NOACCESS);if(!area||!VirtualAlloc(area,capacity,MEM_COMMIT,PAGE_READWRITE))return 5;
    if(!check(file,(unsigned)length,WitPeOk))return 6;
    if(wit_pe_validate(file,(unsigned)length,&plan)!=WitPeUnsupportedImage)return 7;
    if(wit_pe_validate_profile(file,(unsigned)length,&plan,WIT_PE_LIBRARY|WIT_PE_UNWIND_RUNTIME)!=WitPeOk)return 8;
    HMODULE module=LoadLibraryA(argv[1]);if(!module){printf("LoadLibrary error=%lu\n",GetLastError());return 9;}
    const char* names[]={"LibraryAdd","AliasAdd","LibraryData","LibraryPointer","missing","libraryadd"};
    for(unsigned i=0;i<6;++i){unsigned rva=99;FARPROC found=GetProcAddress(module,names[i]);WitU64 address=0;memcpy(&address,&found,sizeof(address));
        if(wit_pe_export_find(file,&plan,names[i],(unsigned)strlen(names[i]),0,&rva)!=WitPeOk||rva!=(found?(unsigned)(address-(WitU64)module):0))return 10;}
    for(unsigned ordinal=6;ordinal<=13;++ordinal){unsigned rva=99;
        if(wit_pe_export_find(file,&plan,0,0,ordinal,&rva)!=WitPeOk)return 11;
        // Windows may return a non-null bogus value for a hole; compare only
        // actual exported ordinals. Holes must be absent in the checked API.
        if(ordinal==6||ordinal==11||ordinal==13){if(rva)return 12;continue;}
        FARPROC found=GetProcAddress(module,(LPCSTR)(uintptr_t)ordinal);WitU64 address=0;memcpy(&address,&found,sizeof(address));
        if(!found||address-(WitU64)module!=rva)return 13;
    }
    FARPROC symbol=GetProcAddress(module,"LibraryAdd");int(*add)(int,int);memcpy(&add,&symbol,sizeof(add));if(add(731,11)!=742)return 14;
    symbol=GetProcAddress(module,(LPCSTR)(uintptr_t)12);int(*ordinal)(void);memcpy(&ordinal,&symbol,sizeof(ordinal));if(ordinal()!=731)return 15;
    FreeLibrary(module);
    unsigned raw=0;wit_pe_file_range(&plan,plan.ExportRva,plan.ExportSize,&raw);
    unsigned functions=0,namesRaw=0,ordinals=0;
    wit_pe_file_range(&plan,plan.ExportFunctionsRva,plan.ExportCount*4,&functions);
    wit_pe_file_range(&plan,plan.ExportNamesRva,plan.ExportNames*4,&namesRaw);
    wit_pe_file_range(&plan,plan.ExportOrdinalsRva,plan.ExportNames*2,&ordinals);
    const unsigned exportRva=plan.ExportRva,exportCount=plan.ExportCount;
    for(unsigned mode=0;mode<9;++mode){
        memcpy(changed,file,(size_t)length);
        if(mode==0)put32(changed+raw,1);
        if(mode==1)put32(changed+raw+20,0xffffffffU);
        if(mode==2)put32(changed+raw+16,0xffffffffU);
        if(mode==3)put32(changed+raw+28,0xffffffffU);
        if(mode==4){changed[ordinals]=(unsigned char)exportCount;changed[ordinals+1]=0;}
        if(mode==5)put32(changed+namesRaw,0xffffffffU);
        if(mode==6)put32(changed+namesRaw+4,u32(changed+namesRaw));
        if(mode==7)put32(changed+functions,exportRva);
        if(mode==8)put32(changed+functions,0xffffffffU);
        if(!check(changed,(unsigned)length,mode==1?WitPeTooLarge:mode==7?WitPeUnsupportedImage:WitPeInvalidImage))return 16;
    }
    // Ordinal-only DLLs do not need name/ordinal arrays. Lookups must still
    // distinguish absent names from valid NONAME exports and EAT holes.
    memcpy(changed,file,(size_t)length);put32(changed+raw+24,0);put32(changed+raw+32,0);put32(changed+raw+36,0);
    if(!check(changed,(unsigned)length,WitPeOk))return 18;
    unsigned found=99;
    if(wit_pe_export_find(changed,&plan,"LibraryAdd",10,0,&found)!=WitPeOk||found)return 19;
    if(wit_pe_export_find(changed,&plan,0,0,12,&found)!=WitPeOk||!found)return 20;
    if(wit_pe_export_find(changed,&plan,0,0,11,&found)!=WitPeOk||found)return 21;
    const unsigned optional=u32(file+60)+24;
    // Unsupported loader dependencies remain explicit, not ignored directories.
    for(unsigned index=0;index<3;++index){
        memcpy(changed,file,(size_t)length);const unsigned directory=index==0?1:index==1?9:13;
        put32(changed+optional+112+directory*8,exportRva);put32(changed+optional+116+directory*8,40);
        if(!check(changed,(unsigned)length,WitPeUnsupportedImage))return 22;
    }
    // Executables cannot enter the DLL profile; DLLs without exports can load,
    // but every symbol query must report missing without touching invalid arrays.
    memcpy(changed,file,(size_t)length);changed[u32(file+60)+23]&=~0x20U;
    if(!check(changed,(unsigned)length,WitPeUnsupportedImage))return 23;
    memcpy(changed,file,(size_t)length);put32(changed+optional+112,0);put32(changed+optional+116,0);
    if(!check(changed,(unsigned)length,WitPeOk))return 24;
    if(wit_pe_export_find(changed,&plan,"LibraryAdd",10,0,&found)!=WitPeOk||found)return 25;
    for(unsigned bytes=0;bytes<(unsigned)length;++bytes)if(!check(file,bytes,-2))return 17;
    free(file);free(changed);VirtualFree(area,0,MEM_RELEASE);
    printf("PASS: %u guarded DLL export cases plus Windows name/ordinal/alias/data/relocation comparisons\n",tests);return 0;
}
