using System.Diagnostics;
using WitOS.Dev;
internal static class QemuCleanupTests
{
    internal static async Task ProtocolAsync()
    {
        foreach(var reject in new[]{false,true})
        {
            using var control=new QemuControl();
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var port=int.Parse(control.Argument.Split(':')[2].Split(',')[0]);
            var commands=new List<string>();
            var peer=Task.Run(async()=>
            {
                using var client=new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(System.Net.IPAddress.Loopback,port,stop.Token);
                using var stream=client.GetStream();
                using var reader=new StreamReader(stream);
                async Task Send(string line)=>await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line+"\r\n"),stop.Token);
                await Send("{\"QMP\":{\"version\":{},\"capabilities\":[]}}");
                commands.Add((await reader.ReadLineAsync(stop.Token))!);
                if(reject){await Send("{\"error\":{\"class\":\"GenericError\"},\"id\":\"witos-capabilities\"}");return;}
                await Send("{\"return\":{},\"id\":\"unrelated\"}");
                await Send("{\"return\":{},\"id\":\"witos-capabilities\"}");
                commands.Add((await reader.ReadLineAsync(stop.Token))!);
                await Send("{\"event\":\"SHUTDOWN\"}");
                await Send("{\"return\":{},\"id\":\"witos-quit\"}");
            });
            bool refused=false;
            try{await control.QuitAsync(stop.Token);}catch(InvalidDataException){refused=true;}
            await peer;
            if(refused!=reject||control.QuitAcknowledged==reject||commands.Count!=(reject?1:2)||
                !commands[0].Contains("qmp_capabilities")||(!reject&&!commands[1].Contains("quit")))
                throw new InvalidDataException("QMP sequencing/error handling failed.");
        }
    }

    internal static async Task RunAsync(string root,string output)
    {
        Toolchain.RequireQemu(root);
        for(var round=0;round<12;++round)
        {
            using var control=new QemuControl();var clock=Stopwatch.StartNew();
            ProcessResult result;
            try
            {
                result=await Processes.RunWithFilesAsync(Toolchain.Qemu(root),
                    ["-machine","none","-display","none","-monitor","none","-serial","none","-nodefaults","-S","-qmp",control.Argument],root,1,Path.Combine(output,$"qmp-{round}.stdout.log"),Path.Combine(output,$"qmp-{round}.stderr.log"),control.QuitAsync);
            }
            finally {await File.WriteAllTextAsync(Path.Combine(output,$"qmp-{round}.log"),control.Transcript);}
            if(!result.TimedOut||result.ExitCode!=0||clock.Elapsed>TimeSpan.FromSeconds(7)||!control.QuitAcknowledged)
                throw new InvalidDataException($"QMP shutdown failed: round={round}, exit={result.ExitCode}, timeout={result.TimedOut}, seconds={clock.Elapsed.TotalSeconds}");
        }
        Console.WriteLine("PASS: 12 QEMU socket-QMP timeouts with native process/job completion.");
    }
}
