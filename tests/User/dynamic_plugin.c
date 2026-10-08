/* The library the dynamic program opens with dlopen (plan step S5.3, lib/libplugin.so): a TLS module loaded after the
 * process started, whose thread-local variable every thread reaches through __tls_get_addr, the existing ones through
 * the TLS musl's dlopen installs for them. */

int plugin_value = 100;
_Thread_local int plugin_tls = 11;

int plugin_tls_add(int value)
{
    plugin_tls += value;
    return plugin_tls;
}
