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
#endif
