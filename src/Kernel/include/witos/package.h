#ifndef WITOS_PACKAGE_H
#define WITOS_PACKAGE_H
#include "types.h"

/* The limits of the readonly boot package: the loader reads at most WIT_PACKAGE_MAX_BYTES into the boot extents. The
 * kernel never parses the package (K8.4a): the root task receives it as a memory object of kind PACKAGE and the
 * system layer's libc validates its table (src/Substrate/libc/files.c). */
#define WIT_PACKAGE_MAX_FILES 1024U
#define WIT_PACKAGE_MAX_NAME 1024U
#define WIT_PACKAGE_MAX_BYTES (128ULL * 1024 * 1024)
#endif
