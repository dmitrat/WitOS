#include "gcenv.witos.h"

/* WitOS has flat CPU indices and no Windows processor groups. This matches
 * upstream's Unix entry policy while retaining upstream parsing semantics.
 * Parsing a set is not applying affinity or discovering online CPUs. */
bool GCToOSInterface::ParseGCHeapAffinitizeRangesEntry(const char **text, size_t *first, size_t *last)
{
    return ParseIndexOrRange(text, first, last);
}
