#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "arm.h"

/* The firmware's framebuffer of a board booted by its device tree (plan step H7.3), for the boot contract the Linux
 * entry builds, as the UEFI loader records the graphics output protocol's (C2.1). The tree names it one of two ways:
 *
 * - a simple-framebuffer node: its reg, width, height, stride in bytes and format, set up by the firmware;
 * - the Raspberry Pi firmware's framebuffer (brcm,bcm2708-fb), which the firmware allocates on request through the
 *   VideoCore's mailbox (brcm,bcm2835-mbox) and its property channel: the display's size, 32 bits in BGR order, the
 *   buffer and its pitch, as Linux's and U-Boot's drivers ask for it.
 *
 * The mailbox reads its message from memory as the bus sees it, through the dma-ranges of the mailbox's bus, so the
 * message is cleaned from the cache before the request and invalidated before the answer is read. The answer's base is
 * a bus address the same window translates, or a physical one (QEMU's raspi4b answers so). Nothing is drawn: the
 * display service draws once the platform publishes the framebuffer as a device. */

#define MAILBOX_READ 0x00U
#define MAILBOX_READ_STATUS 0x18U
#define MAILBOX_WRITE 0x20U
#define MAILBOX_WRITE_STATUS 0x38U
#define MAILBOX_FULL 0x80000000U
#define MAILBOX_EMPTY 0x40000000U
#define MAILBOX_PROPERTY_CHANNEL 8U
#define MAILBOX_POLL_LIMIT 10000000U
#define PROPERTY_SUCCESS 0x80000000U
#define TAG_GET_PHYSICAL_SIZE 0x00040003U
#define TAG_SET_PHYSICAL_SIZE 0x00048003U
#define TAG_SET_VIRTUAL_SIZE 0x00048004U
#define TAG_SET_DEPTH 0x00048005U
#define TAG_SET_PIXEL_ORDER 0x00048006U
#define TAG_ALLOCATE 0x00040001U
#define TAG_GET_PITCH 0x00040008U
#define PIXEL_ORDER_BGR 0U
#define DEFAULT_WIDTH 800U /* where the firmware reports no display size */
#define DEFAULT_HEIGHT 600U
#define MESSAGE_WORDS 36U

#define SOURCE_NONE 0U
#define SOURCE_SIMPLE 1U
#define SOURCE_FIRMWARE 2U

static volatile WitU32 message[MESSAGE_WORDS] __attribute__((aligned(64)));

/* A memory address as a device of the bus sees it, through the bus's first dma-ranges window; the identity without one. */
typedef struct DmaWindow {
    WitU64 Bus, Physical, Bytes;
} DmaWindow;

static WitU64 cells(const WitU8 *p, WitU32 count)
{
    return count == 2 ? wit_arm_be64(p) : count == 1 ? wit_arm_be32(p) : 0;
}

static DmaWindow dma_window(const WitArmTree *tree, WitU32 depth)
{
    DmaWindow window = {0, 0, 0};
    if (depth < 3) {
        return window;
    }
    const WitArmTreeNode *bus = &tree->Nodes[depth - 1], *above = &tree->Nodes[depth - 2];
    const WitU32 child = bus->AddressCells, parent = above->AddressCells, size = bus->SizeCells;
    if (bus->DmaRanges &&
        child &&
        child <= 2 &&
        parent &&
        parent <= 2 &&
        size &&
        size <= 2 &&
        bus->DmaRangesLength >= (child + parent + size) * 4) {
        window.Bus = cells(bus->DmaRanges, child);
        window.Physical = cells(bus->DmaRanges + child * 4, parent);
        window.Bytes = cells(bus->DmaRanges + (child + parent) * 4, size);
    }
    return window;
}

static volatile WitU32 *mailbox_register(WitU64 mailbox, WitU32 offset)
{
    return (volatile WitU32 *)(mailbox + offset);
}

