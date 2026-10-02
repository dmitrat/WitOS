#pragma warning(push)
#pragma warning(disable:4100)
#include "pal.h"
#pragma warning(pop)
#include <exception>
// Actual hosting signatures, with genuine std::string/vector/callback and EH
// dependencies. The Windows-shaped imports bind to the real WitOS adapter in
// the guest; hosted contract tests use the Windows reference implementation.
bool pal::getenv(const char_t* name,string_t* output)
{
    if(!output){SetLastError(ERROR_INVALID_PARAMETER);return false;}
    output->clear();
    const auto count=GetEnvironmentVariableW(name,nullptr,0);
    if(!count)return false;
    std::vector<char_t> buffer(count);
    if(!GetEnvironmentVariableW(name,buffer.data(),count))return false;
    output->assign(buffer.data());return true;
}
void pal::enumerate_environment_variables(const std::function<void(const char_t*,const char_t*)> callback)
{
    struct Block {
        wchar_t* Address;
        ~Block(){if(Address){DWORD saved=GetLastError();if(!FreeEnvironmentStringsW(Address))std::terminate();SetLastError(saved);}}
    } block{GetEnvironmentStringsW()};
    if(!block.Address)return;
    const wchar_t* current=block.Address;
    while(*current){
        const wchar_t* end=current;const wchar_t* equal=nullptr;
        while(*end){if(*end==L'='&&!equal)equal=end;++end;}
        if(equal&&equal!=current){string_t name(current,equal-current);callback(name.c_str(),equal+1);}
        current=end+1;
    }
}
