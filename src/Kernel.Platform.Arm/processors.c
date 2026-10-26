#include "witos/arch.h"
#include "witos/a64_board.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "witos/processor.h"
#include "arm.h"

/* The processors of an ARM64 board (plan steps K7.1, H7.2): the children of /cpus whose device_type is "cpu", each
 * with its reg (the MPIDR affinity in one or two cells, as the parent's #address-cells says), a status other than "okay"
 * that marks it unusable, and how it starts — its enable-method, or /cpus's where it names none: "psci" through the
 * conduit /psci's method names (QEMU's virt: HVC; with an EL2: SMC), or "spin-table", whose processor waits at its
 * cpu-release-addr for the entry's address (the Raspberry Pi 4's firmware and QEMU's raspi4b). The same conduit turns
 * the board off and resets it (ADR 0029). */

#define START_NONE 0U
#define START_PSCI 1U
#define START_SPIN 2U
#define CONDUIT_NONE 0U
#define CONDUIT_HVC 1U
#define CONDUIT_SMC 2U

typedef struct ArmProcessor {
    WitU64 HardwareId, Release;
    WitU32 Start, Enabled;
} ArmProcessor;

static ArmProcessor processors[WIT_PROCESSOR_CAPACITY];
static WitU32 processor_count;
static WitU32 conduit = CONDUIT_HVC; /* the virt board's until the tree is read */
static const WitBootInfo *boot_info;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static WitU32 start_method(const WitArmTreeItem *item)
{
    return wit_arm_tree_names(item, "psci")      ? START_PSCI
        : wit_arm_tree_names(item, "spin-table") ? START_SPIN
                                                 : START_NONE;
}

static void read_processors(const WitBootInfo *boot)
{
    WitArmTree tree;
    WitArmTreeItem item;
    WitU32 address_cells = 1, start_default = START_NONE;
    int in_cpus = 0, in_psci = 0, is_cpu = 0, has_reg = 0;
    ArmProcessor processor = {0};
    boot_info = boot;
    processor_count = 0;
    if (!wit_arm_tree_open(boot, &tree)) {
        return;
    }
    conduit = CONDUIT_NONE;
    while (wit_arm_tree_next(&tree, &item)) {
        const WitU32 depth = item.Depth;
        if (item.Kind == WIT_ARM_TREE_BEGIN) {
            if (depth == 2) { /* the root node is depth 1 */
                in_cpus = wit_arm_same(item.Name, "cpus");
                in_psci = wit_arm_same(item.Name, "psci");
            } else if (depth == 3 && in_cpus) {
                processor = (ArmProcessor){0, 0, START_NONE, 1};
                is_cpu = 0;
                has_reg = 0;
            }
        } else if (item.Kind == WIT_ARM_TREE_END) {
            if (depth == 3 && in_cpus && is_cpu && has_reg && processor_count < WIT_PROCESSOR_CAPACITY) {
                if (processor.Start == START_NONE) {
                    processor.Start = start_default;
                }
                processors[processor_count++] = processor;
            }
            if (depth == 2) {
                in_cpus = 0;
                in_psci = 0;
            }
        } else if (item.Kind == WIT_ARM_TREE_PROPERTY) {
            const char *name = item.Name;
            const WitU8 *value = item.Value;
            const WitU32 length = item.Length;
            if (in_psci && depth == 2 && wit_arm_same(name, "method")) {
                conduit = wit_arm_tree_names(&item, "hvc") ? CONDUIT_HVC
                    : wit_arm_tree_names(&item, "smc")     ? CONDUIT_SMC
                                                           : CONDUIT_NONE;
            }
            if (!in_cpus) {
                continue;
            }
            if (depth == 2 && wit_arm_same(name, "#address-cells") && length >= 4) {
                address_cells = wit_arm_be32(value);
            } else if (depth == 2 && wit_arm_same(name, "enable-method")) {
                start_default = start_method(&item);
            } else if (depth == 3 && wit_arm_same(name, "device_type") && length >= 4) {
                is_cpu = wit_arm_same((const char *)value, "cpu");
            } else if (depth == 3 && wit_arm_same(name, "reg") && length >= address_cells * 4 && address_cells <= 2) {
                processor.HardwareId = address_cells == 2 ? wit_arm_be64(value) : wit_arm_be32(value);
                has_reg = 1;
            } else if (depth == 3 && wit_arm_same(name, "status") && length >= 5) {
                processor.Enabled = wit_arm_same((const char *)value, "okay");
            } else if (depth == 3 && wit_arm_same(name, "enable-method")) {
                processor.Start = start_method(&item);
            } else if (depth == 3 && wit_arm_same(name, "cpu-release-addr") && (length == 8 || length == 4)) {
                processor.Release = length == 8 ? wit_arm_be64(value) : wit_arm_be32(value);
            }
        }
    }
}

/* How the board starts its processors (H7.2), printed once with their enumeration. */
static void describe(void)
{
    WitU32 psci = 0, spin = 0;
    for (WitU32 i = 0; i < processor_count; ++i) {
        psci += processors[i].Start == START_PSCI;
        spin += processors[i].Start == START_SPIN;
    }
    wit_console_write("Processor start: ");
    wit_console_write_u64(psci);
    wit_console_write(" by PSCI, ");
    wit_console_write_u64(spin);
    wit_console_write(" by spin table; PSCI conduit ");
    wit_console_write(conduit == CONDUIT_HVC ? "HVC" : conduit == CONDUIT_SMC ? "SMC" : "none");
    wit_console_write("\n");
}

/* The processors in the tree's order (K7.1). */
WitU32 wit_platform_processors(const WitBootInfo *boot, WitProcessorDescriptor *out, WitU32 capacity)
{
    WitU32 count = 0;
    read_processors(boot);
    for (; count < processor_count && count < capacity; ++count) {
        out[count].HardwareId = processors[count].HardwareId;
        out[count].Enabled = processors[count].Enabled;
        out[count].Reserved = 0;
    }
    describe();
    return count;
}

int wit_arm_psci_available(void)
{
    return conduit != CONDUIT_NONE;
}

int wit_arm_psci(WitU64 function, WitU64 a1, WitU64 a2, WitU64 a3, WitU64 *result)
{
    if (conduit == CONDUIT_NONE) {
        return 0;
    }
    *result = conduit == CONDUIT_SMC ? wit_a64_psci_smc(function, a1, a2, a3) : wit_a64_psci_hvc(function, a1, a2, a3);
    return 1;
}

/* A prepared processor's start (H7.2): PSCI's CPU_ON at the entry that takes its number as the context, or its spin
 * table's release word, written with the spin entry's address before the event that wakes it. */
int wit_a64_board_processor_start(WitU32 index, WitU64 hardware_id)
{
    WitU64 result = 0;
    for (WitU32 i = 0; i < processor_count; ++i) {
        const ArmProcessor *processor = &processors[i];
        if (processor->HardwareId != hardware_id) {
            continue;
        }
        if (processor->Start == START_PSCI) {
            return wit_arm_psci(WIT_PSCI_CPU_ON, hardware_id, (WitU64)wit_a64_secondary_entry, index, &result) &&
                result == 0;
        }
        if (processor->Start == START_SPIN) {
            require(processor->Release != 0, "A spin table without its release address");
            wit_a64_write_physical(boot_info, processor->Release, (WitU64)wit_a64_secondary_spin_entry);
            wit_a64_send_event();
            return 1;
        }
        return 0;
    }
    return 0;
}
