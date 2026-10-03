#ifndef WITOS_CORECLR_FUNCTION_TABLES_H
#define WITOS_CORECLR_FUNCTION_TABLES_H
#include <stdint.h>
#include <stddef.h>
#include "../Runtime.Native/native_limits.h"

struct WitRuntimeFunction {
    uint32_t BeginAddress, EndAddress, UnwindData;
};

static_assert(sizeof(WitRuntimeFunction) == 12, "AMD64 runtime function entry");
using WitFunctionCallback = WitRuntimeFunction *(*)(uint64_t, void *);

struct WitFunctionTableHooks {
    void (*Enter)();
    void (*Leave)();
    bool (*Readable)(uint64_t, size_t);
    bool (*Executable)(uint64_t, size_t);
};

struct WitFunctionLease {
    uint64_t Token, Generation, Base, Length;
    WitRuntimeFunction *Entry;
    uint64_t ModuleReader = 0;
};

// Component-private registry. A reader lease covers callback execution and use
// of the returned record. Callbacks execute outside the registry gate. BeginRemove
// prevents new readers; FinishRemove succeeds only after all prior readers release.
// The caller must also quiesce executing code before freeing its code/metadata.
class WitFunctionTables {
    struct Record {
        uint64_t Key, Base, Length, Generation;
        WitRuntimeFunction *Table;
        uint32_t Count, Readers;
        WitFunctionCallback Callback;
        void *Context;
        bool Retiring;
    };

    static constexpr unsigned Capacity = WIT_CORECLR_FUNCTION_TABLE_CAPACITY;
    Record records[Capacity]{};

    struct Reader {
        uint64_t Token, Generation;
    };

    Reader readers[128]{};
    uint64_t nextGeneration = 1, nextReader = 1;
    WitFunctionTableHooks hooks;
    bool Add(uint64_t, uint64_t, uint64_t, WitRuntimeFunction *, uint32_t, WitFunctionCallback, void *);

public:
    constexpr explicit WitFunctionTables(WitFunctionTableHooks value) : hooks(value) {}

    WitFunctionTables(const WitFunctionTables &) = delete;
    WitFunctionTables &operator=(const WitFunctionTables &) = delete;
    bool AddTable(WitRuntimeFunction *, uint32_t, uint64_t, uint64_t);
    bool AddCallback(uint64_t, uint64_t, uint64_t, WitFunctionCallback, void *);
    bool Acquire(uint64_t, WitFunctionLease *);
    bool Lookup(const WitFunctionLease &, uint64_t, WitRuntimeFunction **);
    bool Release(WitFunctionLease *);
    bool HasRange(uint64_t, uint64_t);
    bool BeginRemove(uint64_t);
    bool FinishRemove(uint64_t);
};
#endif
