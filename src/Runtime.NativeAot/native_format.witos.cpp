#include <stdio.h>
#include <stdarg.h>
#include <stdint.h>
#include <errno.h>
#include <limits.h>
#include "format_fixed.witos.h"

extern "C" unsigned wit_native_fp_rounding(void);
static_assert(sizeof(long)==4 && sizeof(size_t)==8);

namespace {
constexpr unsigned MaxFormat = 4096, MaxWidth = 1024, MaxString = 4096;
struct Output {
    char* bytes; size_t capacity; size_t count = 0;
    void put(char c) { if (count + 1 < capacity) bytes[count] = c; ++count; }
    void repeat(char c, unsigned n) { while (n--) put(c); }
    void finish() { bytes[count < capacity ? count : capacity - 1] = 0; }
};
enum class Length { Normal, Byte, Short, Long, Wide };
struct Spec { bool left=false, plus=false, space=false, zero=false, alternate=false; unsigned width=0; int precision=-1; Length length=Length::Normal; char type=0; };
bool number(const char*& text, unsigned& result) {
    result=0;
    while (*text >= '0' && *text <= '9') { if (result > MaxWidth/10) return false; result=result*10+unsigned(*text++-'0'); if(result>MaxWidth)return false; }
    return true;
}
bool parse(const char*& text, Spec& s, va_list* args) {
    for (;;) {
        if(*text=='-')s.left=true; else if(*text=='+')s.plus=true; else if(*text==' ')s.space=true;
        else if(*text=='0')s.zero=true; else if(*text=='#')s.alternate=true; else break;
        ++text;
    }
    if (*text=='*') { ++text; if(args) { int n=va_arg(*args,int); if(n==INT_MIN)return false; if(n<0){s.left=true;n=-n;} if(unsigned(n)>MaxWidth)return false;s.width=unsigned(n); } }
    else if(!number(text,s.width))return false;
    if(*text=='.') {
        ++text;
        if(*text=='*') { ++text; if(args) { s.precision=va_arg(*args,int); if(s.precision>int(MaxWidth))return false; if(s.precision<0)s.precision=-1; } }
        else { unsigned n; if(!number(text,n))return false;s.precision=int(n); }
    }
    if(*text=='h') {++text;s.length=Length::Short;if(*text=='h'){++text;s.length=Length::Byte;}}
    else if(*text=='l') {++text;s.length=Length::Long;if(*text=='l'){++text;s.length=Length::Wide;}}
    else if(*text=='z'||*text=='t'||*text=='j'){++text;s.length=Length::Wide;}
    else if(*text=='I') {++text;if(text[0]=='6'&&text[1]=='4'){text+=2;s.length=Length::Wide;} else if(text[0]=='3'&&text[1]=='2'){text+=2;s.length=Length::Normal;} else s.length=Length::Wide;}
    s.type=*text;
    if(!s.type)return false;
    ++text;
    if(s.type=='d'||s.type=='i'||s.type=='u'||s.type=='x'||s.type=='X'||s.type=='o')return true;
    if(s.type=='f'||s.type=='F')return (s.length==Length::Normal||s.length==Length::Long)&&s.precision<=int(WIT_FORMAT_FIXED_PRECISION);
    return (s.type=='s'||s.type=='c'||s.type=='%'||s.type=='p')&&s.length==Length::Normal;
}
void emit(Output& out,const Spec& s,const char* text,unsigned length,unsigned leadingZeroes=0,bool numeric=false) {
    char prefix[3]; unsigned prefixLength=0;
    if(numeric) {
        if(length && *text=='-'){prefix[prefixLength++]='-';++text;--length;}
        else if(s.type=='d'||s.type=='i'||s.type=='f'||s.type=='F'){if(s.plus)prefix[prefixLength++]='+';else if(s.space)prefix[prefixLength++]=' ';}
        if(s.alternate&&(s.type=='x'||s.type=='X')&&length&&!(length==1&&*text=='0')) {prefix[prefixLength++]='0';prefix[prefixLength++]=s.type;}
    }
    const unsigned used=prefixLength+leadingZeroes+length;
    const unsigned padding=s.width>used?s.width-used:0;
    const bool zero=s.zero&&!s.left&&numeric&&((s.type=='f'||s.type=='F')||s.precision<0);
    if(!s.left&&!zero)out.repeat(' ',padding);
    for(unsigned i=0;i<prefixLength;++i)out.put(prefix[i]);
    if(zero)out.repeat('0',padding);
    out.repeat('0',leadingZeroes);
    for(unsigned i=0;i<length;++i)out.put(text[i]);
    if(s.left)out.repeat(' ',padding);
}
int format(Output& out,const char* text,va_list arguments,unsigned rounding) {
    va_list args; va_copy(args,arguments);
    while(*text) {
        if(*text!='%'){out.put(*text++);continue;}
        ++text; Spec s;
        if(!parse(text,s,&args)){va_end(args);return -1;}
        if(s.type=='%'){out.put('%');continue;}
        if(s.type=='s') {
            const char* value=va_arg(args,const char*);if(!value)value="(null)";
            unsigned length=0,limit=s.precision<0?MaxString:unsigned(s.precision);
            while(length<limit && value[length])++length;
            if(length==MaxString&&s.precision<0&&value[length]){va_end(args);return -1;}
            emit(out,s,value,length);continue;
        }
        if(s.type=='c'){char value=char(va_arg(args,int));emit(out,s,&value,1);continue;}
        if(s.type=='f'||s.type=='F') {
            char value[WIT_FORMAT_FIXED_CAPACITY];
            const int n=wit_format_fixed(value,va_arg(args,double),s.precision<0?6:unsigned(s.precision),rounding);
            if(n<0){va_end(args);return -1;}
            unsigned length=unsigned(n);
            if(s.alternate&&s.precision==0&&value[length-1]>='0'&&value[length-1]<='9')value[length++]='.';
            if(s.type=='F')for(unsigned i=0;i<length;++i)if(value[i]>='a'&&value[i]<='z')value[i]=char(value[i]-'a'+'A');
            Spec numeric=s;
            const unsigned start=value[0]=='-'?1U:0U;
            if(value[start]<'0'||value[start]>'9')numeric.zero=false;
            emit(out,numeric,value,length,0,true);continue;
        }
        const bool signedValue=s.type=='d'||s.type=='i';
        uint64_t value; bool negative=false;
        if(s.type=='p')value=(uintptr_t)va_arg(args,void*);
        else if(signedValue){
            int64_t v=s.length==Length::Wide?va_arg(args,int64_t):s.length==Length::Long?va_arg(args,long):va_arg(args,int);
            if(s.length==Length::Byte)v=(signed char)v;else if(s.length==Length::Short)v=(short)v;
            negative=v<0;value=negative?0-uint64_t(v):uint64_t(v);
        }else{
            value=s.length==Length::Wide?va_arg(args,uint64_t):s.length==Length::Long?va_arg(args,unsigned long):va_arg(args,unsigned int);
            if(s.length==Length::Byte)value=(unsigned char)value;else if(s.length==Length::Short)value=(unsigned short)value;
        }
        const unsigned base=(s.type=='x'||s.type=='X'||s.type=='p')?16:s.type=='o'?8:10;
        const char* alphabet=(s.type=='X'||s.type=='p')?"0123456789ABCDEF":"0123456789abcdef";
        char reverse[65],ordered[66];unsigned digits=0;
        if(value||s.precision!=0||s.type=='p')do{reverse[digits++]=alphabet[value%base];value/=base;}while(value);
        unsigned length=0;if(negative)ordered[length++]='-';
        for(unsigned i=digits;i>0;--i)ordered[length++]=reverse[i-1];
        unsigned zeros=s.precision>int(digits)?unsigned(s.precision)-digits:0;
        if(s.type=='p'&&digits<16)zeros=16-digits;
        if(s.alternate&&s.type=='o'&&!zeros&&(!digits||ordered[0]!='0'))zeros=1;
        emit(out,s,ordered,length,zeros,true);
    }
    va_end(args);return out.count>INT_MAX?-1:int(out.count);
}
}
extern "C" int wit_native_vsnprintf_s(unsigned __int64 options,char* buffer,size_t bufferCount,size_t maxCount,const char* text,_locale_t locale,va_list args)
{
    // Only the actual narrow C-locale startup contract is supported. Never
    // silently consume wide strings, %n, positional, scientific/hex FP, or
    // unbounded precision as if their formatting had succeeded.
    if(!text){errno=EINVAL;return -1;}
    if(maxCount==0&&!buffer&&!bufferCount)return 0;
    if(!buffer||!bufferCount){errno=EINVAL;return -1;}
    auto fail=[&](int error){buffer[0]=0;errno=error;return -1;};
    if(locale||(options&~(_CRT_INTERNAL_PRINTF_LEGACY_WIDE_SPECIFIERS|_CRT_INTERNAL_PRINTF_STANDARD_ROUNDING)))return fail(EINVAL);
    unsigned size=0;while(size<MaxFormat&&text[size])++size;
    if(size==MaxFormat)return fail(EINVAL);
    const char* scan=text;
    while(*scan){if(*scan++=='%'){Spec s;if(!parse(scan,s,nullptr))return fail(EINVAL);}}
    const size_t capacity=bufferCount>maxCount?maxCount+1:bufferCount;
    Output out{buffer,capacity};
    const int n=format(out,text,args,(options&_CRT_INTERNAL_PRINTF_STANDARD_ROUNDING)?wit_native_fp_rounding():4);
    if(n<0)return fail(EINVAL);
    out.finish();
    if(out.count>=capacity){if(bufferCount>maxCount||maxCount==_TRUNCATE)return -1;return fail(ERANGE);}
    return n;
}
#ifndef WITOS_FORMAT_REFERENCE
extern "C" int __cdecl __stdio_common_vsnprintf_s(unsigned __int64 options,char* buffer,size_t bufferCount,size_t maxCount,const char* text,_locale_t locale,va_list args)
{
    return wit_native_vsnprintf_s(options,buffer,bufferCount,maxCount,text,locale,args);
}
#endif
