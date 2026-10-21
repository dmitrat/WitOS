using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Diagnostics;

/// <summary>
/// The processes and threads of a guest that ran past its deadline (plan step P3), read from its memory: the core QEMU
/// wrote (WITOS_GUEST_DUMP=1), the kernel's map, the boot's serial log for where the image and its tables lay, and the
/// layout of the kernel's records the pinned clang computes from the kernel's headers (kernel_layout.c). For each
/// processor its dispatcher and for each thread its state, its wait, its activations and the user context its kernel
/// frame holds; for a dynamic program's process, the modules musl's dynamic linker lists for debuggers
/// (`_dl_debug_addr`) and each thread's frame-pointer chain as module and offset, named by the module's symbols where a
/// file of that name lies below a directory given. ARM64 alone so far, where the freeze P3 follows was seen.
/// </summary>
internal static partial class GuestThreads
{
    #region Constants

    private const string LAYOUT_SOURCE = "tools/WitOS.Dev/Diagnostics/kernel_layout.c";

    // user.c's Dispatcher, which no header declares: Current, Thread, Idle, IdleTimer, IdleRotate, Parked, Ticking,
    // Kicked, Yielding.
    private const int DISPATCHER_BYTES = 48;

    private static readonly string[] THREAD_STATES = ["empty", "ready", "running", "waiting", "exited"];

    private static readonly string[] WAIT_KINDS = ["none", "sleep", "objects"];

    // libwitos's placement (spawn.c): a program at the start of the code arena, its interpreter 64 MiB above.
    private const ulong INTERPRETER_BASE = 0x0000_0080_0100_0000UL + (64UL << 20);

    private const int FRAMES = 40;

    #endregion

    #region Functions

    /// <summary>
    /// Writes the report.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="corePath">The guest's core.</param>
    /// <param name="mapPath">The kernel's map (WitOS.map of the scenario).</param>
    /// <param name="serialPath">The boot's serial log.</param>
    /// <param name="architecture">The guest's architecture.</param>
    /// <param name="libcPath">The guest's libc.so, for musl's debugger list; the sysroot's by default.</param>
    /// <param name="symbolDirectories">Directories whose ELF files name the functions of the modules.</param>
    /// <returns>When it is written.</returns>
    /// <exception cref="NotSupportedException">The architecture is not ARM64.</exception>
    public static async Task RunAsync(string root, string corePath, string mapPath, string serialPath, KernelArchitecture architecture,
        string? libcPath = null, IReadOnlyList<string>? symbolDirectories = null)
    {
        if (architecture != KernelArchitecture.Arm64)
            throw new NotSupportedException("guest-threads reads ARM64 guests so far.");
        var layout = await ReadLayoutAsync(root, architecture);
        var map = KernelMap.Read(mapPath);
        var serial = await File.ReadAllTextAsync(serialPath);
        var stack = Hex(Required(serial, "Kernel stack: 0x([0-9A-Fa-f]+)"));
        var tableRoot = Hex(Required(serial, "TTBR1: 0x([0-9A-Fa-f]+)"));
        var online = int.Parse(Required(serial, "Processors present/online: \\d+/(\\d+)"), CultureInfo.InvariantCulture);
        // The image's base: the printed stack is wit_a64_kernel_stack above its guard page.
        var imageBase = stack - map.Rva("wit_a64_kernel_stack") - 0x1000;
        using var core = GuestCore.Open(corePath);
        var libc = ElfSymbols.Read(libcPath ?? Path.Combine(root, "artifacts", "sysroot", architecture.Name, "lib", "libc.so"));
        var symbols = new Symbolizer(symbolDirectories ?? []);
        var report = new StringBuilder();
        report.AppendLine($"Image base 0x{imageBase:x}, TTBR1 0x{tableRoot:x}, {online} processors online");
        var lockOwner = core.Read32(imageBase + map.Rva("lock_owner", "cpus"));
        var lockWaiters = core.Read32(imageBase + map.Rva("lock_waiters", "cpus"));
        report.AppendLine($"Kernel lock: owner {(int)lockOwner}, waiters 0x{lockWaiters:x}");
        var dispatchers = imageBase + map.Rva("dispatchers", "user");
        for (var cpu = 0; cpu < online; cpu++)
        {
            var at = dispatchers + (ulong)(cpu * DISPATCHER_BYTES);
            report.AppendLine($"Processor {cpu}: component 0x{core.Read64(at):x}, thread {core.Read32(at + 8)}, idle {core.Read32(at + 12)}, " +
                $"parked {core.Read32(at + 24)}, ticking {core.Read32(at + 28)}, kicked {core.Read32(at + 32)}, yielding 0x{core.Read64(at + 40):x}");
        }
        var owners = imageBase + map.Rva("slot_owners", "user");
        for (var slot = 0; slot < (int)layout.ProcessCapacity; slot++)
        {
            var process = core.Read64(owners + (ulong)(slot * 8));
            if (process != 0 && core.Contains(process, (int)layout.ProcessBytes))
                DescribeProcess(core, layout, tableRoot, slot, process, libc, symbols, report);
        }
        Console.Write(report.ToString());
    }

