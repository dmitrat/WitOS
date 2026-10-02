namespace WitOS.Dev;

internal static class NativeLibraryImage
{
    internal static async Task<string> BuildAsync(string root, string output, string msvc)
    {
        Directory.CreateDirectory(output);
        var obj = Path.Combine(output, "library.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/c", "/TC", "/std:c17", "/W4", "/WX", "/GS-", "/Zl", "/Oi", "/O2", "/Fo" + obj, Path.Combine(root, "tests/User.X64/native_library.c")], root);
        var dll = Path.Combine(output, "WitLibraryFixture.dll");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo","/dll","/noentry","/nodefaultlib","/machine:x64","/subsystem:native","/fixed:no","/dynamicbase","/incremental:no","/Brepro",
            "/base:0x180000000","/def:"+Path.Combine(root,"tests/User.X64/native_library.def"),"/out:"+dll,obj], root);
        return dll;
    }
    internal static async Task<Dictionary<string, string>> BuildDependenciesAsync(string root, string output, string msvc)
    {
        async Task Compile(string source, string name, string? define = null)
        {
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/c", "/TC", "/std:c17", "/W4", "/WX", "/GS-", "/Zl", "/Oi", "/O2", .. (define?.Split(';').Select(value => "/D" + value) ?? Array.Empty<string>()), "/Fo" + Path.Combine(output, name + ".obj"), Path.Combine(root, source)], root);
        }
        async Task ImportLibrary(string name, string module, string export)
        {
            var definition = Path.Combine(output, name + ".def");
            await File.WriteAllTextAsync(definition, $"LIBRARY {module}\nEXPORTS\n {export}\n");
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "lib.exe"), ["/nologo", "/machine:x64", "/def:" + definition, "/out:" + Path.Combine(output, name + ".lib")], root);
        }
        async Task<string> Link(string name, params string[] libraries)
        {
            var dll = Path.Combine(output, name + ".dll");
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo", "/dll", "/noentry", "/nodefaultlib", "/machine:x64", "/subsystem:native", "/fixed:no", "/dynamicbase", "/incremental:no", "/Brepro", "/base:0x180000000", "/out:" + dll, Path.Combine(output, name + ".obj"), .. libraries.Select(lib => Path.Combine(output, lib + ".lib"))], root);
            return dll;
        }
        var result = new Dictionary<string, string>();
        await Compile("tests/User.X64/dependent_library.c", "dependent");
        result["dependent.dll"] = await Link("dependent", "WitLibraryFixture");
        await ImportLibrary("missing-import", "WitLibraryFixture.dll", "MissingSymbol");
        await Compile("tests/User.X64/missing_library.c", "missing");
        result["missing.dll"] = await Link("missing", "missing-import");
        await ImportLibrary("CycleA-import", "CycleA.dll", "CycleA");
        await ImportLibrary("CycleB-import", "CycleB.dll", "CycleB");
        await Compile("tests/User.X64/cycle_library.c", "CycleA", "CYCLE_A");
        result["CycleA.dll"] = await Link("CycleA", "CycleB-import");
        await Compile("tests/User.X64/cycle_library.c", "CycleB");
        result["CycleB.dll"] = await Link("CycleB", "CycleA-import");
        var entryAsm = Path.Combine(output, "library-entry-x64.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + entryAsm, Path.Combine(root, "tests/User.X64/library_entry_x64.asm")], root);
        foreach (var entry in new[] { ("init", (string?)null), ("initfail", "INIT_FAIL"), ("initparent", "INIT_PARENT"), ("initparentfail", "INIT_PARENT;INIT_FAIL") })
        {
            await Compile("tests/User.X64/library_entry.c", entry.Item1, entry.Item2);
            var dll = Path.Combine(output, entry.Item1 + ".dll");
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo", "/dll", "/entry:LibraryEntry", "/nodefaultlib", "/machine:x64", "/subsystem:native", "/fixed:no", "/dynamicbase", "/incremental:no", "/Brepro", "/base:0x180000000", "/out:" + dll, Path.Combine(output, entry.Item1 + ".obj"), entryAsm, Path.Combine(output, "WitLibraryFixture.lib"), .. (entry.Item1.StartsWith("initparent", StringComparison.Ordinal) ? new[] { Path.Combine(output, "init.lib") } : Array.Empty<string>())], root);
            result[entry.Item1 + ".dll"] = dll;
        }
        return result;
    }
}
