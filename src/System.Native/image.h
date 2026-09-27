#ifndef WITOS_NATIVE_IMAGE_H
#define WITOS_NATIVE_IMAGE_H
#include "witos/user_abi.h"
#ifdef __cplusplus
extern "C" {
#endif
/* Internal single-image handoff, not a loader or managed module registry. */
int wit_native_image_valid(const WitUserImageInfo *image);
int wit_native_image_range(const WitUserImageInfo *image, WitU64 address, WitU64 size,
    WitU32 required, WitU32 forbidden, int initialized);
WitU64 wit_native_image_from_address(const WitUserImageInfo *image, WitU64 address);
void wit_native_process_image_initialize(const WitUserStartup *startup);
const WitUserImageInfo *wit_native_process_image(void);
/* Captured startup capability; the kernel checks its live rights on every write. */
WitU64 wit_native_process_console(void);
#ifdef __cplusplus
}
#endif
#endif