    private static void DescribeProcess(GuestCore core, Layout layout, ulong tableRoot, int slot, ulong process, ElfSymbols libc,
        Symbolizer symbols, StringBuilder report)
    {
        var top = core.Read32(process + layout.ProcessThreadTop);
        var space = new UserSpace(core, core.Read64(process + layout.ProcessSpaceRoot));
        var modules = Modules(space, libc);
        report.AppendLine($"Slot {slot}: process {core.Read32(process + layout.ProcessId)} at 0x{process:x}, state {core.Read32(process + layout.ProcessState)}, " +
            $"{top} threads at most, current {core.Read32(process + layout.ProcessCurrentThread)}, " +
            $"{(modules.Count == 0 ? "no module list" : modules[0].Name + (modules.Count > 1 ? $" and {modules.Count - 1} modules" : ""))}");
        foreach (var module in modules)
            report.AppendLine($"    module 0x{module.Base:x} {module.Name}");
        for (var index = 0; index < top; index++)
        {
            var thread = process + layout.ProcessThreads + (ulong)index * layout.ThreadBytes;
            var state = core.Read32(thread + layout.ThreadState);
            if (state == 0)
                continue;
            var waitKind = core.Read32(thread + layout.ThreadWaitKind);
            var waitCount = core.Read32(thread + layout.ThreadWaitCount);
            var handles = Enumerable.Range(0, (int)Math.Min(waitCount, layout.WaitCapacity))
                .Select(i => $"0x{core.Read64(thread + layout.ThreadWaitHandles + (ulong)(i * 8)):x}");
            var deadline = core.Read64(thread + layout.ThreadDeadline);
            var line = new StringBuilder($"  thread {index}: {Name(THREAD_STATES, state)}");
            line.Append(CultureInfo.InvariantCulture, $", processor {core.Read32(thread + layout.ThreadProcessor)}");
            line.Append(CultureInfo.InvariantCulture, $", suspended {core.Read32(thread + layout.ThreadSuspendCount)}");
            line.Append(CultureInfo.InvariantCulture, $", activations {core.Read32(thread + layout.ThreadActivationCount)}");
            line.Append(CultureInfo.InvariantCulture, $", wait {Name(WAIT_KINDS, waitKind)}");
            if (waitKind != 0)
                line.Append(CultureInfo.InvariantCulture,
                    $" [{string.Join(' ', handles)}] handle 0x{core.Read64(thread + layout.ThreadWaitHandle):x} deadline {(deadline == ulong.MaxValue ? "none" : "0x" + deadline.ToString("x", CultureInfo.InvariantCulture))}");
            line.Append(CultureInfo.InvariantCulture,
                $", stack 0x{core.Read64(thread + layout.ThreadStackBottom):x}-0x{core.Read64(thread + layout.ThreadStackTop):x}");
            report.AppendLine(line.ToString());
            var context = core.Read64(thread + layout.ThreadContext);
            if (context == 0)
                continue;
            var frame = context >= 0xFFFF_0000_0000_0000UL ? core.TranslateA64(tableRoot, context) : context;
            if (frame is null || !core.Contains(frame.Value, (int)layout.FrameSpsr + 8))
            {
                report.AppendLine($"    context 0x{context:x}: not mapped");
                continue;
            }
            var registers = Enumerable.Range(0, 31).Select(i => core.Read64(frame.Value + layout.FrameX + (ulong)(i * 8))).ToArray();
            var pc = core.Read64(frame.Value + layout.FrameElr);
            report.AppendLine($"    pc 0x{pc:x} sp 0x{core.Read64(frame.Value + layout.FrameSp):x} " +
                $"lr 0x{registers[30]:x} spsr 0x{core.Read64(frame.Value + layout.FrameSpsr):x} x8 0x{registers[8]:x}");
            report.AppendLine("    x0-x7 " + string.Join(' ', registers.Take(8).Select(value => $"0x{value:x}")));
            report.AppendLine($"    #0 {symbols.Describe(modules, pc)}");
            report.AppendLine($"    lr {symbols.Describe(modules, registers[30])}");
            // The frame records AArch64 code keeps: x29 names the caller's record, its second word the return address.
            var record = registers[29];
            for (var depth = 1; depth < FRAMES && record != 0 && record % 8 == 0; depth++)
            {
                var previous = space.Read64(record);
                var returnAddress = space.Read64(record + 8);
                if (previous is null || returnAddress is null || returnAddress == 0)
                    break;
                report.AppendLine($"    #{depth} {symbols.Describe(modules, returnAddress.Value)}");
                if (previous.Value <= record)
                    break;
                record = previous.Value;
            }
        }
    }

