#include "cxx_runtime.h"

/* __CxxFrameHandler4: the language handler of every function the compiler gives C++ exception metadata (P6.4.e).
 * The search phase finds a catch for a C++ exception and initializes its parameter; the catch then runs through
 * RtlUnwindEx frame consolidation, whose callback calls the catch funclet on the current stack, below the frames being
 * unwound, so the exception object stays alive. The unwind phase destroys the objects of every frame it leaves.
 * While a catch runs, the frame holding its try block still has the IP inside the try; a per-thread record of the
 * running catch gives the frame its state before the try, so an exception leaving the catch neither matches that try
 * again nor unwinds its objects twice. */
namespace WitCxx {

u32 Reader::Compressed()
{
    const u8 first = m_at[0];
    u32 value;
    if (!(first & 0x1)) {
        value = first >> 1;
        m_at += 1;
    } else if ((first & 0x3) == 0x1) {
        value = (first | (u32)m_at[1] << 8) >> 2;
        m_at += 2;
    } else if ((first & 0x7) == 0x3) {
        value = (first | (u32)m_at[1] << 8 | (u32)m_at[2] << 16) >> 3;
        m_at += 3;
    } else if ((first & 0xF) == 0x7) {
        value = (first | (u32)m_at[1] << 8 | (u32)m_at[2] << 16 | (u32)m_at[3] << 24) >> 4;
        m_at += 4;
    } else {
        value = m_at[1] | (u32)m_at[2] << 8 | (u32)m_at[3] << 16 | (u32)m_at[4] << 24;
        m_at += 5;
    }
    return value;
}

i32 Reader::Int()
{
    const u32 value = m_at[0] | (u32)m_at[1] << 8 | (u32)m_at[2] << 16 | (u32)m_at[3] << 24;
    m_at += 4;
    return (i32)value;
}

namespace {

constexpr u64 WITCXX_MAGIC = 0x5843787457ULL; // "WitCxX": a consolidation started by this runtime

/* One dispatcher frame of a function with C++ exception metadata. */
struct Frame {
    u64 ImageBase;
    u32 Start; // RVA of the function or funclet
    FunctionInfo Info;
    u64 Establisher; // the dispatcher's establisher frame
    u64 Base; // objects and funclets address this frame: the parent's for a catch funclet
};

/* What the consolidation callback needs to run one catch. It lives in the search-phase handler's frame, which stays
 * above the callback until the catch completes. */
struct CatchContext {
    u64 Establisher, Base, Funclet, ImageBase;
    i32 State;
    u32 Continuations;
    u64 Continuation[2];
    void *Object;
    const ThrowInfo *Info;
    u64 ThrowImage;
};

struct Thrown {
    void *Object;
    const ThrowInfo *Info;
    u64 ImageBase;
};

const u8 *At(u64 imageBase, i32 rva)
{
    return (const u8 *)(imageBase + (u64)(u32)rva);
}

void Decode(const DISPATCHER_CONTEXT *dispatcher, u64 establisher, Frame &frame)
{
    frame.ImageBase = dispatcher->ImageBase;
    frame.Start = dispatcher->FunctionEntry->BeginAddress;
    Reader reader(At(frame.ImageBase, *(const i32 *)dispatcher->HandlerData));
    FunctionInfo &info = frame.Info;
    info = {};
    info.Header = reader.Byte();
    if (info.Header & FI_BBT) {
        (void)reader.Compressed();
    }
    if (info.Header & FI_UNWIND_MAP) {
        info.UnwindMap = reader.Int();
    }
    if (info.Header & FI_TRY_MAP) {
        info.TryMap = reader.Int();
    }
    if (info.Header & FI_SEPARATED) {
        const i32 segments = reader.Int();
        if (!segments) {
            Fatal();
        }
        Reader table(At(frame.ImageBase, segments));
        const u32 count = table.Compressed();
        for (u32 i = 0; i < count && i < MAP_LIMIT; ++i) {
            const i32 start = table.Int(), map = table.Int();
            if ((u32)start == frame.Start) {
                info.StateMap = map;
                break;
            }
        }
    } else {
        info.StateMap = reader.Int();
    }
    if (info.Header & FI_CATCH) {
        info.Frame = reader.Compressed();
    }
    frame.Establisher = establisher;
    frame.Base = (info.Header & FI_CATCH) ? *(const u64 *)(establisher + info.Frame) : establisher;
}

/* The state of the last IP-to-state entry at or before pc; -1 before the first. */
i32 StateAt(const Frame &frame, u64 pc)
{
    if (!frame.Info.StateMap) {
        return -1;
    }
    Reader reader(At(frame.ImageBase, frame.Info.StateMap));
    const u32 count = reader.Compressed();
    const u64 offset = pc - frame.ImageBase - frame.Start;
    u64 ip = 0;
    i32 state = -1;
    for (u32 i = 0; i < count && i < MAP_LIMIT; ++i) {
        ip += reader.Compressed();
        const i32 next = (i32)reader.Compressed() - 1;
        if (offset < ip) {
            break;
        }
        state = next;
    }
    return state;
}

bool ReadTry(Reader &reader, TryBlock &block)
{
    block.Low = (i32)reader.Compressed();
    block.High = (i32)reader.Compressed();
    block.CatchHigh = (i32)reader.Compressed();
    block.Handlers = reader.Int();
    return block.Low <= block.High && block.High <= block.CatchHigh;
}

/* While a catch of this frame runs, or while the exception that left it unwinds, the frame stands before the catch's
 * try block. */
i32 EffectiveState(const Frame &frame, i32 state, void *tag)
{
    const ThreadState &thread = Thread();
    for (u32 i = thread.CatchCount; i > 0; --i) {
        const ActiveCatch &active = thread.Catches[i - 1];
        if (active.Establisher == frame.Establisher && (!active.Leaving || active.Leaving == tag)) {
            return active.State;
        }
    }
    return state;
}

/* Runs the unwind actions from one state down to another along the unwind map's parent links. */
void UnwindTo(const Frame &frame, i32 state, i32 target)
{
    if (state <= target || !frame.Info.UnwindMap) {
        return;
    }
    Reader header(At(frame.ImageBase, frame.Info.UnwindMap));
    const u32 count = header.Compressed();
    const u8 *start = header.Position();
    if (count > MAP_LIMIT || (u32)state >= count) {
        Fatal();
    }
    for (u32 steps = 0; state > target; ++steps) {
        if (steps > count) {
            Fatal();
        }
        Reader reader(start);
        for (i32 i = 0; i < state; ++i) { // the entry of this state
            const u32 kind = reader.Compressed() & 0x3;
            if (kind) {
                (void)reader.Int();
            }
            if (kind == 1 || kind == 2) {
                (void)reader.Compressed();
            }
        }
        const u8 *entry = reader.Position();
        const u32 link = reader.Compressed();
        const u32 kind = link & 0x3;
        const u8 *next = entry - (link >> 2);
        i32 action = 0;
        u32 object = 0;
        if (kind) {
            action = reader.Int();
        }
        if (kind == 1 || kind == 2) {
            object = reader.Compressed();
        }
        i32 nextState = -1;
        if (next >= start) {
            Reader scan(start);
            for (nextState = 0; scan.Position() < next && (u32)nextState < count; ++nextState) {
                const u32 k = scan.Compressed() & 0x3;
                if (k) {
                    (void)scan.Int();
                }
                if (k == 1 || k == 2) {
                    (void)scan.Compressed();
                }
            }
            if (scan.Position() != next || nextState >= state) {
                Fatal();
            }
        }
        state = nextState; // Leave the state before its action runs.
        const u64 code = frame.ImageBase + (u64)(u32)action;
        if (kind == 1) {
            ((void(__cdecl *)(void *))code)((void *)(frame.Base + object));
        } else if (kind == 2) {
            ((void(__cdecl *)(void *))code)(*(void **)(frame.Base + object));
        } else if (kind == 3) {
            ((void(__cdecl *)(u64, u64))code)(frame.Base, frame.Base);
        }
    }
}

bool SameName(const char *a, const char *b)
{
    while (*a && *a == *b) {
        ++a;
        ++b;
    }
    return *a == *b;
}

bool Matches(const Handler &handler, u64 handlerImage, const CatchableType &type, const Thrown &thrown)
{
    if (!handler.Type || (handler.Adjectives & HT_ELLIPSIS)) {
        return true;
    }
    const TypeDescriptor *caught = (const TypeDescriptor *)At(handlerImage, handler.Type);
    if (!caught->Name[0]) {
        return true;
    }
    const TypeDescriptor *raised = (const TypeDescriptor *)At(thrown.ImageBase, type.Type);
    if (caught != raised && !SameName(caught->Name, raised->Name)) {
        return false;
    }
    if ((type.Properties & CT_REFERENCE_ONLY) && !(handler.Adjectives & HT_REFERENCE)) {
        return false;
    }
    const u32 attributes = thrown.Info->Attributes;
    return !((attributes & TI_CONST) && !(handler.Adjectives & HT_CONST)) &&
        !((attributes & TI_VOLATILE) && !(handler.Adjectives & HT_VOLATILE)) &&
        !((attributes & TI_UNALIGNED) && !(handler.Adjectives & HT_UNALIGNED));
}

void *Adjust(void *object, const Displacement &displacement)
{
    u8 *address = (u8 *)object + displacement.Member;
    if (displacement.VirtualBase >= 0) {
        const u8 *table = *(const u8 *const *)((const u8 *)object + displacement.VirtualBase);
        address += *(const i32 *)(table + displacement.VirtualBaseOffset) + displacement.VirtualBase;
    }
    return address;
}

void Copy(void *target, const void *source, i32 size)
{
    for (i32 i = 0; i < size; ++i) {
        ((u8 *)target)[i] = ((const u8 *)source)[i];
    }
}

/* Initializes the catch parameter in the search phase, before the frames in between unwind. */
void BuildCatchObject(const Frame &frame, const Handler &handler, const CatchableType &type, const Thrown &thrown)
{
    if (!handler.Type || !(handler.Header & HF_CATCH_OBJECT)) {
        return;
    }
    u8 *slot = (u8 *)(frame.Base + handler.CatchObject);
    if (handler.Adjectives & HT_REFERENCE) {
        *(void **)slot = Adjust(thrown.Object, type.This);
    } else if (type.Properties & CT_SIMPLE) {
        Copy(slot, thrown.Object, type.Size);
        if (type.Size == sizeof(void *) && *(void **)slot) {
            *(void **)slot = Adjust(*(void **)slot, type.This);
        }
    } else if (!type.Copy) {
        Copy(slot, Adjust(thrown.Object, type.This), type.Size);
    } else if (type.Properties & CT_VIRTUAL_BASE) {
        ((void(__cdecl *)(void *, void *, int))(thrown.ImageBase + (u64)(u32)type.Copy))(
            slot, Adjust(thrown.Object, type.This), 1);
    } else {
        ((void(__cdecl *)(void *, void *))(thrown.ImageBase + (u64)(u32)type.Copy))(
            slot, Adjust(thrown.Object, type.This));
    }
}

/* Ends a catch, normally or while an exception leaves it: this runtime's own handler runs the destructor when it
 * unwinds the consolidation callback. A catch that ends normally drops its record; one that an exception leaves keeps
 * it, marked with that exception, for the frame the unwind passes next. Either destroys its exception object unless
 * the exception leaving is a rethrow of it. */
struct CatchGuard {
    ThreadState &Thread;
    const CatchContext &Context;

