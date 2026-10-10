#ifndef WITOS_USER_LAYOUT_H
#define WITOS_USER_LAYOUT_H
#include "limits.h"
/* Fixed addresses for the controlled M2 image, not an application ABI promise. */
#define WIT_USER_BASE 0x0000008000000000ULL
#define WIT_USER_LIMIT 0x0000008000200000ULL
#define WIT_USER_CODE 0x0000008000001000ULL
#define WIT_USER_INFO 0x0000008000004000ULL
#define WIT_USER_DATA 0x0000008000008000ULL
#define WIT_USER_DATA_END 0x000000800000A000ULL
/* Repeated native collided unwind exceeds the measured 32 KiB stack. */
#define WIT_USER_STACK_BOTTOM 0x0000008000015000ULL
#define WIT_USER_STACK_TOP 0x0000008000025000ULL
#define WIT_USER_PEER_PAGE 0x0000008000030000ULL
/* The root task's image window, separate from fixed startup/data/thread regions. */
#define WIT_USER_IMAGE_BASE 0x0000008000100000ULL
#define WIT_USER_THREAD_STRIDE 0x20000ULL
#define WIT_USER_TLS 0x0000008000027000ULL
/* Separate PML4 slot: 64 GiB of sparse VA, bounded prototype metadata/frames. */
/* The code arena: a process's images and code mappings, disjoint from the image window, the stacks and the data
 * arena. */
#define WIT_USER_CODE_BASE 0x0000008001000000ULL
#define WIT_USER_CODE_LIMIT 0x0000008040000000ULL
#define WIT_USER_MEMORY_BASE 0x0000010000000000ULL
#define WIT_USER_MEMORY_LIMIT 0x0000011000000000ULL
#define WIT_RUNTIME_USER_LIMIT 0x0000008000400000ULL
#endif
