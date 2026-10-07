#include "common.h"
#include "gcenv.h"
#include "gcenv.ee.h"
#include "gcconfig.h"
#include "RhConfig.h"
#include "pal.witos.h"
#include "pal_environment.witos.h"
#include <errno.h>

/* Component-lifetime initialization. No worker is parked while holding this
 * gate; a concurrent/reentrant caller receives BUSY and may retry. Direct GC
 * configuration/OS lifecycle changes remain externally serialized. */
static volatile WitU32 gate;
static bool ready;

bool PalInit()
{
    const DWORD saved_error = GetLastError();
    if (!wit_native_process_image() || !wit_pal_environment_is_ready() || !g_pRhConfig) {
        SetLastError(ERROR_NOT_READY);
        return false;
    }
    // Do not access compiler TLS (including errno) until kernel state confirms
    // it exists. Writable FS/GS hints never determine the CPU count or identity.
    WitUserThreadInfo info;
    const auto status = wit_native_thread_query(WIT_THREAD_SELF, &info);
    if (status != WIT_STATUS_OK) {
        wit_pal_set_status(status);
        return false;
    }
    if (!info.ProcessId || !info.RawTls) {
        SetLastError(ERROR_GEN_FAILURE);
        return false;
    }
    if (!info.CompilerTls) {
        SetLastError(ERROR_NOT_READY);
        return false;
    }
    if (info.ProcessorCount != 1) {
        SetLastError(ERROR_NOT_SUPPORTED);
        return false;
    }
    if (!wit_native_try_lock(&gate)) {
        SetLastError(ERROR_BUSY);
        return false;
    }
    const int saved_errno = errno;
    DWORD failure = ERROR_SUCCESS;
    if (ready) {
        // Shutdown ends this lifecycle; cached init must not resurrect it or
        // overwrite refreshed configuration after GC/runtime startup.
        if (GCToOSInterface::GetTotalProcessorCount() != info.ProcessorCount) {
            failure = ERROR_INVALID_STATE;
        }
    } else {
        uint64_t count;
        // Match upstream parsing/validity: zero, unparseable and >65535 values
        // fall back to kernel discovery. A valid override must fit this profile.
        if (g_pRhConfig->ReadConfigValue("PROCESSOR_COUNT", &count, true) &&
            count &&
            count <= 65535 &&
            count != info.ProcessorCount) {
            failure = ERROR_NOT_SUPPORTED;
        }
        if (!failure) {
            GCConfig::Initialize();
            if (!GCToOSInterface::Initialize()) {
                failure = ERROR_GEN_FAILURE;
            } else {
                ready = true;
            }
        }
    }
    errno = saved_errno;
    wit_native_unlock(&gate);
    SetLastError(failure ? failure : saved_error);
    return failure == ERROR_SUCCESS;
}
