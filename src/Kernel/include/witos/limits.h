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
 * libc keeps another for it, the libc's futex slots each hold an event, and the libc holds a handle of every chunk of
 * shared memory CoreCLR's JIT maps twice (R6: an ordinary application filled 64 handles, and CoreCLR's own threads
 * with the thread pool's filled 16 threads; P2: the server collector on four processors keeps two threads a processor
 * beside the thread pool's, which passed 32; 64 futex slots and the exit word's event are 65 events). A thread's kernel
 * stack comes from the allocator when the thread is created (P1.5), so a slot of the registry costs the image its
 * records alone. The handles hold every memory object a process may create, every chunk of
 * a volume's file it maps shared (R4.2: CoreCLR maps every IL assembly so), every event and two of every thread, with
 * room for its channels (N2.2: MessagePack's suite filled 256 with 190 chunks of JIT code, and Roslyn's syntax suite
 * held 941 of 1024 beside 401 objects of its own, its assemblies of 156 MiB being 624 chunks). */
#define WIT_PROCESS_THREAD_CAPACITY 64U
#define WIT_PROCESS_HANDLE_CAPACITY 2048U
#define WIT_PROCESS_EVENT_CAPACITY 128U
/* Live memory objects a system layer process creates (S6.1): the process manager creates the writable segments and the
 * first stack of every process it starts, two objects or more while each lives, and the libc a chunk of 64 pages for
 * every 256 KiB of shared memory a JIT maps (R6; 512 since R7.3, where upstream's library tests compiled 30 MiB of code
 * into 120 chunks; 1024 since N2.2, where the namespace service created 481 for the chunks of the assemblies the
 * libraries' suites map, R4.2, and Roslyn's syntax suite 401 of its own). */
#define WIT_PROCESS_OBJECT_CAPACITY 1024U

/* Objects of every component and of the kernel. */
#define WIT_WAIT_ANY_CAPACITY 4U /* handles in one wait-any call */
#define WIT_ACTIVATION_CAPACITY 4U /* activations pending on one thread */
#define WIT_USER_CHANNEL_CAPACITY 4U /* live channels created by one fixture (K2) */
/* Live channels created by a system layer process (N4.2): its protocols take a few, and each pipe it gives a child as a
 * standard stream takes one for the wakeups of that pipe (src/Substrate/libc/pipe.c), three for a child with every
 * stream redirected. */
#define WIT_PROCESS_CHANNEL_CAPACITY 16U
#define WIT_CHANNEL_TABLE_CAPACITY 128U /* channels kernel-wide: eight processes of sixteen (32 until N4.2) */
/* Components kernel-wide: the registry and the pool of created processes (K5.2c). Thirty-two since P1.5, in both
 * kernels: a slot's 32 kernel stacks left the image for the stack window (witos/kernel_stacks.h), whose pages a stack
 * takes from the allocator when a thread of its slot is created, so a slot costs the image its record alone, some
 * 360 KiB in the pool. Until then the stacks made the x64 release image 35.5 MiB with twelve slots, sixteen 52.5 MiB,
 * which the firmware refused to load with 128 MiB, and the self-test kernel kept eight (N4.3). */
#define WIT_PROCESS_CAPACITY 32U
#define WIT_PROCESSOR_CAPACITY 8U /* processors of the kernel's table (K7.1) */
#define WIT_CHANNEL_QUEUE_DEPTH 4U /* messages queued for one endpoint */
#define WIT_CHANNEL_QUEUE_BYTES 1024U /* inline bytes queued for one endpoint */
#define WIT_CHANNEL_MESSAGE_BYTES 256U /* inline bytes of one message */
#define WIT_CHANNEL_MESSAGE_HANDLES 4U /* capabilities moved by one message */
/* Memory objects kernel-wide (R6; 1024 since R7.3; 4096 since N2.2): the namespace service makes one of every 256 KiB
 * of a volume's file a process maps shared, and Roslyn's syntax suite filled 1024 with 401 objects of its own beside
 * 624 chunks of its assemblies; the run of the libraries' fourteen suites held more than 1000 at once. */
#define WIT_MEMORY_OBJECT_TABLE_CAPACITY 4096U
#define WIT_MEMORY_OBJECT_PAGES 64U /* pages of one memory object */
#define WIT_DEVICE_CAPACITY 64U /* device descriptors the kernel publishes, in as many pages as they take (H1.2a) */
#define WIT_INTERRUPT_CAPACITY 8U /* interrupt bindings, kernel-wide: one per line */
#define WIT_PIN_CAPACITY 8U /* DMA pins of a component */
#define WIT_THREAD_SUSPEND_MAX 127U /* suspend count of one thread */

/* Physical pages a component owns and its dynamic reservations. A system layer process's reservations cover every
 * mapping of a dynamic program's libraries, every split of a library's span and every thread stack (S5.4), and
 * CoreCLR's with its JIT: its many small reservations, the shared memory chunks its executable memory maps twice and
 * libraries of megabytes in package mappings of 64 pages (R3.2b); its reservation table sizes every space's arrays,
 * 4096 entries since R7.3, where upstream's library tests filled 1024 with the JIT's two views of each shared memory
 * chunk beside the GC's reservations, 8192 since N2.2, where Roslyn's syntax suite held 3148 with a mapping of each chunk
 * of its assemblies (24 bytes an entry). The page tables record which pages a space owns and aliases (K9), so
 * the page quota sizes no array but the rollback bitmap of one commit, 32 KiB: a system layer process may own 1 GiB since
 * N2.2, where the libraries' own test suites need 400 MB on Linux and 256 MiB ended them, and the guest's physical memory,
 * which the memory-pressure event reports, is what binds it first. */
#define WIT_USER_RESERVATION_CAPACITY 8U
#define WIT_USER_PAGE_CAPACITY 128U
#define WIT_PROCESS_RESERVATION_CAPACITY 8192U
#define WIT_PROCESS_PAGE_CAPACITY 262144U
/* Memory-pressure hysteresis, in allocatable pages of global RAM and of the component quota. */
#define WIT_PRESSURE_LOW_PAGES 16U
#define WIT_PRESSURE_HIGH_PAGES 32U

/* Timer ticks a component may run before its budget expires: a fixture's, and the root task's, which bounds the
 * processes it creates: an hour of processor time since R7.2b, where 30 seconds ended upstream's test groups and
 * allocation tests part way, for each online processor since P2, where the libraries' suites busy on four processors
 * spent the hour in 45 minutes. */
#define WIT_USER_TICK_BUDGET 10U
#define WIT_PROCESS_TICK_BUDGET 360000U

/* Bytes one call moves. */
#define WIT_DEBUG_WRITE_MAX 65536U /* one DEBUG_WRITE */
#define WIT_ABI_MAX_WRITE 256U /* bytes the kernel copies per step of a write */
#define WIT_ABI_MAX_RANDOM 65536U

#endif
