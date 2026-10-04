#ifndef WITOS_FUNCTION_TABLES_GUEST_H
#define WITOS_FUNCTION_TABLES_GUEST_H
#include <windows.h>
#include "function_tables.witos.h"
extern "C" BOOLEAN WINAPI wit_coreclr_add_function_table(PRUNTIME_FUNCTION, DWORD, DWORD64);
extern "C" BOOLEAN WINAPI wit_coreclr_install_function_table(
    DWORD64, DWORD64, DWORD, PGET_RUNTIME_FUNCTION_CALLBACK, PVOID, PCWSTR);
extern "C" BOOLEAN WINAPI wit_coreclr_delete_function_table(PRUNTIME_FUNCTION);
extern "C" PRUNTIME_FUNCTION WINAPI wit_coreclr_lookup_function_entry(DWORD64, PDWORD64, PUNWIND_HISTORY_TABLE);
void wit_coreclr_code_gate_enter();
void wit_coreclr_code_gate_leave();
bool wit_coreclr_code_registered(uint64_t, uint64_t);
bool wit_coreclr_acquire_function(DWORD64, WitFunctionLease *);
void wit_coreclr_release_function(WitFunctionLease *);
PRUNTIME_FUNCTION wit_coreclr_leased_function(const WitFunctionLease &, DWORD64);
const void *wit_coreclr_unwind_read(DWORD64, DWORD, bool);
bool wit_coreclr_acquire_module(DWORD64, WitFunctionLease *);
PRUNTIME_FUNCTION wit_coreclr_leased_module(const WitFunctionLease &, DWORD64);
void wit_coreclr_release_module(WitFunctionLease *);
#endif
