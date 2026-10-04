#include <vcruntime_typeinfo.h>

/* type_info's virtual destructor is its key function: defining it here emits the vtable that every type descriptor
 * of the C++ exception data refers to (P6.4.e). */
type_info::~type_info() noexcept {}
