#ifndef WITOS_UNWIND_VALIDATION_H
#define WITOS_UNWIND_VALIDATION_H
#include "witos/image_info.h"
#include "witos/unwind_metadata.h"
// The descriptor and bytes must remain immutable through validation and use.
// This validates metadata, not stack memory, handler execution or a stack walk.
WitUnwindValidation wit_unwind_validate_function(const WitUserImageInfo *, WitU32, WitUnwindRecord *);
WitUnwindValidation wit_unwind_validate_image(const WitUserImageInfo *);
const WitU8 *wit_unwind_info_address(const WitUserImageInfo *, WitU64);

struct WitUnwindStackRange {
    WitU64 Low, High;
};

// Bounds must describe stable, readable owned stack mappings. These primitives
// do not discover ownership or pin a thread; the guest adapter must supply that.
bool wit_unwind_read_stack(const WitUnwindStackRange *, WitU64, void *, WitU32);
bool wit_unwind_read_code(const WitUserImageInfo *, WitU64, void *, WitU32);
#endif
