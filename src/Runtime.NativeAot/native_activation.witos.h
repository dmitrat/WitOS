#pragma once
#include "bootstrap.h"

/* Activations of the frozen Windows-form line (plan step K1.3). The kernel delivers THREAD_ACTIVATE through the
 * process's one fault callback (RFC 0011 section 7.5); this module registers a callback that handles activations
 * alone where no exception dispatcher is linked, yields to the dispatcher where one is, and runs an activation's
 * callback for either. */

/* Registers entry as the process's fault callback unless one that handles faults is already registered: the
 * activation-only entry yields to any other, any other stays. Returns the kernel's status; OK when nothing changed. */
extern "C" WitU64 wit_native_fault_callback_register(WitU64 entry);

/* Ensures a fault callback that delivers activations is registered (QueueUserAPC calls it before THREAD_ACTIVATE). */
extern "C" WitU64 wit_native_activation_install(void);

/* The activation-only fault callback: runs an activation and rejects a fault, which the kernel then reports. */
extern "C" void wit_native_activation_entry(WitU64 token, WitU64 vector, WitU64 address);

/* Runs the callback of the activation record token names and continues the interrupted context; never returns. */
extern "C" [[noreturn]] void wit_native_activation_dispatch(WitU64 token, WitU64 address);
