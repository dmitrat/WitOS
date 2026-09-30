internal static class ProtocolFixtures
{
    internal const string Worker="[USER] [RUNTIME] worker attach/detach/reuse/rollback and foreign GC/hijack/service-guard/exit-GC passed";
    internal static readonly string[] LegacyMarkers=["[TEST-PASS] Runtime.InvalidHandoffTeardown","[TEST-PASS] Runtime.MemoryProfile","[RUNTIME] image published","[RUNTIME] native TLS ready","[RUNTIME] native initializers ready","[RUNTIME] entering upstream wmain",Worker,"[RUNTIME] wmain returned 0x000000000000002A","[TEST-PASS] Runtime.ManagedBootAndGc","[TEST-PASS] Runtime.RelocationAndTeardown"];
    internal static string Block(string address)=>$"""
Runtime boot image base: {address}
Runtime boot load status: 0
[TEST-PASS] Runtime.MemoryProfile
[USER] [RUNTIME] image published
[USER] [RUNTIME] native TLS ready
[USER] [RUNTIME] native initializers ready
[USER] [RUNTIME] entering upstream wmain
[USER] [RUNTIME] hijack attempts/redirects/returns/unsafe: 0000000000000003/0000000000000001/0000000000000001/0000000000000001
[USER] [RUNTIME] managed OOM recovery passed: 3 hard-limit + 1 backing-pressure
[USER] [RUNTIME] managed EH workers passed: 2 (filters/rethrow/nested-finally/native-release)
{Worker}
{string.Join("\n",Enumerable.Repeat("[USER] [RUNTIME] managed lifecycle audit passed: main+finalizer\n[USER] [RUNTIME] integration cycle passed",4))}
[USER] [RUNTIME] managed finalization passed: 48 releases + 8 resurrection passes + suppression
[USER] [RUNTIME] managed threads passed: 28 (Thread/Join/Monitor/TLS/GC)
[USER] [RUNTIME] managed thread quota recovery passed: 4
[USER] [RUNTIME] wmain returned 0x000000000000002A last-error=0x0000000000000000
Runtime boot state/exit/rip/owned: 3/0x000000000000002A/0x0000000000000000/435
Runtime hardware faults read/write/divide/continue: 12/12/12/36
Runtime managed commit failures: 2
Runtime parked foreign object waits: 2
Runtime managed thread capacity failures: 4
Runtime orderly thread completions: 43
Runtime execution ticks/limit: 120/3000
""";
    internal static string StackOverflow(string address)=>$"""
Runtime managed stack overflow base: {address}
[USER] [RUNTIME] image published
[USER] [RUNTIME] native TLS ready
[USER] [RUNTIME] native initializers ready
[USER] [RUNTIME] entering upstream wmain
[USER] [RUNTIME] managed stack overflow probe entered
[USER] [RUNTIME] managed stack frame
[USER] [RUNTIME] managed stack frame
[USER] [RUNTIME] managed stack frame
[USER] [RUNTIME] managed stack frame
[USER-FAULT] id=900 vector=14 error=0x0000000000000004 address=0x0000008000014E10 cs=0x0000000000000033
Runtime stack fault vector/error/address/low: 14/0x0000000000000004/0x0000008000014E10/0x0000008000015000
[TEST-PASS] Runtime.ManagedStackOverflowContained
""";
    internal static string InitFailure(string address)=>$"""
Runtime init failure base: {address}
[USER] [RUNTIME] image published
[USER] [RUNTIME] native TLS ready
[USER] [RUNTIME] native initializers ready
[USER] [RUNTIME] entering upstream wmain
[USER] WitOS GC startup failure: hr = g_pGCHeap->Initialize();
[USER] WitOS startup failure: InitializeGC()
[USER] WitOS startup failure: InitDLL(PalGetModuleHandleFromPointer((void*)&RhInitialize))
[USER] [RUNTIME] wmain returned 0x00000000FFFFFFFF last-error=0x00000000000000CB
Runtime init failure exit/commits: 0x00000000FFFFFFFF/1
[TEST-PASS] Runtime.GcInitFailureTeardown
""";
    internal static string Abrupt(int mode,string address)=>$"""
Runtime abrupt mode/base: {mode}/{address}
[USER] [RUNTIME] image published
[USER] [RUNTIME] native TLS ready
[USER] [RUNTIME] native initializers ready
[USER] [RUNTIME] entering upstream wmain
{(mode>=2?"[USER] [NATIVE-FAIL-FAST] code=0xC000001D address=0x0000008000102D00 rip=0x0000008000102D00":"")}
Runtime abrupt exit/report: {(mode<2?"0x00000000FFFF0002":"0x00000000C000001D")}/1/0/0/0
[TEST-PASS] Runtime.AbruptWorkerContained
""";
    internal static string NativeFault(string address)=>$"""
Runtime native fault base: {address}
[USER] [RUNTIME] image published
[USER] [RUNTIME] native TLS ready
[USER] [RUNTIME] native initializers ready
[USER] [RUNTIME] entering upstream wmain
[USER] [RUNTIME] native fault probe entered
[USER] [NATIVE-FAIL-FAST] code=0xC000001D address={address} rip={address}
[TEST-PASS] Runtime.NativeFaultContained
""";
    internal static string Valid=>("[TEST-PASS] Runtime.InvalidHandoffTeardown\n"+
        NativeFault("0x0000008000100000")+"\n"+NativeFault("0x0000008000180000")+"\n"+
        StackOverflow("0x0000008000100000")+"\n"+StackOverflow("0x0000008000180000")+"\n"+
        InitFailure("0x0000008000100000")+"\n"+InitFailure("0x0000008000180000")+"\n"+
        string.Join("\n",Enumerable.Range(0,4).SelectMany(mode=>new[]{Abrupt(mode,"0x0000008000100000"),Abrupt(mode,"0x0000008000180000")}))+"\n"+
        Block("0x0000008000100000")+"\n"+Block("0x0000008000180000")+"\n"+Block("0x0000008000100000")+"\n"+Block("0x0000008000180000")+"\n[TEST-PASS] Runtime.ManagedBootAndGc\n[TEST-PASS] Runtime.RelocationAndTeardown\n").Replace("\r\n","\n");
}
