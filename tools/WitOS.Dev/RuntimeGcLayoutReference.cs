using System.Security.Cryptography;
using System.Text.Json;
namespace WitOS.Dev;
internal static class RuntimeGcLayoutReference
{
    public static async Task RunAsync(string root,string msvc)
    {
        var output=Path.Combine(root,"artifacts/runtime-gc-layout-reference");Directory.CreateDirectory(output);
        var pin=RuntimeExperiment.ReadLock(root);var item=pin.Sources.Single(s=>s.Path=="src/coreclr/gc/gc.cpp");
        using var client=new HttpClient();
        var original=await RuntimeExperiment.FetchAsync(client,Path.Combine(root,".tools/runtime-audit"),"runtime",pin.RuntimeCommit,item.Path,item.Sha256);
        var corrected=Path.Combine(root,"artifacts/runtime-config/source/gc.witos.cpp");
        foreach(var (file,name) in new[]{(original,"original_layout.inc"),(corrected,"corrected_layout.inc")}){
            var text=(await File.ReadAllTextAsync(file)).Replace("\r\n","\n");
            var start=text.IndexOf("void gc_heap::get_card_table_element_layout (",StringComparison.Ordinal);
            var end=text.IndexOf("\n#ifdef USE_REGIONS\nbool gc_heap::on_used_changed",start,StringComparison.Ordinal);
            if(start<0||end<=start)throw new InvalidDataException("Pinned GC layout method boundary changed.");
            await File.WriteAllTextAsync(Path.Combine(output,name),text[start..end]);
        }
        var vc=Path.GetFullPath(Path.Combine(msvc,"..","..",".."));
        var version=Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var sdk=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Windows Kits","10");
        var source=Path.Combine(root,"tests/Runtime.NativeAot/gc_layout_reference.cpp");var exe=Path.Combine(output,"layout.exe");
        var build=await Processes.RunAsync(Path.Combine(msvc,"cl.exe"),["/nologo","/MD","/std:c++17","/O2","/GS","/W4","/WX",
            "/I"+output,"/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",version,"ucrt"),
            "/Fo"+output+"/","/Fe"+exe,source,"/link","/LIBPATH:"+Path.Combine(vc,"lib/x64"),
            "/LIBPATH:"+Path.Combine(sdk,"Lib",version,"ucrt/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",version,"um/x64"),"kernel32.lib"],root,60);
        await File.WriteAllTextAsync(Path.Combine(output,"build.log"),build.Output+build.Error);
        if(build.TimedOut||build.ExitCode!=0)throw new InvalidDataException("GC layout reference build failed.");
        var run=await Processes.RunAsync(exe,[],output,30);await File.WriteAllTextAsync(Path.Combine(output,"reference.log"),run.Output+run.Error);
        if(run.TimedOut||run.ExitCode!=0||!run.Output.Contains("PASS: 128 pinned GC bookkeeping layouts"))throw new InvalidDataException("GC layout boundary regression.");
        string Hash(string file)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(output,"reference.json"),JsonSerializer.Serialize(new{hostOnly=true,pin.RuntimeCommit,
            inputs=new[]{original,corrected,source,Path.Combine(output,"original_layout.inc"),Path.Combine(output,"corrected_layout.inc")}.Select(file=>new{file,sha256=Hash(file)}),cases=128},new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
        Console.WriteLine("[GC-LAYOUT-REFERENCE-PASS] 128 pinned original/corrected layouts; complete page coverage (HOSTED only).");
    }
}
