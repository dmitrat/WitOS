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
#define WIT_ACTIVATION_CAPACITY 4U /* activations pending on one thread */
#define WIT_CHANNEL_CAPACITY 4U /* live channels created by one component */
#define WIT_CHANNEL_TABLE_CAPACITY 32U /* channels kernel-wide */
#define WIT_PROCESS_CAPACITY 8U /* components kernel-wide: the registry and the pool of created processes (K5.2c) */
#define WIT_PROCESSOR_CAPACITY 8U /* processors of the kernel's table (K7.1) */
#define WIT_CHANNEL_QUEUE_DEPTH 4U /* messages queued for one endpoint */
#define WIT_CHANNEL_QUEUE_BYTES 1024U /* inline bytes queued for one endpoint */
#define WIT_CHANNEL_MESSAGE_BYTES 256U /* inline bytes of one message */
#define WIT_CHANNEL_MESSAGE_HANDLES 4U /* capabilities moved by one message */
#define WIT_MEMORY_OBJECT_CAPACITY 8U /* live memory objects created by one component */
#define WIT_MEMORY_OBJECT_TABLE_CAPACITY 64U /* memory objects kernel-wide */
#define WIT_MEMORY_OBJECT_PAGES 64U /* pages of one memory object */
#define WIT_DEVICE_CAPACITY 16U /* device descriptors the kernel publishes; the table fits one page */
#define WIT_INTERRUPT_CAPACITY 8U /* interrupt bindings, kernel-wide: one per line */
#define WIT_PIN_CAPACITY 8U /* DMA pins of a component */
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
/* Reservations of the system layer's processes, the root task and the processes it creates (S5.4): every mapping of
 * a dynamic program's libraries, every split of a library's span and every thread stack takes one. The frozen line's
 * full profile keeps WIT_RUNTIME_RESERVATION_CAPACITY. The largest table, the size of every space's arrays. */
#define WIT_PROCESS_RESERVATION_CAPACITY 256U
#define WIT_RUNTIME_PAGE_CAPACITY 2048U
/* Memory-pressure hysteresis, in allocatable pages of global RAM and of the component quota. */
#define WIT_PRESSURE_LOW_PAGES 16U
#define WIT_PRESSURE_HIGH_PAGES 32U

/* Timer ticks a component may run before its budget expires. */
#define WIT_USER_TICK_BUDGET 10U
#define WIT_RUNTIME_TICK_BUDGET 3000U

/* Bytes one call moves. */
#define WIT_DEBUG_WRITE_MAX 65536U /* one DEBUG_WRITE */
#define WIT_ABI_MAX_WRITE 256U /* bytes the kernel copies per step of a write; frozen-line callers size buffers by it */
#define WIT_FILE_MAX_READ 65536U
#define WIT_ABI_MAX_RANDOM 65536U

/* A component's environment: UTF-16 units of its "Name=Value" records with their terminators, and variables. */
#define WIT_ENVIRONMENT_UNITS 4096U
#define WIT_ENVIRONMENT_VARIABLES 64U

/* PE images the loader admits. */
#define WIT_PE_MAX_FILE_SIZE 1048576U
#define WIT_PE_MAX_IMAGE_SIZE 262144U
/* P5 managed Thread image: 1,060,864 bytes; next 64 KiB boundary. */
#define WIT_PE_FULL_IMAGE_SIZE 1114112U
#define WIT_PE_MAX_SECTIONS 16U
#define WIT_PE_MAX_RELOCATIONS 2048U
#define WIT_PE_MAX_UNWIND_ENTRIES 160U
#define WIT_PE_RUNTIME_UNWIND_ENTRIES 320U
#define WIT_PE_FULL_UNWIND_ENTRIES 4096U
#define WIT_PE_MAX_UNWIND_RANGES 320U
#define WIT_PE_EXPORT_CAPACITY 512U
#define WIT_PE_EXPORT_NAME_MAX 255U
#define WIT_PE_TLS_MAX_BYTES 3840U /* static TLS template, below the compiler-TLS data offset of one page */
#define WIT_PE_TLS_CALLBACK_CAPACITY 8U /* PE TLS callbacks of one library */

#endif
