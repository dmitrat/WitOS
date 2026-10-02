using WitOS.Dev;
internal static class AssemblyPackageNative
{
    internal static async Task RunAsync(string root,string output)
    {
        var msvc=await Toolchain.FindMsvcAsync(root);
        var vc=Path.GetFullPath(Path.Combine(msvc,"../../.."));
        var sdk=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Windows Kits/10");
        var version=Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var payload=Enumerable.Range(0,4099).Select(i=>(byte)i).ToArray();
        var files=new (string Name,ReadOnlyMemory<byte> Bytes)[] {
            ("app/Probe.dll",payload),("app/Probe.runtimeconfig.json","{\"runtimeOptions\":{}}"u8.ToArray()),
            ("empty",Array.Empty<byte>()),("resources/\u03bb.txt","payload"u8.ToArray()) };
        var package=Path.Combine(output,"writer.pak");
        var small=Path.Combine(output,"two-files.pak");
        await File.WriteAllBytesAsync(package,AssemblyPackage.Create(files));
        await File.WriteAllBytesAsync(small,AssemblyPackage.Create(new (string,ReadOnlyMemory<byte>)[] {("a.bin",new byte[]{1,2,3}),("b.bin",new byte[]{4,5,6})}));
        var firmware=Path.Combine(output,"firmware.pak");
        var firmwareData=Enumerable.Range(0,2*1024*1024+123).Select(i=>(byte)i).ToArray();
        await File.WriteAllBytesAsync(firmware,AssemblyPackage.Create(new (string,ReadOnlyMemory<byte>)[]{("data",firmwareData)}));
        var hierarchy=Path.Combine(output,"hierarchy.pak");
        await File.WriteAllBytesAsync(hierarchy,AssemblyPackage.Create(new (string,ReadOnlyMemory<byte>)[]{
            ("a",Array.Empty<byte>()),("b.c",Array.Empty<byte>()),("b/d",Array.Empty<byte>())}));
        var exe=Path.Combine(output,"assembly-package.exe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc,"cl.exe"),["/nologo","/MD","/TC","/std:c17","/W4","/WX","/O2",
            "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",version,"ucrt"),"/I"+Path.Combine(sdk,"Include",version,"um"),"/I"+Path.Combine(sdk,"Include",version,"shared"),"/I"+Path.Combine(root,"src/Kernel/include"),
            "/Fo"+output+"/","/Fe"+exe,Path.Combine(root,"src/Kernel/package.c"),Path.Combine(root,"tests/WitOS.Dev.Tests/AssemblyPackageNative.c"),Path.Combine(root,"tests/WitOS.Dev.Tests/BootPackageFirmware.c"),
            "/link","/LIBPATH:"+Path.Combine(vc,"lib/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",version,"ucrt/x64"),
            "/LIBPATH:"+Path.Combine(sdk,"Lib",version,"um/x64"),"kernel32.lib"],root);
        var run=await Processes.RunAsync(exe,[package,small,firmware,hierarchy],output,30);
        await File.WriteAllTextAsync(Path.Combine(output,"assembly-package.log"),run.Output+run.Error);
        if(run.TimedOut||run.ExitCode!=0||!run.Output.StartsWith("PASS: ",StringComparison.Ordinal))throw new InvalidDataException("Native package validation failed: "+run.Output+run.Error);
        for(var i=0;i<files.Length;++i){
            var extracted=await File.ReadAllBytesAsync(Path.Combine(output,$"package-roundtrip-{i}.bin"));
            if(!extracted.AsSpan().SequenceEqual(files[i].Bytes.Span))throw new InvalidDataException("Native package payload changed: "+files[i].Name);
        }
        Console.Write(run.Output);
    }
}
