using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev;

if(args.FirstOrDefault() is "leaf" or "leaf-no-pipes"){
    if(args[0]=="leaf-no-pipes")ChildHandles.CloseOutput();
    File.WriteAllText(args[1],Environment.ProcessId.ToString());
    await Task.Delay(8000);if(args[0]=="leaf")Console.WriteLine("leaf finished");return 0;
}
if(args.FirstOrDefault() is "launcher" or "launcher-no-pipes"){
    if(args[0]=="launcher-no-pipes")ChildHandles.MakeOutputNonInheritable();
    var child=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true};
    child.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);child.ArgumentList.Add(args[0]=="launcher"?"leaf":"leaf-no-pipes");child.ArgumentList.Add(args[1]);
    using var process=Process.Start(child)!;
    while(!File.Exists(args[1]))await Task.Delay(10);
    return 0;
}
if(args.FirstOrDefault()=="export-pipes"){Console.WriteLine("before exported pipes");ChildHandles.ExportOutput(int.Parse(args[1]),args[2]);return 0;}
if(args.FirstOrDefault()=="env"){Console.Write(Environment.GetEnvironmentVariable("WITOS_Q0_TEST_ENV"));return 0;}
if(args.FirstOrDefault()=="echo"){Console.WriteLine(JsonSerializer.Serialize(args.Skip(1)));Console.Error.Write("stderr-tail");return 7;}
if(args.FirstOrDefault()=="flood"){Console.Write(new string('a',200000));Console.Error.Write(new string('b',200000));return 0;}
if(args.FirstOrDefault()=="burst-wait"){
    File.WriteAllText(args[1],Environment.ProcessId.ToString());Console.WriteLine("before burst");
    var chunk=new string('x',65536);
    for(int n=0;n<32;++n){Console.Write(chunk);Console.Error.Write(chunk);}
    await Task.Delay(30000);return 0;
}
if(args.FirstOrDefault()=="stop-handshake"){
    if(await Console.In.ReadLineAsync()!="hello")return 26;Console.WriteLine("ready");
    return await Console.In.ReadLineAsync()=="quit"?25:27;
}
if(args.FirstOrDefault()=="stop-input"){
    Console.WriteLine("before cooperative stop");var command=await Console.In.ReadLineAsync();return command=="quit"?23:24;
}
if(args.FirstOrDefault()=="wait"){Console.WriteLine("before wait");await Task.Delay(30000);return 0;}
if(args.FirstOrDefault()=="capture-limit"){
    var stream=args[1]=="stdout"?Console.Out:Console.Error;
    var chunk=new string('x',65536);
    for(int i=0;i<150;++i)stream.Write(chunk);
    await Task.Delay(30000);return 0;
}
var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../.."));
var scratch=Path.Combine(root,"artifacts/q0-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);
if(args.Contains("--pe-fuzz")){await NativeCoverage.FuzzAsync(root,scratch);return 0;}
if(args.Contains("--pe-coverage")){await PeCorpus.RunAsync(root,scratch,true);return 0;}
if(args.Contains("--pe-only")){await PeCorpus.RunAsync(root,scratch);return 0;}
if(args.Contains("--q1-protocol")){
    await Q1Tests.Cases(root,scratch).First().Run();return 0;
}
var failures=new List<string>();int passed=0;
async Task Test(string name,Func<Task> run){try{await run();++passed;Console.WriteLine("PASS: "+name);}catch(Exception e){failures.Add(name);Console.WriteLine("FAIL: "+name+" ? "+e.Message);}}
void Check(bool value,string why){if(!value)throw new InvalidOperationException(why);}
string[] Child(params string[] rest)=>[Assembly.GetExecutingAssembly().Location,..rest];
await Test("DeadlineIncludesDescendantPipes",async()=>{
    var pid=Path.Combine(scratch,"leaf.pid");var clock=Stopwatch.StartNew();
    var result=await Processes.RunAsync("dotnet",Child("launcher",pid),root,3);clock.Stop();
    Check(result.TimedOut,"Descendant retained pipes past deadline but result was success");
    Check(clock.Elapsed<TimeSpan.FromSeconds(7),"Deadline/cleanup was not bounded");
    Check(File.Exists(pid),"Fixture did not start its descendant");
    try{using var child=Process.GetProcessById(int.Parse(File.ReadAllText(pid)));Check(child.HasExited,"Descendant survived timeout");}catch(ArgumentException){}
});
await Test("ArgumentsEnvironmentAndExit",async()=>{
    string[] values=["", "two words", "C:\\with space\\", "a\"b", "tail\\\"", "?????????"];
    var result=await Processes.RunAsync("dotnet",Child(["echo",..values]),root,15);
    Check(!result.TimedOut&&result.ExitCode==7,"Exit status changed");
    Check(JsonSerializer.Deserialize<string[]>(result.Output)!.SequenceEqual(values),"Argument escaping changed");
    Check(result.Error=="stderr-tail","Stderr lost");
});
await Test("BothStreamsDrain",async()=>{
    var result=await Processes.RunAsync("dotnet",Child("flood"),root,15);
    Check(!result.TimedOut&&result.ExitCode==0&&result.Output.Length==200000&&result.Error.Length==200000,"Large simultaneous output lost");
});
await Test("FailedBuildInvalidatesCurrentAcceptance",async()=>{
    var isolated=Path.Combine(scratch,"failed-root");var dir=Path.Combine(isolated,"artifacts/x64/runtime-boot");Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(isolated,"WitOS.slnx"),"<Solution />");
    var prior=Path.Combine(dir,"acceptance.json");File.WriteAllText(prior,"{\"prior\":true}");
    var cwd=Environment.CurrentDirectory;var stderr=Console.Error;using var errors=new StringWriter();int exit;
    try{Console.SetError(errors);Environment.CurrentDirectory=isolated;exit=await DevTool.RunAsync(["runtime-boot-run"]);}finally{Environment.CurrentDirectory=cwd;Console.SetError(stderr);}
    Check(errors.ToString().Contains("ERROR:"),"Expected diagnostic missing");
    Check(exit!=0,"Missing source should fail build");Check(!File.Exists(prior),"Previous success still current after failure");
});
await Test("QuotedCompileProfile",()=>{
    const string input="cl.exe -DNO_STRESS_LOG -DFEATURE_SUSPEND_REDIRECTION -I\"C:\\Workspace With Spaces\\src\" -DNAME=\\\"value\\\"";
    var parsed=RuntimeGuestDriver.CompileProfile(input);
    Check(parsed.Contains("-IC:\\Workspace With Spaces\\src"),"Quoted include truncated");
    Check(parsed.Contains("-DNAME=\"value\""),"Escaped define quotes changed");return Task.CompletedTask;
});
await Test("MissingSecondWorkloadRejected",()=>{
    var valid=ProtocolFixtures.Valid;
    var broken=valid.Remove(valid.LastIndexOf(ProtocolFixtures.Worker,StringComparison.Ordinal),ProtocolFixtures.Worker.Length);
    Check(Accept(valid),"Valid two-base protocol rejected");Check(!Accept(broken),"Second workload evidence absent but accepted");return Task.CompletedTask;
});
await Test("BootChangesSelectManagedCi",()=>{
    var ci=File.ReadAllText(Path.Combine(root,".github/workflows/nativeaot.yml"));
    var pr=ci.IndexOf("  pull_request:",StringComparison.Ordinal);
    var dispatch=ci.IndexOf("  workflow_dispatch:",StringComparison.Ordinal);
    Check(pr>0&&dispatch>pr,"CI trigger sections missing");
    foreach(var section in new[]{ci[..pr],ci[pr..dispatch]})
        Check(section.Contains("- 'src/Boot.Uefi/**'",StringComparison.Ordinal)&&section.Contains("- 'tests/WitOS.Dev.Tests/**'",StringComparison.Ordinal),"Boot/tool tests missing from a trigger");
    foreach(var command in new[]{"runtime-source","runtime-config","runtime-boot-run","-- test","--pe-coverage","--pe-fuzz"})
        Check(ci.Contains(command,StringComparison.Ordinal),"Required M3 CI gate missing: "+command);
    return Task.CompletedTask;
});
bool Accept(string log)=>RuntimeBootProtocol.Validate(log,33,false);

