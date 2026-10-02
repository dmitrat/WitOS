#ifndef WITOS_HOST_PATH_CODEC_H
#define WITOS_HOST_PATH_CODEC_H
extern "C" {
#include "path.h"
}
namespace WitHostPath {
inline DWORD error(WitU64 status)
{
    switch(status){
    case WIT_STATUS_NOT_FOUND:return ERROR_FILE_NOT_FOUND;
    case WIT_STATUS_INITIALIZATION_FAILED:return ERROR_DLL_INIT_FAILED;
    case WIT_STATUS_NO_MEMORY:return ERROR_NOT_ENOUGH_MEMORY;
    case WIT_STATUS_BAD_HANDLE:return ERROR_INVALID_HANDLE;
    case WIT_STATUS_BAD_ADDRESS:return ERROR_NOACCESS;
    case WIT_STATUS_WRONG_TYPE:return ERROR_DIRECTORY;
    case WIT_STATUS_TOO_LARGE:return ERROR_FILENAME_EXCED_RANGE;
    case WIT_STATUS_UNSUPPORTED:return ERROR_NOT_SUPPORTED;
    case WIT_STATUS_BUSY:return ERROR_BUSY;
    default:return ERROR_INVALID_PARAMETER;
    }
}
inline bool utf8(const pal::string_t& path,char* output,WitU32& bytes)
{
    static_assert(sizeof(pal::char_t)==2,"Selected private host ABI uses UTF-16");
    const auto* text=path.data();const auto count=path.size();bytes=0;
    if(!count){SetLastError(ERROR_INVALID_NAME);return false;}
    if(count>WIT_PATH_INPUT_MAX){SetLastError(ERROR_FILENAME_EXCED_RANGE);return false;}
    for(size_t i=0;i<count;++i){
        unsigned code=(unsigned short)text[i];
        if(code>=0xD800&&code<=0xDBFF){
            if(i+1==count||text[i+1]<0xDC00||text[i+1]>0xDFFF){SetLastError(ERROR_NO_UNICODE_TRANSLATION);return false;}
            code=0x10000+((code-0xD800)<<10)+((unsigned short)text[++i]-0xDC00);
        }else if(code>=0xDC00&&code<=0xDFFF){SetLastError(ERROR_NO_UNICODE_TRANSLATION);return false;}
        const unsigned width=code<0x80?1:code<0x800?2:code<0x10000?3:4;
        if(width>WIT_PATH_INPUT_MAX-bytes){SetLastError(ERROR_FILENAME_EXCED_RANGE);return false;}
        if(width==1)output[bytes++]=(char)code;
        else {
            output[bytes++]=(char)((width==2?0xC0:width==3?0xE0:0xF0)|(code>>(6*(width-1))));
            for(unsigned remaining=width-1;remaining;--remaining)output[bytes++]=(char)(0x80|((code>>(6*(remaining-1)))&63));
        }
    }
    return true;
}
inline bool resolve(const pal::string_t& path,WitNativePath& output,bool mustExist)
{
    char input[WIT_PATH_INPUT_MAX];WitU32 bytes=0;
    if(!utf8(path,input,bytes))return false;
    const auto status=mustExist?wit_native_path_full(input,bytes,&output):wit_native_path_resolve(input,bytes,&output);
    if(status!=WIT_STATUS_OK){SetLastError(error(status));return false;}
    return true;
}
// Input is the already-validated canonical UTF-8 result of the native resolver.
inline size_t wide(const WitNativePath& path,pal::char_t* output)
{
    size_t count=0;
    for(WitU32 i=0;i<path.Bytes;){
        unsigned code=(unsigned char)path.Text[i++];
        if(code>=128){unsigned extra=code<0xE0?1:code<0xF0?2:3;code&=extra==1?31:extra==2?15:7;
            while(extra--)code=(code<<6)|((unsigned char)path.Text[i++]&63);}
        if(code<0x10000)output[count++]=(pal::char_t)code;
        else {code-=0x10000;output[count++]=(pal::char_t)(0xD800+(code>>10));output[count++]=(pal::char_t)(0xDC00+(code&1023));}
    }
    return count;
}
}
#endif
