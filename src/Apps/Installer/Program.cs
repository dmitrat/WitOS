using System;
using System.Threading.Tasks;
using Arlecchino.Hosting;
using Microsoft.Extensions.Hosting;
using WitOS.Installer;

// The installer of base applications (plan step C4.3): with a verb, a command for scripts; without one, the screen.
string verb;
System.Collections.Generic.IReadOnlyList<string> operands;
InstallerOptions options;
try
{
    (verb, operands, options) = InstallerOptions.Parse(args);
}
catch (ArgumentException error)
{
    await Console.Error.WriteLineAsync(error.Message);
    return 2;
}
if (verb.Length > 0)
    return await InstallerCommands.RunAsync(verb, operands, options, Console.Out, Console.Error);

var builder = ArlecchinoHost.CreateBuilder([]);
InstallerScreen.Add(builder.Services.AddArlecchino(InstallerScreen.Configure), options);
await builder.Build().RunAsync();
return 0;
