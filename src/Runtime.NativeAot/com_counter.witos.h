#ifndef WITOS_COM_COUNTER_H
#define WITOS_COM_COUNTER_H
#include <stdint.h>
// Shared checked counter primitive; boundary tests use a local counter only.
static inline bool wit_com_add_reference(uint32_t& count)
{
    if(count==UINT32_MAX)return false;
    ++count;return true;
}
#endif
