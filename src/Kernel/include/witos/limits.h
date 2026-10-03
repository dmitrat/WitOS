#ifndef WITOS_LIMITS_H
#define WITOS_LIMITS_H

/* Quotas of the prototype kernel in one place. A component has the ordinary quota; a component created with the
 * runtime profile has the larger RUNTIME one. An exhausted object or memory quota fails the request and changes
 * nothing; an expired tick budget ends the component. These are prototype bounds, not an application ABI promise;
 * a layout that depends on one is named next to it. User-space quotas are in Runtime.Native/native_limits.h. */

/* Objects a component holds. */
#define WIT_USER_THREAD_CAPACITY 4U /* threads; user_layout.h reserves one stack and TLS window per thread */
#define WIT_HANDLE_CAPACITY 16U
#define WIT_RUNTIME_HANDLE_CAPACITY 32U
#define WIT_EVENT_CAPACITY 4U
#define WIT_RUNTIME_EVENT_CAPACITY 16U
#define WIT_WAIT_ANY_CAPACITY 4U /* handles in one wait-any call */
#define WIT_APC_CAPACITY 4U /* queued user APCs */
#define WIT_LIBRARY_CAPACITY 4U /* loaded native libraries */
#define WIT_LIBRARY_READER_CAPACITY 16U /* module reader handles */
#define WIT_STACK_LEASE_CAPACITY 4U
#define WIT_CODE_VIEW_CAPACITY 16U /* executable views of runtime code memory */
#define WIT_EXCEPTION_MAX_DEPTH 4U /* nested exception deliveries on one thread */
#define WIT_THREAD_SUSPEND_MAX 127U /* suspend count of one thread, as MAXIMUM_SUSPEND_COUNT on Windows */

/* Physical pages a component owns and its dynamic reservations; the runtime page quota sizes the owned-page table. */
#define WIT_USER_RESERVATION_CAPACITY 8U
#define WIT_USER_PAGE_CAPACITY 128U
#define WIT_RUNTIME_RESERVATION_CAPACITY 32U
#define WIT_RUNTIME_PAGE_CAPACITY 2048U
/* Memory-pressure hysteresis, in allocatable pages of global RAM and of the component quota. */
#define WIT_PRESSURE_LOW_PAGES 16U
#define WIT_PRESSURE_HIGH_PAGES 32U

/* Timer ticks a component may run before its budget expires. */
#define WIT_USER_TICK_BUDGET 10U
#define WIT_RUNTIME_TICK_BUDGET 3000U

/* Bytes one call moves. */
#define WIT_ABI_MAX_WRITE 256U /* debug write */
#define WIT_CONSOLE_MAX_WRITE 65536U
#define WIT_FILE_MAX_READ 65536U
#define WIT_ABI_MAX_RANDOM 65536U

/* PE images the loader admits. */
#define WIT_PE_MAX_FILE_SIZE 1048576U
#define WIT_PE_MAX_IMAGE_SIZE 262144U
/* P5 managed Thread image: 1,060,864 bytes; next 64 KiB boundary. */
#define WIT_PE_FULL_IMAGE_SIZE 1114112U
#define WIT_PE_MAX_SECTIONS 16U
#define WIT_PE_MAX_RELOCATIONS 2048U
#define WIT_PE_MAX_UNWIND_ENTRIES 128U
#define WIT_PE_RUNTIME_UNWIND_ENTRIES 320U
#define WIT_PE_FULL_UNWIND_ENTRIES 4096U
#define WIT_PE_MAX_UNWIND_RANGES 320U
#define WIT_PE_EXPORT_CAPACITY 512U
#define WIT_PE_EXPORT_NAME_MAX 255U
#define WIT_PE_TLS_MAX_BYTES 3840U /* static TLS template, below the compiler-TLS data offset of one page */

#endif
