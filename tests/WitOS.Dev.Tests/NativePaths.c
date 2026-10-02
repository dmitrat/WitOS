#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include "path.h"
static unsigned cases;
static unsigned char* guard;
static int test(const char* cwd,const char* input,WitU32 bytes,WitU64 status,const char* expected,unsigned directory)
{
    DWORD old;if(!VirtualProtect(guard,8192,PAGE_READWRITE,&old))return 0;
    char* source=(char*)guard+8192-bytes;if(bytes)memcpy(source,input,bytes);
    if(!VirtualProtect(guard,8192,PAGE_READONLY,&old))return 0;
    WitNativePath result;memset(&result,0xA5,sizeof(result));unsigned char before[sizeof(result)];memcpy(before,&result,sizeof(result));
    const WitU64 actual=wit_path_resolve(cwd,(WitU32)strlen(cwd),source,bytes,&result);++cases;
    if(actual!=status||(actual&&memcmp(&result,before,sizeof(result)))||(!actual&&(result.Bytes!=strlen(expected)||strcmp(result.Text,expected)||result.RequireDirectory!=directory))){
        printf("FAIL path case=%u status=%llu expected=%llu\n",cases,actual,status);return 0;
    }
    return 1;
}
int main(void)
{
    guard=(unsigned char*)VirtualAlloc(0,12288,MEM_RESERVE,PAGE_NOACCESS);if(!guard||!VirtualAlloc(guard,8192,MEM_COMMIT,PAGE_READWRITE))return 1;
    struct Case {const char* cwd;const char* path;const char* expected;unsigned directory;};
    const struct Case examples[]={
        {"/","/","/",1},{"/base/dir",".","/base/dir",1},{"/base/dir","..","/base",1},
        {"/base/dir","../../../../a","/a",0},{"/base/dir","a/b/../c","/base/dir/a/c",0},
        {"/base/dir","/x//./y/..","/x",1},{"/base/dir","a\\b\\..\\c","/base/dir/a/c",0},
        {"/","/a./b..","/a./b..",0},{"/","/a/../b/","/b",1},{"/","/a/..x","/a/..x",0},
        {"/","/\xCE\xBB/\xF0\x9F\x99\x82","/\xCE\xBB/\xF0\x9F\x99\x82",0}};
    for(unsigned i=0;i<sizeof(examples)/sizeof(examples[0]);++i)
        if(!test(examples[i].cwd,examples[i].path,(WitU32)strlen(examples[i].path),WIT_STATUS_OK,examples[i].expected,examples[i].directory))return 2;
    const unsigned char invalid[][8]={{0},{'a',0,'b'},{'C',':','/'},{0xC0,0xAF},{0xED,0xA0,0x80},{0xF4,0x90,0x80,0x80},{0xE2,0x82},{0x80},{'a',1,'b'}};
    const unsigned sizes[]={0,3,3,2,3,4,2,1,3};
    for(unsigned i=0;i<sizeof(sizes)/sizeof(sizes[0]);++i)
        if(!test("/",(const char*)invalid[i],sizes[i],WIT_STATUS_INVALID_ARGUMENT,0,0))return 3;
    char large[4097],expected[1026];memset(large,'a',sizeof(large));large[0]='/';large[1025]=0;memcpy(expected,large,1026);
    if(!test("/",large,1025,WIT_STATUS_OK,expected,0))return 4;
    large[1025]='a';large[1026]=0;if(!test("/",large,1026,WIT_STATUS_TOO_LARGE,0,0))return 5;
    // A full cwd followed by another component must not underflow capacity.
    if(!test(expected,"b",1,WIT_STATUS_TOO_LARGE,0,0))return 6;
    for(unsigned count=2;count<=600;++count){
        for(unsigned i=0;i<count;++i){large[i*3]='.';large[i*3+1]='.';large[i*3+2]='/';}
        large[count*3]='p';large[count*3+1]=0;
        if(!test("/a/b",large,count*3+1,WIT_STATUS_OK,"/p",0))return 7;
    }
    WitNativePath output;memset(&output,0xA5,sizeof(output));unsigned char before[sizeof(output)];memcpy(before,&output,sizeof(output));
    if(wit_path_resolve("/",1,(const char*)1,~0U,&output)!=WIT_STATUS_TOO_LARGE||memcmp(before,&output,sizeof(output)))return 8;
    VirtualFree(guard,0,MEM_RELEASE);printf("PASS: %u native path guard-boundary cases plus oversized-length atomic rejection\n",cases);return 0;
}