await Test("ExitedJobWithExternalPipeOwner",async()=>{
    var report=Path.Combine(scratch,"exported-handles.txt");var time=Stopwatch.StartNew();
    try{
        var result=await Processes.RunAsync("dotnet",Child("export-pipes",Environment.ProcessId.ToString(),report),root,2);
        Check(File.Exists(report),"Child did not publish duplicate handles");
        Check(result.TimedOut&&result.ExitCode==0&&result.Output.Contains("before exported pipes"),"Expected timeout/partial output after parent exit");
        Check(time.Elapsed<TimeSpan.FromSeconds(6),"Cleanup waited for unrelated writer EOF");
    }finally{ChildHandles.CloseExported(report);}
});
await Test("TimeoutControlHandshake",async()=>{
    var result=await Processes.RunWithTimeoutControlAsync("dotnet",Child("stop-handshake"),root,2,async(input,output,token)=>{
        await input.WriteAsync(System.Text.Encoding.UTF8.GetBytes("hello\n"),token);await input.FlushAsync(token);
        while(!output().Contains("ready"))await Task.Delay(10,token);
        await input.WriteAsync(System.Text.Encoding.UTF8.GetBytes("quit\n"),token);await input.FlushAsync(token);
    });
    Check(result.TimedOut&&result.ExitCode==25&&result.Output.Contains("ready"),"Handshake shutdown changed timeout or missed acknowledgement");
});
await Test("CooperativeTimeoutStillTimesOut",async()=>{
    var time=Stopwatch.StartNew();
    var result=await Processes.RunWithTimeoutInputAsync("dotnet",Child("stop-input"),root,2,"quit\n");
    Check(result.TimedOut&&result.ExitCode==23&&result.Output.Contains("before cooperative stop")&&time.Elapsed<TimeSpan.FromSeconds(7),"Cooperative cleanup changed timeout/exit semantics");
    var fallback=await Processes.RunWithTimeoutInputAsync("dotnet",Child("wait"),root,2,"ignored\n");
    Check(fallback.TimedOut&&fallback.ExitCode!=0&&fallback.Output.Contains("before wait"),"Uncooperative child escaped forced cleanup");
});
await Test("TimeoutDuringOutputBurst",async()=>{
    var pid=Path.Combine(scratch,"burst.pid");var time=Stopwatch.StartNew();
    var result=await Processes.RunAsync("dotnet",Child("burst-wait",pid),root,2);
    Check(result.TimedOut&&result.Output.Contains("before burst")&&time.Elapsed<TimeSpan.FromSeconds(7),"Burst timeout/cleanup contract failed");
    try{using var child=Process.GetProcessById(int.Parse(File.ReadAllText(pid)));Check(child.HasExited,"Burst child survived timeout");}catch(ArgumentException){}
});
await Test("RootDeadlineAndPartialOutput",async()=>{
    var r=await Processes.RunAsync("dotnet",Child("wait"),root,2);
    Check(r.TimedOut&&r.Output.Contains("before wait"),"Root timeout lost status/output");
});
await Test("NormalExitReapsPipeIndependentDescendant",async()=>{
    var pid=Path.Combine(scratch,"normal-child.pid");var time=Stopwatch.StartNew();
    var r=await Processes.RunAsync("dotnet",Child("launcher-no-pipes",pid),root,15);
    Check(!r.TimedOut&&r.ExitCode==0,"Normal exit changed");
    Check(time.Elapsed<TimeSpan.FromSeconds(6),"Waited for a pipe-independent child to exit naturally");
    try{using var child=Process.GetProcessById(int.Parse(File.ReadAllText(pid)));Check(child.HasExited,"Daemon escaped command ownership");}catch(ArgumentException){}
});
await Test("LaunchFailureAndEnvironment",async()=>{
    bool failed=false;
    try{await Processes.RunAsync(Path.Combine(scratch,"does-not-exist.exe"),[],root,2);}catch(System.ComponentModel.Win32Exception){failed=true;}
    Check(failed,"Launch failure returned success");
    var r=await Processes.RunAsync("dotnet",Child("env"),root,15,new Dictionary<string,string>{{"WITOS_Q0_TEST_ENV","value with spaces ?"}});
    Check(r.Output=="value with spaces ?"&&!r.TimedOut,"Environment corrupted");
});
await Test("ConcurrentPipesStayIsolated",async()=>{
    var runs=await Task.WhenAll(Enumerable.Range(0,4).Select(i=>Processes.RunAsync("dotnet",Child("echo",i.ToString()),root,15)));
    for(int i=0;i<runs.Length;i++)Check(JsonSerializer.Deserialize<string[]>(runs[i].Output)!.Single()==i.ToString(),"Cross-process pipe leak");
});
await Test("ArgumentRoundtrip",()=>{
    string[] values=["cl.exe","","-IC:\\with spaces\\","-DNAME=\"two words\"","\\\"","a\"\"b","tab\tvalue"];
    Check(WindowsCommandLine.Parse(string.Join(' ',values.Select(WindowsCommandLine.Quote))).SequenceEqual(values),"Quote/parse mismatch");
    return Task.CompletedTask;
});
await Test("ManagedObjectSnapshotSchema",()=>{
    var hash=new string('a',64);
    using var valid=JsonDocument.Parse(JsonSerializer.Serialize(new{file="guest.pe",buildEvidence=new{inputs=new[]{new{file="NativeAotBoot.obj",sha256=hash}}}}));
    Check(RuntimeBootProtocol.SharedManagedObjectHash(valid.RootElement)==hash,"Nested managed input identity rejected");
    foreach(var bad in new[]{"{}",JsonSerializer.Serialize(new{inputs=new[]{new{file="NativeAotBoot.obj",sha256=hash}}}),
        JsonSerializer.Serialize(new{buildEvidence=new{inputs=new[]{new{file="NativeAotBoot.obj",sha256="bad"}}}}),
        JsonSerializer.Serialize(new{buildEvidence=new{inputs=new[]{new{file="NativeAotBoot.obj",sha256=hash},new{file="NativeAotBoot.obj",sha256=hash}}}})}){
        using var invalid=JsonDocument.Parse(bad);bool rejected=false;
        try{_ = RuntimeBootProtocol.SharedManagedObjectHash(invalid.RootElement);}catch(InvalidDataException){rejected=true;}
        Check(rejected,"Malformed/ambiguous managed identity accepted");
    }
    return Task.CompletedTask;
});
await Test("HostedSemanticContract",()=>{
    var valid=string.Join("\n",Enumerable.Repeat(RuntimeBootProtocol.Cycle[7..],4))+"\n"+
        RuntimeBootProtocol.Finalization[7..]+"\n"+RuntimeBootProtocol.ManagedThreads[7..]+"\n[RUNTIME] managed thread capacity reference passed: 4\n";
    Check(RuntimeBootProtocol.ValidateHosted(valid,42,false),"Valid hosted semantic workload rejected");
    Check(RuntimeBootProtocol.ValidateHostedLog(valid+"\nExit code: 42\n"),"Persisted hosted proof rejected");
    foreach(var invalid in new[]{"",valid.Replace(RuntimeBootProtocol.Cycle[7..]+"\n",""),valid+RuntimeBootProtocol.Cycle[7..]+"\n",
        valid.Replace("48 releases","12 releases"),valid.Replace("capacity reference","quota recovery"),valid+"unexpected stderr\n"})
        Check(!RuntimeBootProtocol.ValidateHosted(invalid,42,false),"Incomplete/mismatched hosted proof accepted");
    Check(!RuntimeBootProtocol.ValidateHosted(valid,0,false)&&!RuntimeBootProtocol.ValidateHosted(valid,42,true),"Hosted exit/timeout ignored");
    Check(!RuntimeBootProtocol.ValidateHostedLog(valid+"\nExit code: 0\n"),"Wrong persisted exit accepted");
    return Task.CompletedTask;
});
await Test("LegacyAndRuntimeFaultBoundaries",()=>{
    const string fault="[USER-FAULT] id=1 vector=14 error=0x0000000000000004 address=0x0000008000014E10 cs=0x0000000000000033\n";
    const string boundary="[TEST-PASS] User.Isolation\n";
    Check(DevTool.ValidateUserFaults(fault+fault+boundary+fault,2,1),"Valid legacy/runtime fault split rejected");
    Check(!DevTool.ValidateUserFaults(fault+boundary+fault+fault,2,1),"Extra runtime fault hid missing legacy fault");
    Check(!DevTool.ValidateUserFaults(fault+fault+fault+boundary,2,1),"Legacy fault hid missing runtime fault");
    Check(!DevTool.ValidateUserFaults(fault+fault+boundary+fault+"[USER-FAULT] malformed\n",2,1),"Malformed fault ignored");
    Check(!DevTool.ValidateUserFaults((fault+fault+boundary+fault).Replace("0033","0008"),2,1),"Supervisor fault accepted");
    return Task.CompletedTask;
});
await Test("PerBaseProtocolMutations",()=>{
    var good=ProtocolFixtures.Valid;
    var second=good.LastIndexOf("Runtime boot image base:",StringComparison.Ordinal);
    Check(Accept(good.Replace("\n","\r\n")),"CRLF protocol rejected");
    string[] mutations=[
        good.Replace(RuntimeBootProtocol.Cycle,""),
        good.Replace(RuntimeBootProtocol.LifecycleAudit,""),
        good.Replace(RuntimeBootProtocol.LifecycleAudit+"\n"+RuntimeBootProtocol.Cycle,RuntimeBootProtocol.Cycle+"\n"+RuntimeBootProtocol.LifecycleAudit),
        good.Replace("Runtime execution ticks/limit: 120/3000","Runtime execution ticks/limit: 3000/3000"),
        good.Replace(RuntimeBootProtocol.ThreadQuota,""),
        good.Replace("Runtime managed thread capacity failures: 4","Runtime managed thread capacity failures: 0"),
        good.Replace("[TEST-PASS] Runtime.ManagedStackOverflowContained",""),
        good.Replace("[USER] [RUNTIME] managed stack frame",""),
        good.Replace("/0x0000008000015000","/0x0000008000025000"),
        good.Replace("[TEST-PASS] Runtime.ManagedStackOverflowContained","[USER] [RUNTIME] unexpected stack-finally cleanup\n[TEST-PASS] Runtime.ManagedStackOverflowContained"),
        good.Replace(RuntimeBootProtocol.ManagedThreads, ""),
        good.Replace("Runtime parked foreign object waits: 2", "Runtime parked foreign object waits: 0"),
        good.Replace(RuntimeBootProtocol.Finalization, ""),
        good.Replace("48 releases + 8 resurrection passes", "12 releases + 1 resurrection passes"),
        good.Replace(RuntimeBootProtocol.ManagedEh, ""),
        good.Insert(second, RuntimeBootProtocol.ManagedEh+"\n"),
        good.Replace("0x0000008000180000","0x0000008000100000"),
        good+"[PANIC] injected\n",good+"[EXCEPTION] injected\n",
        good.Insert(second,ProtocolFixtures.Worker+"\n"),
        good.Replace("[USER] [RUNTIME] native TLS ready\n",""),
        good.Replace("[USER] [RUNTIME] native TLS ready","[USER] [RUNTIME] image published"),
        good.Replace("[USER] [RUNTIME] native TLS ready\n[USER] [RUNTIME] native initializers ready","[USER] [RUNTIME] native initializers ready\n[USER] [RUNTIME] native TLS ready"),
        good[..second]+good[second..].Replace("wmain returned 0x000000000000002A","wmain returned 0x000000000000002B"),
        good.Replace("0000000000000003/0000000000000001/0000000000000001/0000000000000001","0000000000000001/0000000000000000/0000000000000000/0000000000000001"),
        good.Replace("Runtime hardware faults read/write/divide/continue: 12/12/12/36","Runtime hardware faults read/write/divide/continue: 0/0/0/0"),
        good.Replace("Runtime managed commit failures: 2","Runtime managed commit failures: 0"),
        good.Replace("[USER] [RUNTIME] managed OOM recovery passed: 3 hard-limit + 1 backing-pressure", ""),
        good.Replace("Runtime init failure exit/commits: 0x00000000FFFFFFFF/1","Runtime init failure exit/commits: 0x00000000FFFFFFFF/0"),
        good.Replace("[USER] WitOS GC startup failure: hr = g_pGCHeap->Initialize();", ""),
        good.Replace("Runtime abrupt exit/report: 0x00000000FFFF0002/1/0/0/0","Runtime abrupt exit/report: 0x00000000FFFF0002/1/1/0/0"),
        good.Replace("[TEST-PASS] Runtime.AbruptWorkerContained", ""),
        good.Replace("Runtime orderly thread completions: 43","Runtime orderly thread completions: 0"),
        good.Replace("[TEST-PASS] Runtime.RelocationAndTeardown", "")];
    foreach(var mutation in mutations)Check(!Accept(mutation),"Malformed protocol accepted");
    Check(!RuntimeBootProtocol.Validate(good,33,true)&&!RuntimeBootProtocol.Validate(good,35,false),"Timeout/exit ignored");
    var header=File.ReadAllText(Path.Combine(root,"src/Kernel.Arch.X64/user_layout.h"));
    var bases=Regex.Matches(header,@"#define WIT_USER_IMAGE_(?:BASE|ALTERNATE) 0x([0-9a-fA-F]+)ULL").Select(m=>Convert.ToUInt64(m.Groups[1].Value,16));
    Check(bases.SequenceEqual(RuntimeBootProtocol.ImageBases),"Kernel/host base contract drift");return Task.CompletedTask;
});
await Test("AttemptHistoryAndFailureStages",async()=>{
    var home=Path.Combine(scratch,"history");var dir=Path.Combine(home,"artifacts/x64/runtime-boot");
    string? lastId=null;string? lastAcceptance=null;
    await RuntimeBootAttempt.RunAsync(home,"test",a=>{lastId=a.RunId;lastAcceptance=Path.Combine(a.RunDirectory,"acceptance.json");a.Publish(new{fixture="unit-test"});return Task.CompletedTask;});
    var saved=File.ReadAllText(lastAcceptance!);
    foreach(var stage in new[]{"discovery","build","hash","launch","boot"}){
        bool failed=false;
        try{await RuntimeBootAttempt.RunAsync(home,"test",a=>{
            Check(!File.Exists(Path.Combine(dir,"acceptance.json")),"Old success visible during attempt");
            using var running=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"current-run.json")));
            Check(running.RootElement.GetProperty("status").GetString()=="running"&&a.RunId!=lastId,"Run identity/status not refreshed");
            throw new InvalidDataException(stage);
        });}catch(InvalidDataException e){failed=e.Message==stage;}
        Check(failed&&!File.Exists(Path.Combine(dir,"acceptance.json")),"Failure published success");
        using var current=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"current-run.json")));
        Check(current.RootElement.GetProperty("status").GetString()=="failed"&&current.RootElement.GetProperty("error").GetString()==stage,"Failure status lost");
        Check(File.ReadAllText(Path.Combine(dir,"last-success.json"))==saved&&File.ReadAllText(lastAcceptance!)==saved,"Historical success changed");
    }
    await RuntimeBootAttempt.RunAsync(home,"retry",a=>{a.Publish(new{fixture="retry"});return Task.CompletedTask;});
    Check(File.Exists(Path.Combine(dir,"acceptance.json")),"Success after failure missing");
    Check(!Directory.EnumerateFiles(dir,"*.tmp",SearchOption.AllDirectories).Any(),"Atomic publication left temp files");
});
await Test("AttemptExclusionAndIncompleteRun",async()=>{
    var home=Path.Combine(scratch,"exclusive");
    await RuntimeBootAttempt.RunAsync(home,"outer",async a=>{
        bool busy=false;try{await RuntimeBootAttempt.RunAsync(home,"inner",b=>Task.CompletedTask);}catch(IOException){busy=true;}
        Check(busy,"Concurrent attempt acquired output ownership");a.Publish(new{fixture="exclusive"});
    });
    bool missing=false;try{await RuntimeBootAttempt.RunAsync(home,"no-publication",a=>Task.CompletedTask);}catch(InvalidOperationException){missing=true;}
    Check(missing&&!File.Exists(Path.Combine(home,"artifacts/x64/runtime-boot/acceptance.json")),"Incomplete attempt accepted");
});
await Test("LegacyHistoryAndPublicationFailure",async()=>{
    var home=Path.Combine(scratch,"legacy");var dir=Path.Combine(home,"artifacts/x64/runtime-boot");Directory.CreateDirectory(dir);
    var log=Path.Combine(home,"artifacts/logs/legacy.serial.log");Directory.CreateDirectory(Path.GetDirectoryName(log)!);File.WriteAllText(log,"historical bytes");
    File.WriteAllText(Path.Combine(dir,"acceptance.json"),JsonSerializer.Serialize(new{logs=new[]{new{file=log}}}));
    try{await RuntimeBootAttempt.RunAsync(home,"migration",a=>throw new IOException("injected"));}catch(IOException){}
    using(var prior=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"last-success.json")))){
        var preserved=prior.RootElement.GetProperty("logs")[0].GetProperty("file").GetString()!;
        File.WriteAllText(log,"new run bytes");Check(File.ReadAllText(preserved)=="historical bytes","Legacy logs were not snapshotted");
    }
    IEnumerable<int> Broken(){yield return 1;throw new IOException("publication");}
    try{await RuntimeBootAttempt.RunAsync(home,"publication",a=>{a.Publish(new{values=Broken()});return Task.CompletedTask;});}catch(IOException){}
    Check(!File.Exists(Path.Combine(dir,"acceptance.json")),"Partial serialization published success");
    using var status=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"current-run.json")));
    Check(status.RootElement.GetProperty("status").GetString()=="failed","Publication failure missing");
});
foreach(var test in Q1Tests.Cases(root,scratch))await Test(test.Name,test.Run);
if(args.Contains("--pe"))await Test("GuardedPeCorpus",()=>PeCorpus.RunAsync(root,scratch));
Console.WriteLine($"HOST TESTS: {passed} passed, {failures.Count} failed");return failures.Count==0?0:1;
