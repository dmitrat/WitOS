extern "C" {
#include "image.h"
}

// The NativeAotBoot acceptance hands upstream wmain one native test entry. The kernel self-test
// selects it by the boot resource name of each runtime component; the guest driver only asks here.
using Callback = int (*)(int);
using Fixture = int (*)(Callback);

extern "C" {
int wit_runtime_worker_acceptance(Callback callback);
int wit_runtime_native_fault(Callback callback);
int wit_runtime_stack_overflow(Callback callback);
int wit_runtime_raw_join(Callback callback);
int wit_runtime_raw_detached(Callback callback);
int wit_runtime_fault_join(Callback callback);
int wit_runtime_fault_detached(Callback callback);
}

namespace {
struct Variant {
    const wchar_t *Name;
    Fixture Entry;
};

const Variant variants[] = {{L"boot:/WitOS.NativeAotBoot.native-fault.pe", &wit_runtime_native_fault},
    {L"boot:/WitOS.NativeAotBoot.raw-join.pe", &wit_runtime_raw_join},
    {L"boot:/WitOS.NativeAotBoot.raw-detached.pe", &wit_runtime_raw_detached},
    {L"boot:/WitOS.NativeAotBoot.fault-join.pe", &wit_runtime_fault_join},
    {L"boot:/WitOS.NativeAotBoot.fault-detached.pe", &wit_runtime_fault_detached},
    {L"boot:/WitOS.NativeAotBoot.stack-overflow.pe", &wit_runtime_stack_overflow}};

bool named(const WitUserImageInfo *image, const wchar_t *name)
{
    WitU32 length = 0;
    while (name[length]) {
        ++length;
    }
    if (length != image->ResourceNameLength) {
        return false;
    }
    for (WitU32 i = 0; i < length; ++i) {
        if (image->ResourceName[i] != name[i]) {
            return false;
        }
    }
    return true;
}
}

// Unlisted names, including the plain boot:/WitOS.NativeAotBoot.pe, run the worker acceptance.
extern "C" Fixture wit_runtime_boot_fixture(const WitUserImageInfo *image)
{
    for (const auto &variant : variants) {
        if (named(image, variant.Name)) {
            return variant.Entry;
        }
    }
    return &wit_runtime_worker_acceptance;
}
