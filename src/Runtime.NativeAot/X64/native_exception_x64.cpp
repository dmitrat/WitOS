#include "exception_classification.h"

extern "C" WitU32 wit_x64_classify_gp(const WitU8 *bytes, WitU32 size, WitU64 error)
{
    if (!bytes || !size || size > 15) {
        return WIT_GP_UNSUPPORTED;
    }
    WitU32 i = 0;
    while (i < size) {
        const WitU8 prefix = bytes[i];
        if ((prefix >= 0x40 && prefix <= 0x4F) ||
            prefix == 0x66 ||
            prefix == 0x67 ||
            prefix == 0x26 ||
            prefix == 0x2E ||
            prefix == 0x36 ||
            prefix == 0x3E ||
            prefix == 0x64 ||
            prefix == 0x65) {
            ++i;
        } else {
            break;
        }
    }
    if (i == size) {
        return WIT_GP_UNSUPPORTED;
    }
    const WitU8 opcode = bytes[i++];
    if (opcode == 0xFA || opcode == 0xF4) {
        return error ? WIT_GP_UNSUPPORTED : WIT_GP_PRIVILEGED;
    }
    if (i == size) {
        return WIT_GP_UNSUPPORTED;
    }
    if (opcode == 0x8E) {
        return error ? WIT_GP_ACCESS_UNKNOWN : WIT_GP_UNSUPPORTED;
    }
    if (error) {
        return WIT_GP_UNSUPPORTED;
    }
    if (opcode == 0x8B || opcode == 0x89) {
        return (bytes[i] & 0xC0) != 0xC0 ? WIT_GP_ACCESS_UNKNOWN : WIT_GP_UNSUPPORTED;
    }
    if (opcode == 0x0F) {
        const WitU8 second = bytes[i++];
        if (i < size && (second == 0x28 || second == 0x29) && (bytes[i] & 0xC0) != 0xC0) {
            return WIT_GP_ACCESS_UNKNOWN;
        }
    }
    return WIT_GP_UNSUPPORTED;
}
