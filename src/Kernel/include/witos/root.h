#ifndef WITOS_ROOT_H
#define WITOS_ROOT_H
#include "types.h"

/* The startup descriptor of a component the kernel builds from a flat image (RFC 0011 section 7.11, plan step K4):
 * what the kernel hands the first thread in its argument register, at WIT_USER_INFO. Everything else the system layer
 * builds for itself. A self-test kernel's fixtures start with it too (K8.4c), holding the log alone. The handles are
 * the root task's initial capabilities: the kernel log (DEBUG_WRITE), the boot package as a read-only
 * memory object, the device descriptor table with the authority to acquire devices, and the clock capability that
 * sets UTC (K6); the table carries its count so that later steps append. */
#define WIT_ROOT_STARTUP_VERSION 1U
#define WIT_ROOT_STARTUP_SIZE 128U
#define WIT_ROOT_HANDLES 8U
#define WIT_ROOT_HANDLE_LOG 0U
#define WIT_ROOT_HANDLE_PACKAGE 1U
#define WIT_ROOT_HANDLE_DEVICES 2U
#define WIT_ROOT_HANDLE_CLOCK 3U

typedef struct WitRootStartup {
    WitU32 Version, Size;
    WitU32 AbiVersion, Features; /* ABI-1: the version and the feature mask QUERY reports */
    WitU64 MemoryBase, MemoryLimit; /* the dynamic data arena of the component */
    WitU64 CodeBase, CodeLimit; /* the dynamic code arena */
    WitU64 PackageBytes; /* the size of the boot package object */
    WitU32 HandleCount, Reserved;
    WitU64 Handles[WIT_ROOT_HANDLES]; /* zero beyond HandleCount */
} WitRootStartup;

WIT_STATIC_ASSERT(sizeof(WitRootStartup) == WIT_ROOT_STARTUP_SIZE, "Root startup ABI");
#endif
