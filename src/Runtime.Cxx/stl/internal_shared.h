#pragma once
/* WitOS's replacement for internal_shared.h, the closed vcruntime header that the separately compiled microsoft/STL
 * sources include from the toolset's crt/src/vcruntime (P6.4.i). It declares only what the pinned sources WitOS
 * builds use: the Windows API and the C runtime's internal allocation functions, which are the ordinary ones of the
 * WitOS UCRT subset. Nothing of the toolset's header is copied. */
#include <Windows.h>
#include <malloc.h>
#include <stdlib.h>

#define _calloc_crt calloc
#define _free_crt free
#define _malloc_crt malloc
#define _realloc_crt realloc