    ~CatchGuard()
    {
        u32 at = Thread.CatchCount;
        while (at > 0 &&
            (Thread.Catches[at - 1].Leaving ||
                Thread.Catches[at - 1].Object != Context.Object ||
                Thread.Catches[at - 1].Establisher != Context.Establisher)) {
            --at;
        }
        if (!at) {
            Fatal();
        }
        void *leaving = Thread.Unwinding;
        if (leaving) {
            Thread.Catches[at - 1].Leaving = leaving;
        } else {
            for (u32 i = at; i < Thread.CatchCount; ++i) {
                Thread.Catches[i - 1] = Thread.Catches[i];
            }
            --Thread.CatchCount;
        }
        if (leaving != Context.Object) {
            Destroy(Context.Object, Context.Info, Context.ThrowImage);
        }
    }
};

/* The consolidation callback: runs the catch funclet with the frame of its function and returns where execution
 * continues. */
void *RunCatch(EXCEPTION_RECORD *record)
{
    const CatchContext *context = (const CatchContext *)record->ExceptionInformation[2];
    ThreadState &thread = Thread();
    thread.Unwinding = nullptr;
    ForgetLeft(context->Object); // Its unwind has passed every catch it left.
    if (thread.CatchCount == CATCH_CAPACITY) {
        Fatal();
    }
    thread.Catches[thread.CatchCount++] = {
        context->Establisher, context->State, context->Object, context->Info, context->ThrowImage, nullptr};
    --thread.Uncaught;
    u64 continuation;
    {
        CatchGuard guard{thread, *context};
        continuation = ((u64(__cdecl *)(u64, u64))context->Funclet)(context->Base, context->Base);
    }
    if (context->Continuations) {
        if (continuation >= context->Continuations) {
            Fatal();
        }
        continuation = context->ImageBase + context->Continuation[continuation];
    }
    return (void *)continuation;
}

[[noreturn]] void CatchIt(const Frame &frame, const DISPATCHER_CONTEXT *dispatcher, const TryBlock &block,
    const Handler &handler, const CatchableType &type, const Thrown &thrown)
{
    BuildCatchObject(frame, handler, type, thrown);
    CatchContext context = {frame.Establisher, frame.Base, frame.ImageBase + (u64)(u32)handler.Code, frame.ImageBase,
        block.Low - 1, handler.Continuations, {handler.Continuation[0], handler.Continuation[1]}, thrown.Object,
        thrown.Info, thrown.ImageBase};
    EXCEPTION_RECORD consolidation = {};
    consolidation.ExceptionCode = CONSOLIDATE;
    consolidation.ExceptionFlags = EXCEPTION_NONCONTINUABLE;
    consolidation.NumberParameters = 4;
    consolidation.ExceptionInformation[0] = (ULONG_PTR)&RunCatch;
    consolidation.ExceptionInformation[1] = (ULONG_PTR)WITCXX_MAGIC;
    consolidation.ExceptionInformation[2] = (ULONG_PTR)&context;
    consolidation.ExceptionInformation[3] = (ULONG_PTR)(i64)(block.Low - 1);
    CONTEXT scratch;
    Thread().Unwinding = thrown.Object;
    Unwind(frame.Establisher, dispatcher->ControlPc, &consolidation, &scratch, dispatcher->HistoryTable);
    Fatal();
}

bool ReadHandler(Reader &reader, const Frame &frame, Handler &handler)
{
    handler = {};
    handler.Header = reader.Byte();
    if (handler.Header & HF_ADJECTIVES) {
        handler.Adjectives = reader.Compressed();
    }
    if (handler.Header & HF_TYPE) {
        handler.Type = reader.Int();
    }
    if (handler.Header & HF_CATCH_OBJECT) {
        handler.CatchObject = reader.Compressed();
    }
    handler.Code = reader.Int();
    handler.Continuations = (handler.Header >> 4) & 0x3;
    if (handler.Continuations == 3) {
        return false;
    }
    for (u32 i = 0; i < handler.Continuations; ++i) {
        handler.Continuation[i] =
            (handler.Header & HF_CONTINUATION_RVA) ? (u64)(u32)reader.Int() : (u64)frame.Start + reader.Compressed();
    }
    return true;
}

/* Searches this frame's try blocks around the state, inner ones first, for a catch of the thrown type. A catch funclet
 * has its own try and unwind maps, with the try blocks inside its catch only. */
void Search(const Frame &frame, const DISPATCHER_CONTEXT *dispatcher, const Thrown &thrown)
{
    if (!frame.Info.TryMap) {
        return;
    }
    const i32 state = EffectiveState(frame, StateAt(frame, dispatcher->ControlPc), thrown.Object);
    Reader reader(At(frame.ImageBase, frame.Info.TryMap));
    const u32 count = reader.Compressed();
    const CatchableTypeArray *types = (const CatchableTypeArray *)At(thrown.ImageBase, thrown.Info->Catchables);
    for (u32 i = 0; i < count && i < MAP_LIMIT; ++i) {
        TryBlock block;
        if (!ReadTry(reader, block)) {
            Fatal();
        }
        if (state < block.Low || state > block.High) {
            continue;
        }
        Reader handlers(At(frame.ImageBase, block.Handlers));
        const u32 handlerCount = handlers.Compressed();
        for (u32 h = 0; h < handlerCount && h < MAP_LIMIT; ++h) {
            Handler handler;
            if (!ReadHandler(handlers, frame, handler)) {
                Fatal();
            }
            for (i32 t = 0; t < types->Count; ++t) {
                const CatchableType &type = *(const CatchableType *)At(thrown.ImageBase, types->Types[t]);
                if (Matches(handler, frame.ImageBase, type, thrown)) {
                    CatchIt(frame, dispatcher, block, handler, type, thrown);
                }
            }
        }
    }
}

bool ThrownOf(const EXCEPTION_RECORD *record, Thrown &thrown)
{
    if (record->ExceptionCode != CXX_EXCEPTION || record->NumberParameters != 4) {
        return false;
    }
    const u64 magic = record->ExceptionInformation[0];
    if (magic != MAGIC1 && magic != MAGIC2 && magic != MAGIC3) {
        return false;
    }
    thrown = {(void *)record->ExceptionInformation[1], (const ThrowInfo *)record->ExceptionInformation[2],
        (u64)record->ExceptionInformation[3]};
    return thrown.Info != nullptr;
}

} // namespace

} // namespace WitCxx