    // musl's dynamic linker's list of modules, from the debugger hook in the interpreter at libwitos's place for it: each
    // module's base and name; empty for a static program.
    private static List<Module> Modules(UserSpace space, ElfSymbols libc)
    {
        var modules = new List<Module>();
        var magic = space.Read32(INTERPRETER_BASE);
        var hook = libc.Value("_dl_debug_addr");
        if (magic != 0x464C457FU || hook is null)
            return modules;
        var debug = space.Read64(INTERPRETER_BASE + hook.Value);
        var head = debug is null ? null : space.Read64(debug.Value + 8);
        for (var dso = head; dso is { } at && at != 0 && modules.Count < 256; dso = space.Read64(at + 24))
        {
            var imageBase = space.Read64(at);
            var name = space.Read64(at + 8);
            if (imageBase is null || name is null)
                break;
            modules.Add(new Module(imageBase.Value, space.ReadString(name.Value) ?? "?"));
        }
        return modules;
    }

    // A process's user memory through its own translation table, ARM64's stage 1 as the kernel's.
    private sealed class UserSpace(GuestCore core, ulong root)
    {
        public ulong? Read64(ulong address)
        {
            var physical = root == 0 ? null : core.TranslateA64(root, address);
            return physical is { } at && (address & 0xFFF) <= 0xFF8 && core.Contains(at, 8) ? core.Read64(at) : null;
        }

        public uint? Read32(ulong address)
        {
            var physical = root == 0 ? null : core.TranslateA64(root, address);
            return physical is { } at && (address & 0xFFF) <= 0xFFC && core.Contains(at, 4) ? core.Read32(at) : null;
        }

        public string? ReadString(ulong address)
        {
            var text = new StringBuilder();
            for (var i = 0; i < 256; i++)
            {
                var physical = core.TranslateA64(root, address + (ulong)i);
                if (physical is null)
                    return null;
                var value = core.Read(physical.Value, 1)[0];
                if (value == 0)
                    return text.ToString();
                text.Append((char)value);
            }
            return text.ToString();
        }
    }

    private sealed record Module(ulong Base, string Name);

    // Names an address as module+offset, and as a function where a file of the module's name lies below a directory.
    private sealed class Symbolizer(IReadOnlyList<string> directories)
    {
        private readonly Dictionary<string, ElfSymbols?> m_files = new(StringComparer.Ordinal);

