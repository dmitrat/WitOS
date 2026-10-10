#ifndef WITOS_LIMITS_H
#define WITOS_LIMITS_H

/* The kernel's quotas in one place (K8.4d: mechanisms only). A component has one of two profiles: a self-test
 * kernel's fixture has the small one its tests exhaust (WIT_USER_*, WIT_HANDLE_CAPACITY, WIT_EVENT_CAPACITY,
 * WIT_MEMORY_OBJECT_CAPACITY), a system layer process (the root task and every process PROCESS_CREATE makes) the large
 * one (WIT_PROCESS_*), whose tables size every record's arrays. An exhausted object or memory quota fails the request
 * and changes nothing; an expired tick budget ends the component. These bound the prototype; they are not an
 * application ABI promise, and a layout that depends on one is named next to it. */

/* Objects a fixture holds. */
#define WIT_USER_THREAD_CAPACITY 4U
#define WIT_HANDLE_CAPACITY 16U
#define WIT_EVENT_CAPACITY 4U
#define WIT_MEMORY_OBJECT_CAPACITY 8U /* live memory objects created by one fixture */
/* Threads, handles and events of a system layer process (K5.3): a .NET program runs its main thread, the finalizer,
 * the thread pool's workers and gate thread and its own threads; each thread takes a handle for its identity and the
 * libc keeps another for it, and the libc's futex slots each hold an event. A kernel stack per thread of every registry
 * slot is part of the kernel image, the self-test kernel's too (K8.4d). */
#define WIT_PROCESS_THREAD_CAPACITY 16U
#define WIT_PROCESS_HANDLE_CAPACITY 64U
#define WIT_PROCESS_EVENT_CAPACITY 32U
/* Live memory objects a system layer process creates (S6.1): the process manager creates the writable segments and the
 * first stack of every process it starts, two objects or more while each lives. */
#define WIT_PROCESS_OBJECT_CAPACITY 32U

/* Objects of every component and of the kernel. */
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
#define WIT_MEMORY_OBJECT_TABLE_CAPACITY 64U /* memory objects kernel-wide */
#define WIT_MEMORY_OBJECT_PAGES 64U /* pages of one memory object */
#define WIT_DEVICE_CAPACITY 16U /* device descriptors the kernel publishes; the table fits one page */
#define WIT_INTERRUPT_CAPACITY 8U /* interrupt bindings, kernel-wide: one per line */
#define WIT_PIN_CAPACITY 8U /* DMA pins of a component */
#define WIT_THREAD_SUSPEND_MAX 127U /* suspend count of one thread */

/* Physical pages a component owns and its dynamic reservations. A system layer process's reservations cover every
 * mapping of a dynamic program's libraries, every split of a library's span and every thread stack (S5.4); its page
 * quota and reservation table size every space's arrays. */
#define WIT_USER_RESERVATION_CAPACITY 8U
#define WIT_USER_PAGE_CAPACITY 128U
#define WIT_PROCESS_RESERVATION_CAPACITY 256U
#define WIT_PROCESS_PAGE_CAPACITY 2048U
/* Memory-pressure hysteresis, in allocatable pages of global RAM and of the component quota. */
#define WIT_PRESSURE_LOW_PAGES 16U
#define WIT_PRESSURE_HIGH_PAGES 32U

/* Timer ticks a component may run before its budget expires: a fixture's, and the root task's, which bounds the
 * processes it creates. */
#define WIT_USER_TICK_BUDGET 10U
#define WIT_PROCESS_TICK_BUDGET 3000U

/* Bytes one call moves. */
#define WIT_DEBUG_WRITE_MAX 65536U /* one DEBUG_WRITE */
#define WIT_ABI_MAX_WRITE 256U /* bytes the kernel copies per step of a write */
#define WIT_ABI_MAX_RANDOM 65536U

#endif
