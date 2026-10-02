#pragma warning(push)
#pragma warning(disable:4100)
#include "pal.h"
#pragma warning(pop)
#include "host_path_codec.witos.h"
extern "C" {
#include "directory.h"
}
namespace {
void enumerate(const pal::string_t& path,const pal::string_t& pattern,bool directories,std::vector<pal::string_t>* output)
{
    if(!output){SetLastError(ERROR_INVALID_PARAMETER);return;}
    const DWORD previous=GetLastError();
    char input[WIT_PATH_INPUT_MAX],filter[WIT_PATH_INPUT_MAX];WitU32 inputBytes=0,filterBytes=0;
    if(!WitHostPath::utf8(path,input,inputBytes)||!WitHostPath::utf8(pattern,filter,filterBytes))return;
    WitNativeDirectory directory;WitU64 status=wit_native_directory_open(input,inputBytes,&directory);
    if(status!=WIT_STATUS_OK){SetLastError(WitHostPath::error(status));return;}
    for(;;){
        WitStorageInfo info;WitU32 found=0;
        status=wit_native_directory_next(&directory,filter,filterBytes,directories?WIT_DIRECTORY_ONLY:WIT_DIRECTORY_ALL,&info,&found);
        if(status!=WIT_STATUS_OK){SetLastError(WitHostPath::error(status));return;}
        if(!found)break;
        WitNativePath name;name.Bytes=info.NameBytes;
        for(WitU32 i=0;i<info.NameBytes;++i)name.Text[i]=(char)info.Name[i];
        pal::char_t wide[WIT_PATH_BUFFER];const size_t count=WitHostPath::wide(name,wide);
        output->emplace_back(wide,count); // Genuine vector/string allocation/EH stays required.
    }
    SetLastError(previous);
}
}
void pal::readdir(const string_t& path,const string_t& pattern,std::vector<string_t>* output){enumerate(path,pattern,false,output);}
void pal::readdir(const string_t& path,std::vector<string_t>* output){enumerate(path,_X("*"),false,output);}
void pal::readdir_onlydirectories(const string_t& path,const string_t& pattern,std::vector<string_t>* output){enumerate(path,pattern,true,output);}
void pal::readdir_onlydirectories(const string_t& path,std::vector<string_t>* output){enumerate(path,_X("*"),true,output);}
