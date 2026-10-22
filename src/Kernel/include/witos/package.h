#ifndef WITOS_PACKAGE_H
#define WITOS_PACKAGE_H
#include "types.h"

/* The limits of the readonly boot package: the loader reads at most WIT_PACKAGE_MAX_BYTES into the boot extents, the
 * contract's WIT_MAX_STORAGE_EXTENTS of 2 MiB each (W3.2: of a MiB, 128 MiB, until the .NET install's ASP.NET Core
 * framework put the package of the NuGet libraries' scenario at 142 MB). The kernel never parses the package (K8.4a):
 * the root task receives it as a memory object of kind PACKAGE and the system layer's libc validates its table
 * (src/Substrate/libc/files.c). The table of 4096 entries of 32 bytes fits the one mapping of 64 pages the libc reads it
 * through (N4.1: 1024 before the .NET install carried tzdata's zoneinfo). */
#define WIT_PACKAGE_MAX_FILES 4096U
#define WIT_PACKAGE_MAX_NAME 1024U
#define WIT_PACKAGE_EXTENT_BYTES (2ULL * 1024 * 1024)
#define WIT_PACKAGE_MAX_BYTES (256ULL * 1024 * 1024)
#endif
