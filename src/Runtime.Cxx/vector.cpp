/* The compiler's array helpers (P6.4.g): `eh vector constructor iterator' builds the elements of an array and, when a
 * constructor throws, destroys the ones it built, newest first; `eh vector destructor iterator' destroys an array
 * newest first and, when a destructor throws, still destroys the rest. A second exception during that cleanup
 * terminates. Their decorated names cannot be spelled in C++, so the linker maps them to these functions. */
typedef void(__cdecl *ObjectFunction)(void *);

namespace {

/* noexcept: an exception from a destructor while another one unwinds terminates. */
void DestroyRange(char *end, size_t size, size_t count, ObjectFunction destructor) noexcept
{
    while (count--) {
        end -= size;
        destructor(end);
    }
}

struct Built {
    char *Array;
    size_t Size, Count;
    ObjectFunction Destructor;
    bool Done;

    ~Built()
    {
        if (!Done) {
            DestroyRange(Array + Size * Count, Size, Count, Destructor);
        }
    }
};

struct Remaining {
    char *&End;
    size_t Size;
    size_t &Count;
    ObjectFunction Destructor;
    bool Done;

    ~Remaining()
    {
        if (!Done) {
            DestroyRange(End, Size, Count, Destructor);
        }
    }
};

} // namespace

/* noexcept(false): under /EHsc the compiler takes extern "C" functions for non-throwing and would drop the cleanup. */
extern "C" void __cdecl wit_cxx_vector_construct(
    void *array, size_t size, size_t count, ObjectFunction constructor, ObjectFunction destructor) noexcept(false)
{
    Built built = {(char *)array, size, 0, destructor, false};
    for (; built.Count < count; ++built.Count) {
        constructor(built.Array + size * built.Count);
    }
    built.Done = true;
}

extern "C" void __cdecl wit_cxx_vector_destroy(
    void *array, size_t size, size_t count, ObjectFunction destructor) noexcept(false)
{
    char *end = (char *)array + size * count;
    size_t left = count;
    Remaining remaining = {end, size, left, destructor, false};
    while (left) {
        end -= size;
        --left;
        destructor(end);
    }
    remaining.Done = true;
}

#pragma comment(linker, "/alternatename:??_L@YAXPEAX_K1P6AX0@Z2@Z=wit_cxx_vector_construct")
#pragma comment(linker, "/alternatename:??_M@YAXPEAX_K1P6AX0@Z@Z=wit_cxx_vector_destroy")