        public string Describe(IReadOnlyList<Module> modules, ulong address)
        {
            var module = modules.Where(module => module.Base <= address).OrderByDescending(module => module.Base).FirstOrDefault();
            if (module is null || address - module.Base > (1UL << 32))
                return $"0x{address:x}";
            var offset = address - module.Base;
            var name = Path.GetFileName(module.Name);
            var function = Symbols(name)?.Name(offset);
            return $"0x{address:x} {name}+0x{offset:x}{(function is null ? "" : " " + function)}";
        }

        private ElfSymbols? Symbols(string name)
        {
            if (m_files.TryGetValue(name, out var known))
                return known;
            var path = directories.Where(Directory.Exists)
                .SelectMany(directory => Directory.EnumerateFiles(directory, name, SearchOption.AllDirectories)).FirstOrDefault();
            ElfSymbols? symbols = null;
            try
            {
                symbols = path is null ? null : ElfSymbols.Read(path);
            }
            catch (InvalidDataException)
            {
            }
            m_files[name] = symbols;
            return symbols;
        }
    }

    // The values of kernel_layout.c's array, compiled to LLVM IR for the kernel's triple with the kernel's headers.
    private static async Task<Layout> ReadLayoutAsync(string root, KernelArchitecture architecture)
    {
        Toolchain.RequireClang(root);
        string[] includes = ["src/Kernel/include", "src/Kernel", "src/Kernel.Arch.A64/include", "src/Kernel.Arch.A64"];
        var arguments = new List<string>
        {
            $"--target={architecture.KernelTriple}", "-S", "-emit-llvm", "-o", "-", "-ffreestanding", "-nostdinc"
        };
        arguments.AddRange(architecture.KernelCompileOptions);
        arguments.AddRange(includes.Select(include => "-I" + Path.Combine(root, include)));
        arguments.Add(Path.Combine(root, LAYOUT_SOURCE));
        var result = await Processes.RunAsync(Toolchain.Clang(root), arguments, root);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"clang could not compile {LAYOUT_SOURCE}:\n{result.Error}");
        var match = LayoutArray().Match(result.Output);
        if (!match.Success)
            throw new InvalidDataException($"No wit_layout in clang's IR of {LAYOUT_SOURCE}.");
        var values = Regex.Matches(match.Groups[1].Value, "i64 (\\d+)").Select(m => ulong.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        return new Layout(values);
    }

    private static string Required(string text, string pattern)
    {
        var match = Regex.Match(text, pattern);
        return match.Success ? match.Groups[1].Value : throw new InvalidDataException($"The serial log has no line for /{pattern}/.");
    }

    private static ulong Hex(string text) => ulong.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static string Name(string[] names, uint value) => value < names.Length ? names[value] : value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"@wit_layout = [^\[]*\[\d+ x i64\] \[([^\]]*)\]")]
    private static partial Regex LayoutArray();

    #endregion

    #region Nested Types

    // kernel_layout.c's values by their place in its array.
    private sealed class Layout(ulong[] values)
    {
        public ulong ProcessBytes => values[0];
        public ulong ProcessId => values[1];
        public ulong ProcessState => values[2];
        public ulong ProcessThreads => values[3];
        public ulong ProcessThreadTop => values[4];
        public ulong ProcessCurrentThread => values[5];
        public ulong ThreadBytes => values[6];
        public ulong ThreadState => values[7];
        public ulong ThreadWaitKind => values[8];
        public ulong ThreadWaitHandle => values[9];
        public ulong ThreadWaitHandles => values[10];
        public ulong ThreadWaitCount => values[11];
        public ulong ThreadActivationCount => values[12];
        public ulong ThreadDeadline => values[13];
        public ulong ThreadHandle => values[14];
        public ulong ThreadProcessor => values[15];
        public ulong ThreadSuspendCount => values[16];
        public ulong ThreadContext => values[17];
        public ulong ThreadStackBottom => values[18];
        public ulong ThreadStackTop => values[19];
        public ulong ThreadAffinity => values[20];
        public ulong ProcessCapacity => values[21];
        public ulong ProcessorCapacity => values[22];
        public ulong WaitCapacity => values[23];
        public ulong FrameX => values[24];
        public ulong FrameSp => values[25];
        public ulong FrameElr => values[26];
        public ulong FrameSpsr => values[27];
        public ulong ProcessSpaceRoot => values[29];
    }

    #endregion
}
