#ifndef WITOS_NATIVE_LIMITS_H
#define WITOS_NATIVE_LIMITS_H

/* Quotas of the user-space native layers in one place: Runtime.Native, the NativeAOT adapters and the CoreCLR
 * adapters. Each bounds a static table, an arena or one input; exceeding it fails the request and never grows the
 * table. Kernel quotas are in witos/limits.h. */

/* Process startup and shutdown. */
#define WIT_NATIVE_MAX_INITIALIZERS 16U /* native initializer table entries of the image */
#define WIT_NATIVE_TLS_MAX_INITIALIZERS 32U /* dynamic C++ TLS initializers */
#define WIT_NATIVE_TLS_MAX_DESTRUCTORS 32U /* dynamic C++ TLS destructors */
#define WIT_NATIVE_EXIT_MAX_CALLBACKS 32U /* process-exit callbacks */

/* Files and paths. */
#define WIT_FILE_VIEW_CAPACITY 4U
#define WIT_PATH_INPUT_MAX 4096U

/* NativeAOT adapters. */
#define WIT_NATIVE_HEAP_CAPACITY 128U /* live allocations of the private nothrow heap */
#define WIT_NATIVE_HEAP_ARENA_BYTES (256U * 1024U)
#define WIT_NATIVE_GC_EVENT_CAPACITY 16U
#define WIT_NATIVE_VECTORED_HANDLER_CAPACITY 8U
#define WIT_SEH_SCOPE_CAPACITY 128U /* scope-table entries one SEH validation accepts */
#define WIT_PAL_ENV_CAPACITY 16U /* environment variables */
#define WIT_PAL_ENV_NAME_MAX 63U
#define WIT_PAL_ENV_VALUE_MAX 1023U
#define WIT_PAL_ENV_BLOCK_CAPACITY 4U /* environment blocks handed out at once */

/* CoreCLR adapters. */
#define WIT_CORECLR_MAPPER_CAPACITY 4U /* double-mapped executable regions */
#define WIT_CORECLR_VIEW_CAPACITY 16U /* views of those regions */
#define WIT_CORECLR_MAPPER_MAX_BYTES (64ULL * 1024 * 1024)
#define WIT_CORECLR_FUNCTION_TABLE_CAPACITY 32U /* dynamic function tables */

#endif
