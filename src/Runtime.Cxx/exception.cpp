#include <new>
#include <vcruntime_exception.h>

/* std::exception's message storage (P6.4.g): an owned message is copied into the nothrow allocator, a borrowed one is
 * shared. A failed copy leaves no message, and what() then reports an unknown exception. */
extern "C" void __cdecl __std_exception_copy(const __std_exception_data *from, __std_exception_data *to)
{
    if (!from->_DoFree || !from->_What) {
        to->_What = from->_What;
        to->_DoFree = false;
        return;
    }
    size_t length = 0;
    while (from->_What[length]) {
        ++length;
    }
    char *copy = (char *)operator new(length + 1, std::nothrow);
    if (!copy) {
        return;
    }
    for (size_t i = 0; i <= length; ++i) {
        copy[i] = from->_What[i];
    }
    to->_What = copy;
    to->_DoFree = true;
}

extern "C" void __cdecl __std_exception_destroy(__std_exception_data *data)
{
    if (data->_DoFree) {
        operator delete((void *)data->_What);
    }
    data->_What = nullptr;
    data->_DoFree = false;
}
