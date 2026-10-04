#ifndef WITOS_NATIVE_ENCODING_H
#define WITOS_NATIVE_ENCODING_H
#include <windows.h>
extern "C" int WINAPI wit_native_multibyte_to_wide(UINT, DWORD, LPCCH, int, LPWSTR, int);
extern "C" int WINAPI wit_native_wide_to_multibyte(UINT, DWORD, LPCWCH, int, LPSTR, int, LPCCH, LPBOOL);
#endif