using namespace WitCxx;

extern "C" EXCEPTION_DISPOSITION __cdecl __CxxFrameHandler4(
    EXCEPTION_RECORD *record, void *establisher, CONTEXT *context, DISPATCHER_CONTEXT *dispatcher)
{
    (void)context;
    Frame frame;
    Decode(dispatcher, (u64)establisher, frame);
    if (record->ExceptionFlags & EXCEPTION_UNWINDING) {
        void *tag = nullptr;
        if (record->ExceptionCode == CONSOLIDATE &&
            record->NumberParameters == 4 &&
            record->ExceptionInformation[1] == WITCXX_MAGIC) {
            tag = ((const CatchContext *)record->ExceptionInformation[2])->Object;
        }
        const i32 state = EffectiveState(frame, StateAt(frame, dispatcher->ControlPc), tag);
        i32 target = -1;
        if ((record->ExceptionFlags & EXCEPTION_TARGET_UNWIND) &&
            record->ExceptionCode == CONSOLIDATE &&
            record->NumberParameters == 4 &&
            record->ExceptionInformation[1] == WITCXX_MAGIC) {
            target = (i32)(i64)record->ExceptionInformation[3];
        } else if (record->ExceptionFlags & EXCEPTION_TARGET_UNWIND) {
            target = StateAt(frame, dispatcher->TargetIp);
        }
        UnwindTo(frame, state, target);
        return ExceptionContinueSearch;
    }
    Thrown thrown;
    if (ThrownOf(record, thrown)) {
        Search(frame, dispatcher, thrown);
        if (frame.Info.Header & FI_NOEXCEPT) {
            Fatal(); // The exception would leave a noexcept function: std::terminate.
        }
    }
    return ExceptionContinueSearch;
}
