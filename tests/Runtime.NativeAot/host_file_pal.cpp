#pragma warning(push)
#pragma warning(disable: 4100) // Upstream inline mkdir intentionally ignores mode on Windows.
#include "pal.h"
#pragma warning(pop)
#include <cstdio>
extern "C" {
#include "file_view.h"
#include "path.h"
void file_view_model_length(WitU64);
const char* file_view_model_path(void);
WitU32 file_view_model_path_bytes(void);
unsigned file_view_model_handles(void);
unsigned file_view_model_allocations(void);
unsigned host_environment_live();
unsigned host_environment_allocated();
unsigned host_environment_released();
void host_library_fixture(const char*);
unsigned host_library_references(void);
void host_library_fail_path_allocation(int);
}
int main(int argc,char** argv)
{
    if(argc!=2)return 40;host_library_fixture(argv[1]);
    size_t length=99;SetLastError(0x1234);
    auto read=pal::mmap_read(L"/file",&length);
    if(!read||length!=70001||GetLastError()!=0x1234||file_view_model_handles()||file_view_model_allocations()!=1)return 1;
    for(size_t i=0;i<length;++i)if(((const unsigned char*)read)[i]!=(unsigned char)(i%251))return 2;
    if(pal::munmap((void*)read,length+1)||GetLastError()!=ERROR_INVALID_PARAMETER||!file_view_model_allocations())return 3;
    SetLastError(0x4321);if(!pal::munmap((void*)read,length)||GetLastError()!=0x4321||file_view_model_allocations())return 4;
    if(pal::munmap((void*)read,length)||GetLastError()!=ERROR_INVALID_HANDLE)return 5;
    auto copy=pal::mmap_copy_on_write(L"/file",nullptr);if(!copy)return 6;
    ((unsigned char*)copy)[0]=0xA5;
    if(!pal::munmap(copy,70001))return 7;
    length=99;auto relative=pal::mmap_read(L"./file",&length);if(!relative||length!=70001||!pal::munmap((void*)relative,length))return 8;
    length=99;if(pal::mmap_read(L"/missing",&length)||length!=99||GetLastError()!=ERROR_FILE_NOT_FOUND||file_view_model_handles())return 9;
    file_view_model_length(0);if(pal::mmap_read(L"/file",&length)||length||GetLastError()!=ERROR_FILE_INVALID||file_view_model_handles())return 10;
    file_view_model_length(70001);
    std::wstring unicode=L"/";unicode.push_back((wchar_t)0x03BB);unicode.push_back((wchar_t)0xD83D);unicode.push_back((wchar_t)0xDE42);
    if(!pal::file_exists(unicode))return 11;
    const unsigned char expected[]={0xCE,0xBB,0xF0,0x9F,0x99,0x82};
    if(file_view_model_path_bytes()!=sizeof(expected)||memcmp(file_view_model_path(),expected,sizeof(expected)))return 12;
    std::wstring bad=L"/";bad.push_back((wchar_t)0xD800);
    if(pal::file_exists(bad)||GetLastError()!=ERROR_NO_UNICODE_TRANSLATION)return 13;
    if(!pal::file_exists(L"/")||file_view_model_path_bytes())return 14;
    if(pal::file_exists(L"/missing")||GetLastError()!=ERROR_FILE_NOT_FOUND)return 15;
    std::wstring tooLong=L"/"+std::wstring(1025,L'a');
    if(pal::file_exists(tooLong)||GetLastError()!=ERROR_FILENAME_EXCED_RANGE)return 16;
    std::wstring cwd;
    if(!pal::getcwd(&cwd)||cwd!=L"/"||wit_native_cwd_set("/dir/sub",8)!=WIT_STATUS_OK)return 17;
    if(!pal::getcwd(&cwd)||cwd!=L"/dir/sub")return 18;
    std::wstring full=L"../../file";SetLastError(0x1234);
    if(!pal::fullpath(&full)||full!=L"/file"||GetLastError()!=0x1234)return 19;
    full=L"../../missing";const auto missing=full;
    if(pal::realpath(&full)||full!=missing||GetLastError()!=ERROR_FILE_NOT_FOUND)return 20;
    if(wit_native_cwd_set("../../file",10)!=WIT_STATUS_WRONG_TYPE||!pal::getcwd(&cwd)||cwd!=L"/dir/sub")return 21;
    full=L"../../../dir//./sub/..";
    if(!pal::realpath(&full)||full!=L"/dir")return 22;
    if(!pal::is_path_rooted(L"/file")||pal::is_path_rooted(L"file")||pal::is_path_rooted(L"")||!pal::is_path_fully_qualified(L"/"))return 23;
    if(wit_native_cwd_set("/",1)!=WIT_STATUS_OK)return 24;
    std::vector<pal::string_t> entries={L"keep"};SetLastError(0x1234);
    pal::readdir(L"/",L"*.json",&entries);
    if(entries!=std::vector<pal::string_t>{L"keep",L"alpha.deps.json",L"beta.json"}||GetLastError()!=0x1234)return 25;
    entries.clear();pal::readdir_onlydirectories(L"/",&entries);
    if(entries!=std::vector<pal::string_t>{L"dir"})return 26;
    entries.clear();pal::readdir(L"/",L"?.txt",&entries);
    if(entries.size()!=1||entries[0]!=L"\u03bb.txt")return 27;
    entries={L"preserved"};pal::readdir(L"/missing",&entries);
    if(entries!=std::vector<pal::string_t>{L"preserved"}||GetLastError()!=ERROR_FILE_NOT_FOUND)return 28;
    pal::readdir(L"/file",&entries);
    if(entries!=std::vector<pal::string_t>{L"preserved"}||GetLastError()!=ERROR_DIRECTORY)return 29;
    entries.clear();pal::readdir_onlydirectories(L"/dir",L"s*",&entries);
    if(entries!=std::vector<pal::string_t>{L"sub"})return 30;
    if(!SetEnvironmentVariableW(L"WITOS_HOST_ENV_TEST",L"value=with=equals")||!SetEnvironmentVariableW(L"WITOS_HOST_ENV_EMPTY",L""))return 31;
    std::wstring environment=L"old";
    if(!pal::getenv(L"WITOS_HOST_ENV_TEST",&environment)||environment!=L"value=with=equals")return 32;
    environment=L"old";
    if(pal::getenv(L"WITOS_HOST_ENV_MISSING",&environment)||!environment.empty()||GetLastError()!=ERROR_ENVVAR_NOT_FOUND)return 33;
    environment=L"old";if(pal::getenv(L"WITOS_HOST_ENV_EMPTY",&environment)||!environment.empty())return 34;
    unsigned foundEnvironment=0,foundEmpty=0;
    pal::enumerate_environment_variables([&](const wchar_t* name,const wchar_t* value){
        if(std::wstring(name)==L"WITOS_HOST_ENV_TEST"){if(std::wstring(value)!=L"value=with=equals")throw 35;++foundEnvironment;}
        if(std::wstring(name)==L"WITOS_HOST_ENV_EMPTY"){if(*value)throw 39;++foundEmpty;}
    });
    if(foundEnvironment!=1||foundEmpty!=1||host_environment_live())return 36;
    bool threw=false;try{pal::enumerate_environment_variables([](const wchar_t*,const wchar_t*){throw 37;});}catch(int value){threw=value==37;}
    if(!threw||host_environment_live()||host_environment_allocated()!=2||host_environment_released()!=2)return 38;
    SetEnvironmentVariableW(L"WITOS_HOST_ENV_TEST",nullptr);SetEnvironmentVariableW(L"WITOS_HOST_ENV_EMPTY",nullptr);
    const pal::string_t libraryPath=L"/native/./lib.dll";
    pal::dll_t library=nullptr;SetLastError(0x2468);
    if(!pal::load_library(&libraryPath,&library)||!library||GetLastError()!=0x2468||host_library_references()!=1)return 41;
    auto symbol=pal::get_symbol(library,"LibraryAdd");
    if(!symbol||reinterpret_cast<int(*)(int,int)>(symbol)(731,11)!=742||GetLastError()!=0x2468)return 42;
    pal::dll_t second=nullptr;
    if(!pal::load_library(&libraryPath,&second)||second!=library||host_library_references()!=2)return 43;
    if(pal::get_symbol(library,"missing")||GetLastError()!=ERROR_PROC_NOT_FOUND)return 44;
    if(pal::get_symbol(library,nullptr)||GetLastError()!=ERROR_INVALID_PARAMETER)return 45;
    const std::string longName(256,'a');
    if(pal::get_symbol(library,longName.c_str())||GetLastError()!=ERROR_INVALID_PARAMETER)return 46;
    pal::string_t modulePath=L"keep";pal::dll_t discovered=reinterpret_cast<pal::dll_t>(99);
    SetLastError(0x6789);
    if(!pal::get_module_path(library,&modulePath)||modulePath!=L"/native/lib.dll"||GetLastError()!=0x6789)return 51;
    if(!pal::get_loaded_library(L"lib.dll","LibraryAdd",&discovered,&modulePath)||discovered!=library||host_library_references()!=3||modulePath!=L"/native/lib.dll"||GetLastError()!=0x6789)return 52;
    pal::unload_library(discovered);discovered=reinterpret_cast<pal::dll_t>(99);modulePath=L"keep";
    if(pal::get_loaded_library(L"lib.dll","missing",&discovered,&modulePath)||discovered!=reinterpret_cast<pal::dll_t>(99)||modulePath!=L"keep"||host_library_references()!=2||GetLastError()!=ERROR_PROC_NOT_FOUND)return 53;
    if(!pal::get_loaded_library(L"/native/./lib.dll","LibraryAdd",&discovered,&modulePath)||discovered!=library||host_library_references()!=3)return 54;
    pal::unload_library(discovered);discovered=reinterpret_cast<pal::dll_t>(99);modulePath=L"keep";
    const pal::string_t maximumName=pal::string_t(WIT_PATH_INPUT_MAX-14,L'/')+L"native/lib.dll";
    if(!pal::get_loaded_library(maximumName.c_str(),"LibraryAdd",&discovered,&modulePath)||discovered!=library||host_library_references()!=3)return 58;
    pal::unload_library(discovered);discovered=reinterpret_cast<pal::dll_t>(99);modulePath=L"keep";
    const pal::string_t oversizedName=L"/"+maximumName;
    if(pal::get_loaded_library(oversizedName.c_str(),"LibraryAdd",&discovered,&modulePath)||host_library_references()!=2||GetLastError()!=ERROR_FILENAME_EXCED_RANGE||modulePath!=L"keep")return 59;
    host_library_fail_path_allocation(1);bool allocationThrew=false;
    try{(void)pal::get_loaded_library(L"lib.dll","LibraryAdd",&discovered,&modulePath);}catch(const std::bad_alloc&){allocationThrew=true;}
    if(!allocationThrew||host_library_references()!=2||discovered!=reinterpret_cast<pal::dll_t>(99)||modulePath!=L"keep")return 55;
    SetLastError(0x3579);pal::unload_library(second);
    if(host_library_references()!=1||GetLastError()!=0x3579||reinterpret_cast<int(*)(int,int)>(symbol)(1,2)!=3)return 47;
    pal::unload_library(library);
    if(host_library_references()||GetLastError()!=0x3579||pal::get_symbol(library,"LibraryAdd")||GetLastError()!=ERROR_INVALID_HANDLE)return 48;
    modulePath=L"keep";discovered=reinterpret_cast<pal::dll_t>(99);
    if(pal::get_module_path(library,&modulePath)||modulePath!=L"keep"||GetLastError()!=ERROR_INVALID_HANDLE)return 56;
    if(pal::get_loaded_library(L"lib.dll","LibraryAdd",&discovered,&modulePath)||host_library_references()||modulePath!=L"keep"||discovered!=reinterpret_cast<pal::dll_t>(99))return 57;
    const pal::string_t absent=L"/missing";
    library=reinterpret_cast<pal::dll_t>(99);
    if(pal::load_library(&absent,&library)||library||GetLastError()!=ERROR_FILE_NOT_FOUND||host_library_references())return 49;
    library=reinterpret_cast<pal::dll_t>(99);
    if(pal::load_library(&bad,&library)||library||GetLastError()!=ERROR_NO_UNICODE_TRANSLATION||host_library_references())return 50;
    puts("PASS: actual corehost module discovery, retained references, transactional paths and bad_alloc cleanup (HOSTED reference)");
    puts("PASS: actual corehost library signatures, real Windows DLL invocation, reference and error contracts (HOSTED transport reference)");
    puts("PASS: actual corehost environment signatures, empty/missing values and callback exception propagation (WINDOWS reference)");
    puts("PASS: actual corehost readdir signatures, filtering and append/error contracts (HOSTED syscall model only)");
    puts("PASS: actual corehost path/cwd signatures and transactional resolution (HOSTED syscall model only)");
    puts("PASS: actual corehost PAL file signatures, real std::wstring, native view backend and UTF-16/UTF-8 contract (HOSTED syscall model only)");return 0;
}
