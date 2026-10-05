#ifndef WITOS_PROCESS_STATE_H
#define WITOS_PROCESS_STATE_H
#include "types.h"

/* State every module of a component shares (P6.4.j3a): the environment and the current directory, which the kernel
 * keeps for the process as kernel32 keeps them for a Windows process. Every module reaches them through this call,
 * so a change by one is what every other sees. */
#define WIT_PROCESS_STATE_VERSION 1U
#define WIT_PROCESS_ENV_GET 0U
#define WIT_PROCESS_ENV_SET 1U
#define WIT_PROCESS_ENV_BLOCK 2U
#define WIT_PROCESS_CWD_GET 3U
#define WIT_PROCESS_CWD_SET 4U
#define WIT_PROCESS_NAME_UNITS 255U /* UTF-16 units of a variable name */
#define WIT_PROCESS_PATH_BYTES 1025U /* '/' and a package name */

typedef struct WitProcessStateRequest {
    WitU32 Version, Size, Operation, Reserved;
    WitU64 Name, NameUnits, Value, ValueUnits, Buffer, BufferBytes;
} WitProcessStateRequest;

WIT_STATIC_ASSERT(sizeof(WitProcessStateRequest) == 64, "Process state ABI");
/* ENV_GET: Name/NameUnits is a UTF-16 name, which compares with ASCII case folding; the result is the value's UTF-16
 * units, and the value is copied without a terminator only when BufferBytes holds it whole. A missing name is
 * NOT_FOUND. A name is 1 to WIT_PROCESS_NAME_UNITS units without '=' or NUL.
 * ENV_SET: Value/ValueUnits is the new UTF-16 value without NUL, and Value 0 removes the variable (NOT_FOUND when it
 * is absent). Beyond the environment's quotas the call is NO_MEMORY and changes nothing.
 * ENV_BLOCK: the environment as "Name=Value\0" records in the order they were set, and a final "\0"; the result is its
 * units, copied only whole.
 * CWD_GET: the current directory as canonical UTF-8 from '/'; the result is its bytes, copied only whole.
 * CWD_SET: Name/NameUnits is a canonical UTF-8 path from '/' naming a directory of the package; a missing path is
 * NOT_FOUND, a file WRONG_TYPE.
 * Unused fields are zero. Failures change no state and no output. */
#endif
