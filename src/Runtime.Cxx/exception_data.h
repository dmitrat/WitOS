#pragma once
/* The MSVC x64 C++ exception ABI as the compiler emits it (P6.4.e). Layouts follow the public toolset headers
 * ehdata.h, ehdata4_export.h and rttidata.h, which document the format; this runtime owns its definitions so that it
 * depends on no toolset version. Every address field is an image-relative 32-bit offset. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

namespace WitCxx {

using u8 = unsigned char;
using u32 = unsigned int;
using i32 = int;
using u64 = unsigned long long;
using i64 = long long;

constexpr u32 CXX_EXCEPTION = 0xE06D7363U; // 'msc' | 0xE0000000
constexpr u64 MAGIC1 = 0x19930520ULL, MAGIC2 = 0x19930521ULL, MAGIC3 = 0x19930522ULL, PURE_MAGIC = 0x01994000ULL;
constexpr u32 CONSOLIDATE = 0x80000029U; // STATUS_UNWIND_CONSOLIDATE

/* ThrowInfo attributes. */
constexpr u32 TI_CONST = 0x1, TI_VOLATILE = 0x2, TI_UNALIGNED = 0x4, TI_PURE = 0x8;
/* CatchableType properties. */
constexpr u32 CT_SIMPLE = 0x1, CT_REFERENCE_ONLY = 0x2, CT_VIRTUAL_BASE = 0x4;
/* Handler adjectives. */
constexpr u32 HT_CONST = 0x1, HT_VOLATILE = 0x2, HT_UNALIGNED = 0x4, HT_REFERENCE = 0x8, HT_ELLIPSIS = 0x40;

struct TypeDescriptor {
    const void *VTable;
    void *Spare;
    char Name[1];
};

struct Displacement {
    i32 Member, VirtualBase, VirtualBaseOffset; // mdisp, pdisp (-1 without virtual base), vdisp
};

struct CatchableType {
    u32 Properties;
    i32 Type;
    Displacement This;
    i32 Size;
    i32 Copy;
};

struct CatchableTypeArray {
    i32 Count;
    i32 Types[1];
};

struct ThrowInfo {
    u32 Attributes;
    i32 Destroy;
    i32 ForwardCompat;
    i32 Catchables;
};

/* A decoded __CxxFrameHandler4 FuncInfo4. */
struct FunctionInfo {
    u8 Header;
    i32 UnwindMap, TryMap, StateMap;
    u32 Frame; // catch funclets: displacement of the parent frame pointer from the funclet's establisher frame
};

constexpr u8 FI_CATCH = 0x01, FI_SEPARATED = 0x02, FI_BBT = 0x04, FI_UNWIND_MAP = 0x08, FI_TRY_MAP = 0x10,
             FI_NOEXCEPT = 0x40;

struct TryBlock {
    i32 Low, High, CatchHigh;
    i32 Handlers;
};

struct Handler {
    u8 Header;
    u32 Adjectives;
    i32 Type;
    u32 CatchObject;
    i32 Code;
    u32 Continuations;
    u64 Continuation[2]; // image-relative
};

constexpr u8 HF_ADJECTIVES = 0x01, HF_TYPE = 0x02, HF_CATCH_OBJECT = 0x04, HF_CONTINUATION_RVA = 0x08;

/* A bounded reader of the compressed FH4 encoding. */
class Reader {
public:
    explicit Reader(const u8 *at) : m_at(at) {}

    u32 Compressed();
    i32 Int();

    u8 Byte()
    {
        return *m_at++;
    }

    const u8 *Position() const
    {
        return m_at;
    }

private:
    const u8 *m_at;
};

constexpr u32 MAP_LIMIT = 0x10000; // entries in one map; larger metadata is malformed

} // namespace WitCxx
