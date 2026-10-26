#include "witos/arch.h"
#include "witos/a64_board.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "arm.h"

/* The end of a boot on an ARM64 board (ADR 0029, plan step H7.3). The test exit is Arm semihosting's where the board
 * has it: QEMU's virt, whose tree's root is compatible with linux,dummy-virt, and any board whose /chosen bootargs name
 * witos.test-exit, as the runner writes them for raspi4b. Anywhere else HLT #0xF000 is an undefined instruction, since
 * no debugger halts the processor. Past the test exit, or without it, a success turns the board off — through PSCI
 * where the tree names a conduit, through the Raspberry Pi's watchdog (brcm,bcm2835-pm-wdt) otherwise — and a failure
 * halts. A reset the root task asks for takes the same ways. */

#define SEMIHOSTING_EXIT 0x18U
#define ADP_STOPPED_APPLICATION_EXIT 0x20026U

/* The power management block's watchdog, as Linux's bcm2835_wdt drives it: a reset after a few ticks, and the halt
 * the firmware recognizes by partition 63 in PM_RSTS (QEMU's raspi4b turns the machine off for it). */
#define PM_RSTC 0x1CU
#define PM_RSTS 0x20U
#define PM_WDOG 0x24U
#define PM_PASSWORD 0x5A000000U
#define PM_RSTC_WRCFG_CLR 0xFFFFFFCFU
#define PM_RSTC_WRCFG_FULL_RESET 0x00000020U
#define PM_RSTS_HALT 0x00000555U /* partition 63, its bits spread over every other bit */
#define PM_WDOG_TICKS 10U /* about 150 microseconds */
#define RESET_WAIT_MILLISECONDS 100U

static int test_exit = 1; /* QEMU's virt until a tree says otherwise */
static WitU64 watchdog;

static int has_token(const WitArmTreeItem *item, const char *token)
{
    for (WitU32 i = 0; i < item->Length && item->Value[i];) {
        WitU32 k = 0;
        while (i < item->Length && item->Value[i] == ' ') {
            ++i;
        }
        while (i + k < item->Length && item->Value[i + k] && token[k] && item->Value[i + k] == token[k]) {
            ++k;
        }
        if (token[k] == 0 && (i + k == item->Length || item->Value[i + k] == 0 || item->Value[i + k] == ' ')) {
            return 1;
        }
        while (i < item->Length && item->Value[i] && item->Value[i] != ' ') {
            ++i;
        }
    }
    return 0;
}

void wit_arm_power_tree(WitU64 address)
{
    WitArmTree tree;
    WitArmTreeItem item;
    WitU32 depth;
    int virt = 0, asked = 0;
    if (wit_arm_tree_open_early(address, &tree) &&
        (depth = wit_arm_tree_seek(&tree, "/", 1)) != 0 &&
        wit_arm_tree_property(&tree, depth, "compatible", &item)) {
        virt = wit_arm_tree_names(&item, "linux,dummy-virt");
    }
    if (wit_arm_tree_open_early(address, &tree) &&
        (depth = wit_arm_tree_seek(&tree, "/chosen", 7)) != 0 &&
        wit_arm_tree_property(&tree, depth, "bootargs", &item)) {
        asked = has_token(&item, "witos.test-exit");
    }
    test_exit = virt || asked;
}

void wit_arm_power_map(const WitBootInfo *boot)
{
    watchdog = wit_arm_board(boot)->Watchdog;
    if (watchdog) {
        wit_arch_map_device_page(boot, watchdog & ~4095ULL);
    }
}

static volatile WitU32 *pm(WitU32 offset)
{
    return (volatile WitU32 *)(watchdog + offset);
}

static void watchdog_reset(void)
{
    *pm(PM_WDOG) = PM_PASSWORD | PM_WDOG_TICKS;
    *pm(PM_RSTC) = PM_PASSWORD | (*pm(PM_RSTC) & PM_RSTC_WRCFG_CLR) | PM_RSTC_WRCFG_FULL_RESET;
}

/* The watchdog acts a few ticks after its write; a board that has not reset by then cannot. */
static void wait_for_reset(void)
{
    const WitU64 frequency = wit_platform_monotonic_frequency();
    const WitU64 start = wit_platform_monotonic_read();
    while (frequency && wit_platform_monotonic_read() - start < frequency / 1000 * RESET_WAIT_MILLISECONDS) {
    }
}

WIT_NORETURN void wit_platform_finish(WitU32 code)
{
    WitU64 result = 0;
    if (test_exit) {
        /* The host sees the same status as q35's isa-debug-exit: guest 0x10 -> 33, guest 0x11 -> 35. */
        const WitU64 block[2] = {ADP_STOPPED_APPLICATION_EXIT, ((WitU64)code << 1) | 1};
        wit_arm_semihosting(SEMIHOSTING_EXIT, block);
    }
    if (code == 0x10) {
        /* Past the test exit, a success turns the board off (ADR 0029). */
        if (wit_arm_psci_available()) {
            wit_console_write("[POWER] off through PSCI SYSTEM_OFF\n");
            wit_arm_psci(WIT_PSCI_SYSTEM_OFF, 0, 0, 0, &result);
            wit_console_write("[POWER] PSCI SYSTEM_OFF returned; halted\n");
        } else if (watchdog) {
            wit_console_write("[POWER] off through the watchdog\n");
            *pm(PM_RSTS) = PM_PASSWORD | *pm(PM_RSTS) | PM_RSTS_HALT;
            watchdog_reset();
            wait_for_reset();
            wit_console_write("[POWER] the watchdog did not turn the board off; halted\n");
        } else {
            wit_console_write("[POWER] no means to turn this board off; halted\n");
        }
    }
    wit_arch_halt();
}

void wit_platform_reset(void)
{
    WitU64 result = 0;
    if (wit_arm_psci_available()) {
        wit_console_write("[POWER] reset through PSCI SYSTEM_RESET\n");
        wit_arm_psci(WIT_PSCI_SYSTEM_RESET, 0, 0, 0, &result);
    } else if (watchdog) {
        wit_console_write("[POWER] reset through the watchdog\n");
        watchdog_reset();
        wait_for_reset();
    } else {
        wit_console_write("[POWER] no means to reset this board\n");
    }
}

/* No message interrupts on this board (ADR 0029, H1.2b): INTERRUPT_BIND_MESSAGE answers UNSUPPORTED and QUERY clears
 * the feature; the GICv3 ITS (ARM64) and the IMSIC (riscv64) wait for a step that needs them. */
int wit_platform_messages_supported(void)
{
    return 0;
}

int wit_platform_message_allocate(WitU64 *address, WitU32 *data, WitU32 *token)
{
    (void)address;
    (void)data;
    (void)token;
    return 0;
}

void wit_platform_message_free(WitU32 token)
{
    (void)token;
    wit_panic("This board binds no message target");
}
