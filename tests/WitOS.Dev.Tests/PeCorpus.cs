using System.Text.Json;
using System.Security.Cryptography;
using WitOS.Dev;
internal static class PeCorpus
{
    public static async Task RunAsync(string root,string output,bool coverage=false)
    {
        var msvc=await Toolchain.FindMsvcAsync(root);var vc=Path.GetFullPath(Path.Combine(msvc,"../../.."));
        var version=Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var sdk=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Windows Kits/10");
        var spaced=Path.Combine(output,"include with spaces");Directory.CreateDirectory(spaced);
        await File.WriteAllTextAsync(Path.Combine(spaced,"fixture.h"),"#define EXPECTED_VALUE 73\n");
        var profile=RuntimeGuestDriver.CompileProfile($"""cl.exe -I"{spaced}" "-DLABEL=\"two words\"" -DNO_STRESS_LOG -DFEATURE_SUSPEND_REDIRECTION""");
        var quoteSource=Path.Combine(spaced,"quoted source.c");
        await File.WriteAllTextAsync(quoteSource,"#include \"fixture.h\"\n_Static_assert(EXPECTED_VALUE == 73, \"include\");\n_Static_assert(sizeof(LABEL) == 10, \"define\");\n");
        await Processes.RequireSuccessAsync(Path.Combine(msvc,"cl.exe"),["/nologo","/c","/TC","/std:c17","/W4","/WX",..profile,"/Fo"+Path.Combine(spaced,"quoted object.obj"),quoteSource],root);
        if(coverage)await NativeCoverage.PrepareAsync(root);
        var compiler=coverage?Path.Combine(NativeCoverage.DirectoryPath(root),"bin/clang-cl.exe"):Path.Combine(msvc,"cl.exe");
        string[] instrumentation=coverage?["/clang:-fprofile-instr-generate","/clang:-fcoverage-mapping","/fsanitize=address","-fuse-ld=lld"]:[];
        var source=Path.Combine(root,"tests/WitOS.Dev.Tests/PeCorpus.c");var parser=Path.Combine(root,"src/Kernel/pe.c");
        var exe=Path.Combine(output,"pe-corpus.exe");
        await Processes.RequireSuccessAsync(compiler,["/nologo","/MD","/TC","/std:c17",coverage?"/Od":"/O2",..instrumentation,"/GS","/W4","/WX","/D_CRT_SECURE_NO_WARNINGS",
            "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"Include",version,"ucrt"),"/I"+Path.Combine(sdk,"Include",version,"shared"),"/I"+Path.Combine(sdk,"Include",version,"um"),
            "/I"+Path.Combine(root,"src/Kernel/include"),"/Fo"+output+"/","/Fe"+exe,source,parser,Path.Combine(root,"src/Kernel/pe_imports.c"),Path.Combine(root,"src/Kernel/pe_exports.c"),"/link",
            "/LIBPATH:"+Path.Combine(vc,"lib/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",version,"ucrt/x64"),"/LIBPATH:"+Path.Combine(sdk,"Lib",version,"um/x64"),"kernel32.lib"],root);
        var image=Path.Combine(root,"artifacts/runtime-readiness/guest-driver/WitOS.NativeAotBoot.pe");
        IReadOnlyDictionary<string,string>? environment=coverage?new Dictionary<string,string>{
            ["LLVM_PROFILE_FILE"]=Path.Combine(output,"pe-corpus.profraw"),
            ["PATH"]=Path.Combine(NativeCoverage.DirectoryPath(root),"lib/clang/20/lib/windows")+Path.PathSeparator+Environment.GetEnvironmentVariable("PATH")}:null;
        var result=await Processes.RunAsync(exe,[image],output,30,environment);
        await File.WriteAllTextAsync(Path.Combine(output,"pe-corpus.log"),result.Output+result.Error);
        if(result.TimedOut||result.ExitCode!=0||!result.Output.Contains("PASS: 555 immutable trailing-guard PE inputs"))throw new Exception($"PE corpus failed (exit={result.ExitCode}, timeout={result.TimedOut}); see {Path.Combine(output, "pe-corpus.log")}");
        string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(output,"pe-corpus.json"),JsonSerializer.Serialize(new{hostOnly=true,cases=555,structuralVerdictCases=26,seed="0x57314A29",inputs=new[]{source,parser,Path.Combine(root,"src/Kernel/pe_imports.c"),Path.Combine(root,"src/Kernel/pe_exports.c"),image,Path.Combine(root,"src/Kernel/include/witos/pe.h"),Path.Combine(root,"src/Kernel/include/witos/unwind_metadata.h")}.Select(file=>new{file,sha256=Hash(file)})}));
        Console.Write(result.Output);
        if(coverage)await NativeCoverage.ReportAsync(root,output,exe);
    }
}
