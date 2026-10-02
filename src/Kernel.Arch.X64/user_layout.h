#ifndef WITOS_USER_LAYOUT_H
#define WITOS_USER_LAYOUT_H
/* Only the bootstrap logical processor is brought online. Shared by discovery
 * and process-wide operations; SMP requires a real remote-CPU rendezvous. */
#define WIT_USER_PROCESSOR_COUNT 1U
/* Prototype hysteresis, in allocatable pages (global RAM and component quota). */
#define WIT_PRESSURE_LOW_PAGES 16U
#define WIT_PRESSURE_HIGH_PAGES 32U

/* Fixed addresses for the controlled M2 image, not an application ABI promise. */
#define WIT_USER_BASE 0x0000008000000000ULL
#define WIT_USER_LIMIT 0x0000008000200000ULL
#define WIT_USER_CODE 0x0000008000001000ULL
#define WIT_USER_INFO 0x0000008000004000ULL
#define WIT_USER_IMAGE_INFO_OFFSET 256U
#define WIT_USER_DATA 0x0000008000008000ULL
#define WIT_USER_DATA_END 0x000000800000A000ULL
/* Repeated native collided unwind exceeds the measured 32 KiB stack. */
#define WIT_USER_STACK_BOTTOM 0x0000008000015000ULL
#define WIT_USER_STACK_TOP 0x0000008000025000ULL
#define WIT_USER_PEER_PAGE 0x0000008000030000ULL
/* Dedicated PE image window, separate from fixed startup/data/thread regions. */
#define WIT_USER_IMAGE_BASE 0x0000008000100000ULL
#define WIT_USER_IMAGE_ALTERNATE 0x0000008000180000ULL
#define WIT_USER_THREAD_CAPACITY 4U
#define WIT_USER_THREAD_STRIDE 0x20000ULL
#define WIT_USER_TLS 0x0000008000027000ULL
#define WIT_USER_CS 0x33U
#define WIT_USER_SS 0x2BU
/* Separate PML4 slot: 64 GiB of sparse VA, bounded prototype metadata/frames. */
/* JIT RX views stay within rel32 reach of the current fixed native image.
 * This private window is disjoint from image/stacks and the default data arena. */
#define WIT_USER_CODE_BASE 0x0000008001000000ULL
#define WIT_USER_CODE_LIMIT 0x0000008040000000ULL
#define WIT_USER_MEMORY_BASE 0x0000010000000000ULL
#define WIT_USER_MEMORY_LIMIT 0x0000011000000000ULL
#define WIT_USER_RESERVATION_CAPACITY 8U
#define WIT_USER_PAGE_CAPACITY 128U
#define WIT_RUNTIME_PAGE_CAPACITY 2048U
#define WIT_RUNTIME_RESERVATION_CAPACITY 32U
#define WIT_RUNTIME_USER_LIMIT 0x0000008000400000ULL
#define WIT_RUNTIME_TICK_BUDGET 3000U
#define WIT_USER_TICK_BUDGET 10U
#endif
