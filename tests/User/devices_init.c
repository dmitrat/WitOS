#include <stdio.h>

/* /bin/init of the runtime-devices scenario (plan step D1.1, ADR 0025): the root task starts it only after the device
 * manager, /system/DeviceManager, answered that its devices are discovered, so its one line follows every discovery
 * line in the log. */

int main(void)
{
    printf("[DEVICES-INIT] /bin/init started after the device manager answered\n");
    return 0;
}
