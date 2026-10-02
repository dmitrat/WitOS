#include "witos/types.h"
extern __declspec(thread) WitU64 primitive;

extern "C" __declspec(noinline) WitU64 wit_dynamic_primitive(void)
{
    return primitive;
}
