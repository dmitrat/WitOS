using System.Reflection.PortableExecutable;
using System.Text;
using WitOS.Dev.Host;
namespace WitOS.Dev.CoreClr;

internal static class CoreClrStorageImage
{
    #region Functions

    internal static async Task BuildAsync(string root, string output, string msvc)
    {
        var objects = new List<string> { Path.Combine(output, "native_start.obj") };
        foreach (var source in new[] { "src/System.Native/library.c", "src/System.Native/library_lifecycle.c", "tests/User.X64/library_loader.c", "tests/User.X64/library_graph.c", "tests/User.X64/library_readers.c", "tests/User.X64/library_lifecycle.c", "tests/User.X64/library_tls_guest.c", "src/System.Native/directory.c", "tests/User.X64/native_directory.c", "src/System.Native/path.c", "src/System.Native/current_directory.c", "tests/User.X64/native_paths.c", "src/System.Native/file.c", "src/System.Native/file_view.c", "tests/User.X64/file_views.c", "tests/User.X64/coreclr_storage.c" })
        {
            var obj = Path.Combine(output, "storage_" + source.Replace('/', '_').Replace('.', '_') + ".obj");
            objects.Add(obj);
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo","/c","/TC","/std:c17","/W4","/WX","/GS-","/Zl","/Oi","/O2",
                "/I"+Path.Combine(root,"src/Kernel/include"),"/I"+Path.Combine(root,"src/System.Native"),"/I"+Path.Combine(root,"tests/User.X64"),"/I"+output,
                "/Fo"+obj,Path.Combine(root,source)], root);
        }
        var palInclude = Path.Combine(output, "host-pal");
        await CoreClrHostFilePal.PrepareAsync(root, palInclude);
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        foreach (var source in new[] { "src/Runtime.CoreClr/host_library.witos.cpp", "src/Runtime.CoreClr/host_files.witos.cpp", "tests/User.X64/host_file_pal.cpp" })
        {
            var obj = Path.Combine(output, Path.GetFileName(source) + ".obj");
            objects.Add(obj);
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo","/c","/TP","/std:c++20","/O2","/GS-","/Zl","/W4","/WX","/DNDEBUG","/DWITOS_HOST_FILES",
                "/I"+palInclude,"/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",version,"ucrt"),"/I"+Path.Combine(sdk,"Include",version,"um"),"/I"+Path.Combine(sdk,"Include",version,"shared"),
                "/I"+Path.Combine(root,"src/System.Native"),"/I"+Path.Combine(root,"src/Kernel/include"),"/Fo"+obj,Path.Combine(root,source)], root);
        }
        var stackProbe = Path.Combine(output, "storage_chkstk.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + stackProbe, Path.Combine(root, "src/Kernel.Arch.X64/chkstk.asm")], root);
        objects.Add(stackProbe);
        objects.Add(Path.Combine(output, "native_error.obj"));
        var file = Path.Combine(output, "CoreClrStorageFixture.pe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo","/subsystem:native","/entry:wit_native_start","/nodefaultlib","/machine:x64",
            "/fixed:no","/dynamicbase","/incremental:no","/Brepro","/base:0x180000000","/out:"+file,..objects], root);
        var bytes = await File.ReadAllBytesAsync(file);
        using var pe = new PEReader(new MemoryStream(bytes));
        if (pe.PEHeaders.CorHeader is not null || pe.PEHeaders.PEHeader!.ImportTableDirectory.Size != 0 || pe.PEHeaders.PEHeader.DelayImportTableDirectory.Size != 0)
            throw new InvalidDataException("Storage fixture acquired managed/native OS imports.");
        var header = new StringBuilder("static const unsigned char wit_storage_image[]={\n");
        for (var i = 0; i < bytes.Length; i += 16)
            header.AppendLine(string.Join(",", bytes.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
        header.AppendLine("};");
        await File.WriteAllTextAsync(Path.Combine(output, "coreclr_storage_image.h"), header.ToString(), Encoding.ASCII);
    }

    #endregion
}