/* One request of the property channel: the message's words are its tags; nonzero when the firmware answered it. */
static int property_call(WitU64 mailbox, const DmaWindow *window, WitU32 words)
{
    const WitU64 address = (WitU64)message;
    WitU64 bus = address;
    WitU32 spin = 0, answer = 0;
    if (window->Bytes) {
        if (address < window->Physical || address - window->Physical >= window->Bytes) {
            return 0;
        }
        bus = address - window->Physical + window->Bus;
    }
    if (bus > 0xFFFFFFFFULL) {
        return 0;
    }
    message[0] = words * 4;
    message[1] = 0;
    wit_arm_clean_invalidate(address, sizeof(message));
    while ((*mailbox_register(mailbox, MAILBOX_WRITE_STATUS) & MAILBOX_FULL) && ++spin < MAILBOX_POLL_LIMIT) {
    }
    *mailbox_register(mailbox, MAILBOX_WRITE) = ((WitU32)bus & ~15U) | MAILBOX_PROPERTY_CHANNEL;
    for (spin = 0; spin < MAILBOX_POLL_LIMIT; ++spin) {
        if (*mailbox_register(mailbox, MAILBOX_READ_STATUS) & MAILBOX_EMPTY) {
            continue;
        }
        answer = *mailbox_register(mailbox, MAILBOX_READ);
        if ((answer & 15U) == MAILBOX_PROPERTY_CHANNEL) {
            break;
        }
    }
    wit_arm_clean_invalidate(address, sizeof(message));
    return spin < MAILBOX_POLL_LIMIT && message[1] == PROPERTY_SUCCESS;
}

static WitU32 tag(WitU32 at, WitU32 identifier, WitU32 values, WitU32 first, WitU32 second)
{
    message[at] = identifier;
    message[at + 1] = values * 4;
    message[at + 2] = 0;
    message[at + 3] = first;
    message[at + 4] = second;
    return at + 3 + values;
}

static void report(const WitBootInfo *boot, const char *source)
{
    wit_console_write("[BOOT] Framebuffer: ");
    wit_console_write_u64(boot->FramebufferWidth);
    wit_console_write("x");
    wit_console_write_u64(boot->FramebufferHeight);
    wit_console_write(", stride ");
    wit_console_write_u64(boot->FramebufferStride);
    wit_console_write(boot->FramebufferFormat == WIT_PIXEL_BGRX ? ", BGRX at " : ", RGBX at ");
    wit_console_write_hex(boot->FramebufferBase);
    wit_console_write(" of ");
    wit_console_write_u64(boot->FramebufferBytes);
    wit_console_write(" bytes, ");
    wit_console_write(source);
    wit_console_write("\n");
}

/* The framebuffer the firmware allocates through the mailbox: the display's size, then the mode and the buffer. */
static int firmware_framebuffer(WitU64 mailbox, const DmaWindow *window, WitBootInfo *boot)
{
    WitU32 width = DEFAULT_WIDTH, height = DEFAULT_HEIGHT, at;
    at = tag(2, TAG_GET_PHYSICAL_SIZE, 2, 0, 0);
    message[at] = 0;
    if (property_call(mailbox, window, at + 1) && message[5] && message[6]) {
        width = message[5];
        height = message[6];
    }
    at = tag(2, TAG_SET_PHYSICAL_SIZE, 2, width, height);
    at = tag(at, TAG_SET_VIRTUAL_SIZE, 2, width, height);
    at = tag(at, TAG_SET_DEPTH, 1, 32, 0);
    at = tag(at, TAG_SET_PIXEL_ORDER, 1, PIXEL_ORDER_BGR, 0);
    const WitU32 allocate = at;
    at = tag(at, TAG_ALLOCATE, 2, 4096, 0);
    const WitU32 pitch = at;
    at = tag(at, TAG_GET_PITCH, 1, 0, 0);
    message[at] = 0;
    if (!property_call(mailbox, window, at + 1)) {
        return 0;
    }
    const WitU32 depth = message[2 + 5 + 5 + 3];
    WitU64 base = message[allocate + 3];
    const WitU64 bytes = message[allocate + 4], stride = message[pitch + 3];
    if (window->Bytes && base >= window->Bus && base - window->Bus < window->Bytes) {
        base = base - window->Bus + window->Physical;
    }
    if (depth != 32 ||
        !base ||
        (base & 4095) ||
        !bytes ||
        stride < (WitU64)width * 4 ||
        stride % 4 ||
        stride * height > bytes) {
        wit_console_write("[BOOT] The firmware's framebuffer is not one the kernel takes; left\n");
        return 0;
    }
    boot->FramebufferBase = base;
    boot->FramebufferBytes = (bytes + 4095) & ~4095ULL;
    boot->FramebufferWidth = width;
    boot->FramebufferHeight = height;
    boot->FramebufferStride = (WitU32)(stride / 4);
    boot->FramebufferFormat = WIT_PIXEL_BGRX;
    return 1;
}

