#include "witos/arch.h"
#include "witos/a64_board.h"
#include "witos/platform.h"
#include "arm.h"

/* The console and the end of a boot. The console is the UART the device tree names (H7.2): /chosen's stdout-path, or
 * the alias serial0 where the tree names none (the Raspberry Pi firmware's tree, whose serial0 is the UART on the GPIO
 * header's pins 8 and 10), either an alias or a path; a PL011 (QEMU's virt, the Pi's uart0) or the Pi's mini UART
 * (brcm,bcm2835-aux-uart, 16550-like). The boot paths hand the tree over before their first line or as soon as they
 * have it; until then the console is the virt board's PL011, and a tree that names no UART the platform drives leaves
 * the console silent rather than writing where nothing is known. The firmware has set the line up: no register of
 * either UART's line is written. The test exit is Arm semihosting's. */

#define PL011_DATA 0x000U
#define PL011_FLAGS 0x018U
#define PL011_TRANSMIT_FULL 0x20U
#define MINI_UART_IO 0x00U
#define MINI_UART_LSR 0x14U
#define MINI_UART_TRANSMIT_EMPTY 0x20U /* the transmit FIFO takes a byte */
#define SERIAL_POLL_LIMIT 1000000U
#define PATH_CAPACITY 128U

#define UART_NONE 0U
#define UART_PL011 1U
#define UART_MINI 2U

#define SEMIHOSTING_EXIT 0x18U
#define ADP_STOPPED_APPLICATION_EXIT 0x20026U

static WitU64 uart = WIT_ARM_PL011_BASE;
static WitU32 uart_kind = UART_PL011;

static volatile WitU32 *uart_register(WitU32 offset)
{
    return (volatile WitU32 *)(uart + offset);
}

void wit_console_initialize(void)
{
    /* The firmware has enabled the UART for its own console; QEMU ignores the line settings. */
}

/* A property's string up to its options (stdout-path's ":115200n8") or its end, copied with its terminator. */
static WitU32 copy_name(const WitArmTreeItem *item, char *out, WitU32 capacity)
{
    WitU32 length = 0;
    while (length < item->Length && length + 1 < capacity && item->Value[length] && item->Value[length] != ':') {
        out[length] = (char)item->Value[length];
        ++length;
    }
    out[length] = 0;
    return length;
}

void wit_platform_console_tree(WitU64 address)
{
    WitArmTree tree;
    WitArmTreeItem item;
    char path[PATH_CAPACITY], alias[PATH_CAPACITY];
    WitU32 length = 0, depth, kind = UART_NONE;
    WitU64 base = 0, size = 0;
    if (!address) {
        return;
    }
    if (!wit_arm_tree_open_early(address, &tree)) {
        return;
    }
    uart_kind = UART_NONE; /* a tree was given: the console is what it names */
    if ((depth = wit_arm_tree_seek(&tree, "/chosen", 7)) != 0 &&
        wit_arm_tree_property(&tree, depth, "stdout-path", &item)) {
        length = copy_name(&item, path, PATH_CAPACITY);
    }
    if (length == 0) {
        path[0] = 's', path[1] = 'e', path[2] = 'r', path[3] = 'i', path[4] = 'a', path[5] = 'l', path[6] = '0';
        path[7] = 0;
        length = 7;
    }
    if (path[0] != '/') {
        for (WitU32 i = 0; i <= length; ++i) {
            alias[i] = path[i];
        }
        length = 0;
        if (wit_arm_tree_open_early(address, &tree) &&
            (depth = wit_arm_tree_seek(&tree, "/aliases", 8)) != 0 &&
            wit_arm_tree_property(&tree, depth, alias, &item)) {
            length = copy_name(&item, path, PATH_CAPACITY);
        }
    }
    if (length == 0 ||
        path[0] != '/' ||
        !wit_arm_tree_open_early(address, &tree) ||
        (depth = wit_arm_tree_seek(&tree, path, length)) == 0) {
        return;
    }
    while (wit_arm_tree_next(&tree, &item) && item.Kind == WIT_ARM_TREE_PROPERTY && item.Depth == depth) {
        if (wit_arm_same(item.Name, "compatible")) {
            kind = wit_arm_tree_names(&item, "arm,pl011")            ? UART_PL011
                : wit_arm_tree_names(&item, "brcm,bcm2835-aux-uart") ? UART_MINI
                                                                     : UART_NONE;
        } else if (wit_arm_same(item.Name, "reg") && !wit_arm_tree_reg(&tree, &item, 0, &base, &size)) {
            base = 0;
        }
    }
    if (kind != UART_NONE && base != 0) {
        uart = base;
        uart_kind = kind;
    }
}

void wit_platform_console_put(WitU8 value)
{
    if (uart_kind == UART_NONE) {
        return;
    }
    for (WitU32 spin = 0; spin < SERIAL_POLL_LIMIT; ++spin) {
        if (uart_kind == UART_PL011 && (*uart_register(PL011_FLAGS) & PL011_TRANSMIT_FULL) == 0) {
            *uart_register(PL011_DATA) = value;
            return;
        }
        if (uart_kind == UART_MINI && (*uart_register(MINI_UART_LSR) & MINI_UART_TRANSMIT_EMPTY) != 0) {
            *uart_register(MINI_UART_IO) = value;
            return;
        }
    }
    /* A missing/broken serial device must not hang panic or shutdown. */
}

WitU32 wit_platform_boot_devices(WitU64 *pages, WitU32 capacity)
{
    /* The console stays usable across the switch to the kernel's translation tables. */
    if (capacity == 0 || uart_kind == UART_NONE) {
        return 0;
    }
    pages[0] = uart & ~4095ULL;
    return 1;
}

/* The console, once paging is active (H7.2). */
void wit_arm_console_describe(void)
{
    wit_console_write(uart_kind == UART_PL011 ? "Console: PL011 at " : "Console: mini UART at ");
    wit_console_write_hex(uart);
    wit_console_write("\n");
}

WIT_NORETURN void wit_platform_finish(WitU32 code)
{
    /* The host sees the same status as q35's isa-debug-exit: guest 0x10 -> 33, guest 0x11 -> 35.
     * Without semihosting the call traps or returns, and the processor stays halted. */
    const WitU64 block[2] = {ADP_STOPPED_APPLICATION_EXIT, ((WitU64)code << 1) | 1};
    wit_arm_semihosting(SEMIHOSTING_EXIT, block);
    if (code == 0x10) {
        /* Past the test exit, a success turns the board off (ADR 0029) where the board has PSCI. */
        WitU64 result = 0;
        wit_console_write("[POWER] off through PSCI SYSTEM_OFF\n");
        if (wit_arm_psci(WIT_PSCI_SYSTEM_OFF, 0, 0, 0, &result)) {
            wit_console_write("[POWER] PSCI SYSTEM_OFF returned; halted\n");
        } else {
            wit_console_write("[POWER] no PSCI on this board; halted\n");
        }
    }
    wit_arch_halt();
}

void wit_platform_reset(void)
{
    WitU64 result = 0;
    wit_console_write("[POWER] reset through PSCI SYSTEM_RESET\n");
    if (!wit_arm_psci(WIT_PSCI_SYSTEM_RESET, 0, 0, 0, &result)) {
        wit_console_write("[POWER] no PSCI on this board\n");
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
