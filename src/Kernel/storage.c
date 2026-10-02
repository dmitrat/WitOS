#include "witos/storage.h"
#include "witos/virtual.h"
static WitPackage storage;
static int published;
int wit_storage_initialize(const WitBootInfo* boot)
{
    WitPackage candidate;
    // Architecture mapping has verified ownership and installed supervisor RO/NX
    // mappings for the entire separate boot allocation before this call.
    if(published||!boot||wit_package_open(wit_virtual_boot_storage(),boot->StorageBytes,&candidate)!=WitPackageOk)return 0;
    storage=candidate;published=1;return 1;
}
const WitPackage* wit_storage_package(void){return published?&storage:0;}
