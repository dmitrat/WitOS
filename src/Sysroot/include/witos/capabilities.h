#ifndef WITOS_CAPABILITIES_H
#define WITOS_CAPABILITIES_H
#include "witos/types.h"

/* libwitos's capability API (plan step D1.1, ADR 0025, RFC 0011 v3 section 9.1): the system layer's handles,
 * channels and mappings as WitOS's own services use them, the device manager and its drivers first. Every function
 * returns 0 or an errno value, as witos_spawn does: no kernel call number or status appears above layer 2 (RFC 0011
 * section 9.4). A function checks its arguments before it calls the kernel, which checks them again; a failed call
 * changes nothing. The managed OutWit.OS assembly calls these functions. Rights are the kernel's WIT_RIGHT_* values
 * (witos/user_abi.h). */

/* Takes the capability the process's creator granted it (witos/start.h): ENOENT when it was granted none or it was
 * taken already. */
int witos_take_grant(WitU64 *handle);

/* Closes a handle: EBADF for no handle of the process. */
int witos_handle_close(WitU64 handle);

/* A new handle of the same object with the rights given, a subset of the handle's: EACCES for more rights, EBADF. */
int witos_handle_duplicate(WitU64 handle, WitU64 rights, WitU64 *duplicate);

/* A new channel's two endpoints, each with SEND, RECEIVE, WAIT, DUPLICATE and TRANSFER: ENOMEM for the quota. */
int witos_channel_create(WitU64 *first, WitU64 *second);

/* Sends count bytes and handle_count handles, which move to the peer with the message (at most 256 bytes and four
 * handles): EPIPE when the peer's end is closed, EAGAIN when its queue is full, EINVAL for a size beyond the limits,
 * EACCES for a handle without TRANSFER. On failure the handles stay the caller's. */
int witos_channel_send(WitU64 endpoint, const void *bytes, WitU32 count, const WitU64 *handles, WitU32 handle_count);

/* Waits for a message and receives it into the buffers, writing what arrived to *count and *handle_count: EPIPE when
 * the peer's end is closed and nothing is queued, EMSGSIZE when the message does not fit, which leaves it queued. */
int witos_channel_receive(WitU64 endpoint, void *bytes, WitU32 capacity, WitU32 *count, WitU64 *handles,
    WitU32 handle_capacity, WitU32 *handle_count);

/* Maps bytes of a memory object from a page-aligned offset with a protection among WIT_MEMORY_READ, READ|WRITE and
 * READ|EXECUTE (witos/user_abi.h) into the caller's space: EACCES for rights the protection needs, EINVAL. */
int witos_memory_map(WitU64 object, WitU64 offset, WitU64 bytes, WitU32 protection, void **address);

/* Releases a mapping witos_memory_map made, whole: EINVAL for an address that starts no mapping. */
int witos_memory_unmap(void *address);

/* The device of a descriptor of the device table (D1.2), a handle with BIND, QUERY, DUPLICATE and TRANSFER: EACCES for
 * a table handle without ACQUIRE, EBUSY for a device another process holds, EINVAL for an index beyond the table. The
 * handle moves to another process when it is the process's last reference to the device. */
int witos_device_acquire(WitU64 table, WitU32 index, WitU64 *device);

/* A memory region of a device as a memory object (MAP, WRITE, QUERY, DUPLICATE, TRANSFER; never EXECUTE), mapped
 * uncached: EACCES without BIND, ENOSYS for a port region, EINVAL. The object covers at most 64 pages of the region
 * from the page the window names (ABI-1 1.3, C2.1), so a larger region (a framebuffer) is reached window by window;
 * witos_device_memory is the window of zero. */
int witos_device_memory(WitU64 device, WitU32 region, WitU64 *object);
int witos_device_memory_at(WitU64 device, WitU32 region, WitU64 window, WitU64 *object);

/* One access of 1, 2 or 4 bytes at an offset of a device's port region (ABI-1 1.7, H5.2, ADR 0031): EACCES without BIND,
 * EINVAL for a region, width or offset the descriptor does not allow, ENOSYS for a memory region or an architecture
 * without a port space. */
int witos_device_port_read(WitU64 device, WitU32 region, WitU32 offset, WitU32 width, WitU32 *value);
int witos_device_port_write(WitU64 device, WitU32 region, WitU32 offset, WitU32 width, WitU32 value);

/* An anonymous memory object of bytes, a multiple of the page size, its pages zeroed and charged to the process (D2):
 * MAP, WRITE, QUERY, DUPLICATE and TRANSFER; ENOMEM for the quota or more than 64 pages. */
int witos_memory_object_create(WitU64 bytes, WitU64 *object);

/* An auto-reset event (D2), with WAIT and SIGNAL: ENOMEM for the quota. */
int witos_event_create(WitU64 *event);

/* Sets an event (W1.2a): one waiter of an auto-reset event completes, or the next wait does; EACCES without SIGNAL. */
int witos_event_set(WitU64 event);

/* Waits for one of up to four objects until an absolute monotonic deadline (all ones for none), writing the index of
 * the one that completed to *index (D2): ETIMEDOUT at the deadline; a signal's activation does not end the wait. */
int witos_object_wait(const WitU64 *handles, WitU32 count, WitU64 deadline, WitU32 *index);

/* The monotonic clock's reading and its frequency in ticks per second (N4.3): what a deadline of witos_object_wait is
 * counted in. */
int witos_clock_read(WitU64 *ticks);
int witos_clock_frequency(WitU64 *frequency);

/* Binds a device's interrupt line to an event (D2): the kernel masks the line and sets the event on each interrupt; a
 * handle with ACK. EACCES without BIND, EBUSY for a line another binding holds, EINVAL. */
int witos_interrupt_bind(WitU64 device, WitU32 line, WitU64 event, WitU64 *binding);

/* Unmasks the bound line after its interrupt was served (D2); does nothing for a message binding. */
int witos_interrupt_ack(WitU64 binding);

/* Binds a message target of the board to an event (H1.2b, ADR 0029): a handle, and the address and the data the driver
 * writes into its function's MSI capability or MSI-X table entry; each message the device writes sets the event, and
 * nothing is masked. EACCES without BIND or SIGNAL, ENOMEM when every target is bound, and the errno of UNSUPPORTED on
 * a board without message interrupts. */
int witos_interrupt_bind_message(WitU64 device, WitU64 event, WitU64 *binding, WitU64 *address, WitU32 *data);

/* Whether the board serves message interrupts (QUERY's WIT_ABI_FEATURE_MESSAGE_INTERRUPTS, H1.2b): 1 or 0. */
int witos_message_interrupts(void);

/* Pins a page-aligned window of an anonymous memory object for the device's DMA (D2), writing its physical ranges to
 * ranges, capacity pairs of an address and a size (a zero size ends the list): a pin handle, which keeps the pages
 * resident and unmoved until witos_dma_unpin. EACCES without BIND or MAP, EINVAL for a window the object cannot take
 * or too few ranges. */
int witos_dma_pin(
    WitU64 device, WitU64 object, WitU64 offset, WitU64 bytes, WitU64 *ranges, WitU32 capacity, WitU64 *pin);

/* Ends a pin (D2). */
int witos_dma_unpin(WitU64 pin);

/* A full barrier for a device's view of memory (D2): MFENCE on x64, DSB SY on ARM64, between writing what a device
 * reads and telling the device so. */
void witos_device_barrier(void);
#endif
