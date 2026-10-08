/* The library the dynamic program needs (plan step S5.3, lib/libdynamic.so, DT_NEEDED of tests/User/dynamic_main.c):
 * musl's dynamic linker maps it from the package before the program starts, binds the program's references to its
 * data and functions, runs its constructor and gives every thread its thread-local variable in the static TLS
 * block. */

int dynamic_counter = 40;
_Thread_local int dynamic_tls = 5;
static int constructed;

__attribute__((constructor)) static void construct(void)
{
    constructed = 1;
}

int dynamic_constructed(void)
{
    return constructed;
}

int dynamic_answer(int value)
{
    return value + dynamic_counter;
}

int *dynamic_tls_address(void)
{
    return &dynamic_tls;
}
