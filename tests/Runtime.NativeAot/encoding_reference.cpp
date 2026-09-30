#include "native_encoding.witos.h"
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <errno.h>
#include <initializer_list>
static unsigned cases;
static UINT selectedPage=CP_UTF8;
static bool mb(const char* input, int size, DWORD flags, int capacity)
{
    wchar_t ours[64], system[64]; memset(ours,0x5A,sizeof(ours)); memset(system,0x5A,sizeof(system));
    SetLastError(0x1234); errno=71;
    int a=wit_native_multibyte_to_wide(selectedPage,flags,input,size,ours,capacity); DWORD ae=GetLastError();
    if(errno!=71)return false;
    SetLastError(0x1234);
    int b=MultiByteToWideChar(selectedPage,flags,input,size,system,capacity); DWORD be=GetLastError();
    ++cases;
    if(a!=b || ae!=be || (a && capacity && memcmp(ours,system,(size_t)a*2))) {
        printf("FAIL MB size=%d flags=%lu cap=%d ours=%d/%lu Windows=%d/%lu bytes:",size,flags,capacity,a,ae,b,be);
        for(int i=0;i<(size<0?(int)strlen(input)+1:size);++i)printf(" %02x",(unsigned char)input[i]);puts(""); return false;
    }
    for(int i=capacity;i<64;++i)if(ours[i]!=0x5A5A)return false;
    if(!a)for(auto c:ours)if(c!=0x5A5A)return false;
    return true;
}
static bool wc(const wchar_t* input, int size, DWORD flags, int capacity)
{
    char ours[128], system[128]; memset(ours,0x5A,sizeof(ours)); memset(system,0x5A,sizeof(system));
    SetLastError(0x1234); errno=71;
    int a=wit_native_wide_to_multibyte(selectedPage,flags,input,size,ours,capacity,nullptr,nullptr); DWORD ae=GetLastError();
    if(errno!=71)return false;
    SetLastError(0x1234);
    int b=WideCharToMultiByte(selectedPage,flags,input,size,system,capacity,nullptr,nullptr); DWORD be=GetLastError();
    ++cases;
    if(a!=b || ae!=be || (a && capacity && memcmp(ours,system,(size_t)a))) {
        printf("FAIL WC size=%d flags=%lu cap=%d ours=%d/%lu Windows=%d/%lu\n",size,flags,capacity,a,ae,b,be);return false;
    }
    for(int i=capacity;i<128;++i)if(ours[i]!=0x5A)return false;
    if(!a)for(auto c:ours)if(c!=0x5A)return false;
    return true;
}
int main()
{
    if(GetACP()!=CP_UTF8){puts("UTF-8 process manifest not effective");return 1;}
    // Every Unicode scalar and isolated UTF-16 surrogate, strict and replacement,
    // both sizing and writing; input UTF-8 comes from Windows, not our encoder.
    for(uint32_t point=0;point<=0x10FFFF;++point) {
        wchar_t wide[2];int units=1;
        if(point<0x10000)wide[0]=(wchar_t)point;
        else {units=2;wide[0]=(wchar_t)(0xD800+((point-0x10000)>>10));wide[1]=(wchar_t)(0xDC00+((point-0x10000)&1023));}
        if(!wc(wide,units,0,0)||!wc(wide,units,0,128)||!wc(wide,units,WC_ERR_INVALID_CHARS,0)||!wc(wide,units,WC_ERR_INVALID_CHARS,128))return 1;
        char utf8[4];int bytes=WideCharToMultiByte(CP_UTF8,0,wide,units,utf8,4,nullptr,nullptr);
        if(!mb(utf8,bytes,0,0)||!mb(utf8,bytes,0,64)||!mb(utf8,bytes,MB_ERR_INVALID_CHARS,0)||!mb(utf8,bytes,MB_ERR_INVALID_CHARS,64))return 1;
    }
    for(unsigned v=0;v<65536;++v){char bytes[2]={(char)(v>>8),(char)v};
        if(!mb(bytes,2,0,64)||!mb(bytes,2,MB_ERR_INVALID_CHARS,64))return 1;}
    uint64_t seed=0x619279AB8721ULL;
    for(unsigned i=0;i<100000;++i){
        char bytes[16];wchar_t wide[16];
        for(int j=0;j<16;++j){seed=seed*6364136223846793005ULL+1442695040888963407ULL;bytes[j]=(char)(seed>>32);wide[j]=(wchar_t)(seed>>40);}
        int n=1+(i%16);
        if(!mb(bytes,n,0,64)||!mb(bytes,n,MB_ERR_INVALID_CHARS,64)||!wc(wide,n,0,128)||!wc(wide,n,WC_ERR_INVALID_CHARS,128))return 1;
    }
    const char sample[]="A\0\xE2\x82\xAC\xF0\x9F\x98\x80";
    const wchar_t wide[]={L'A',0,0x20AC,0xD83D,0xDE00,0};
    for(int cap=0;cap<20;++cap)if(!mb(sample,10,0,cap)||!mb(sample,-1,0,cap)||!wc(wide,6,0,cap)||!wc(wide,-1,0,cap))return 1;
    for(UINT page: {UINT(CP_ACP),UINT(CP_THREAD_ACP),UINT(CP_UTF8)}) {
        selectedPage=page;
        for(DWORD flags=0;flags<2048;++flags)if(!mb(sample,10,flags,64)||!wc(wide,6,flags,128))return 1;
        for(DWORD flags: {DWORD(0),DWORD(MB_PRECOMPOSED),DWORD(MB_ERR_INVALID_CHARS),DWORD(MB_PRECOMPOSED|MB_ERR_INVALID_CHARS)})
            for(int n: {-1,10})for(int cap: {0,1,64})if(!mb(sample,n,flags,cap))return 1;
        for(DWORD flags: {DWORD(0),DWORD(WC_ERR_INVALID_CHARS)})
            for(int n: {-1,6})for(int cap: {0,1,128})if(!wc(wide,n,flags,cap))return 1;
    }
    printf("PASS: %u UTF conversion differential cases\n",cases);return 0;
}
