#ifndef WITOS_STORAGE_H
#define WITOS_STORAGE_H
#include "package.h"
#include "boot.h"
int wit_storage_initialize(const WitBootInfo *boot);
const WitPackage *wit_storage_package(void);
#endif