void wit_platform_framebuffer_tree(WitU64 address, WitBootInfo *boot)
{
    WitArmTree tree;
    WitArmTreeItem item;
    WitU32 source = SOURCE_NONE, depth = 0, firmware = 0, simple = 0, okay = 1, format = 0;
    WitU64 mailbox = 0, base = 0, bytes = 0, size = 0, width = 0, height = 0, stride = 0;
    DmaWindow window = {0, 0, 0};
    boot->FramebufferBase = 0;
    boot->FramebufferBytes = 0;
    boot->FramebufferWidth = 0;
    boot->FramebufferHeight = 0;
    boot->FramebufferStride = 0;
    boot->FramebufferFormat = 0;
    if (!wit_arm_tree_open_early(address, &tree)) {
        return;
    }
    /* One walk: the first simple-framebuffer that is not disabled, the Pi firmware's framebuffer and the mailbox. */
    WitU32 kind = 0;
    WitU64 node_base = 0, node_bytes = 0, node_width = 0, node_height = 0, node_stride = 0;
    WitU32 node_format = 0;
    while (wit_arm_tree_next(&tree, &item)) {
        if (item.Kind == WIT_ARM_TREE_BEGIN) {
            kind = 0;
            okay = 1;
            node_base = node_bytes = node_width = node_height = node_stride = 0;
            node_format = 0;
            depth = item.Depth;
        } else if (item.Kind == WIT_ARM_TREE_PROPERTY && item.Depth == depth) {
            if (wit_arm_same(item.Name, "compatible")) {
                kind = wit_arm_tree_names(&item, "simple-framebuffer") ? 1
                    : wit_arm_tree_names(&item, "brcm,bcm2708-fb")     ? 2
                    : wit_arm_tree_names(&item, "brcm,bcm2835-mbox")   ? 3
                                                                       : 0;
                if (kind == 3) {
                    window = dma_window(&tree, depth);
                }
            } else if (wit_arm_same(item.Name, "status") && item.Length >= 5) {
                okay = wit_arm_same((const char *)item.Value, "okay");
            } else if (wit_arm_same(item.Name, "reg") && !wit_arm_tree_reg(&tree, &item, 0, &node_base, &node_bytes)) {
                node_base = 0;
            } else if (wit_arm_same(item.Name, "width") && item.Length == 4) {
                node_width = wit_arm_be32(item.Value);
            } else if (wit_arm_same(item.Name, "height") && item.Length == 4) {
                node_height = wit_arm_be32(item.Value);
            } else if (wit_arm_same(item.Name, "stride") && item.Length == 4) {
                node_stride = wit_arm_be32(item.Value);
            } else if (wit_arm_same(item.Name, "format")) {
                node_format = wit_arm_tree_names(&item, "x8r8g8b8") || wit_arm_tree_names(&item, "a8r8g8b8")
                    ? WIT_PIXEL_BGRX
                    : wit_arm_tree_names(&item, "x8b8g8r8") || wit_arm_tree_names(&item, "a8b8g8r8") ? WIT_PIXEL_RGBX
                                                                                                     : 0;
            }
        } else if (item.Kind == WIT_ARM_TREE_END && item.Depth == depth) {
            if (kind == 1 && okay && !simple && node_base && node_format) {
                simple = 1;
                base = node_base;
                bytes = node_bytes;
                width = node_width;
                height = node_height;
                stride = node_stride;
                format = node_format;
            } else if (kind == 2 && okay) {
                firmware = 1;
            } else if (kind == 3 && okay && node_base) {
                mailbox = node_base;
                size = node_bytes;
            }
            kind = 0; /* the parent's properties came before this child: nothing of it is left to judge */
            depth = item.Depth - 1;
        }
    }
    if (simple &&
        width &&
        height &&
        stride >= width * 4 &&
        stride % 4 == 0 &&
        !(base & 4095) &&
        stride * height <= bytes) {
        boot->FramebufferBase = base;
        boot->FramebufferBytes = (bytes + 4095) & ~4095ULL;
        boot->FramebufferWidth = (WitU32)width;
        boot->FramebufferHeight = (WitU32)height;
        boot->FramebufferStride = (WitU32)(stride / 4);
        boot->FramebufferFormat = format;
        source = SOURCE_SIMPLE;
    } else if (firmware && mailbox && size && firmware_framebuffer(mailbox, &window, boot)) {
        source = SOURCE_FIRMWARE;
    }
    if (source == SOURCE_NONE) {
        wit_console_write("[BOOT] No framebuffer\n");
        return;
    }
    report(boot, source == SOURCE_SIMPLE ? "simple-framebuffer" : "from the firmware through the VideoCore's mailbox");
}
