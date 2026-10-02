#pragma warning(push)
#pragma warning(disable:4100)
#include "pal.h"
#pragma warning(pop)
#include "host_path_codec.witos.h"
// The assignments below retain genuine std::wstring allocation/EH dependencies.
// This object is not included in allocation-free leaf probes.
bool pal::getcwd(pal::string_t* output)
{
    if(!output){SetLastError(ERROR_INVALID_PARAMETER);return false;}
    const DWORD previous=GetLastError();WitNativePath path;
    const auto status=wit_native_cwd_get(&path);
    if(status!=WIT_STATUS_OK){SetLastError(WitHostPath::error(status));return false;}
    pal::char_t text[WIT_PATH_BUFFER];const size_t count=WitHostPath::wide(path,text);
    output->assign(text,count);SetLastError(previous);return true;
}
bool pal::fullpath(pal::string_t* value,bool skip_error_logging)
{
    (void)skip_error_logging;
    if(!value){SetLastError(ERROR_INVALID_PARAMETER);return false;}
    const DWORD previous=GetLastError();WitNativePath resolved;
    if(!WitHostPath::resolve(*value,resolved,true))return false;
    pal::char_t text[WIT_PATH_BUFFER];const size_t count=WitHostPath::wide(resolved,text);
    value->assign(text,count);SetLastError(previous);return true;
}
bool pal::realpath(pal::string_t* value,bool skip_error_logging)
{return fullpath(value,skip_error_logging);} // Immutable boot namespace has no symlinks.
bool pal::is_path_rooted(const pal::string_t& path)
{return !path.empty()&&(path.front()=='/'||path.front()==92);}
bool pal::is_path_fully_qualified(const pal::string_t& path)
{return is_path_rooted(path);}
