// An interactive console program under a file manager (plan step E4.2): what a program written on Windows asks of the
// terminal it runs on — a question answered with a line, then the whole screen held until a key — run by
// Arlecchino.Commander from its copy on the data volume. Nothing here knows WitOS but the lines the scenario reads.
using System.Runtime.InteropServices;

Console.Write("What is your name? ");
var name = Console.ReadLine() ?? "";
Console.WriteLine($"[INTERACTIVE] Hello, {name}!");

// The alternate screen, as a full-screen program takes it: the file manager hands its terminal over until it is given
// back.
Console.Write("\u001b[?1049h\u001b[2J\u001b[H");
Console.WriteLine("[INTERACTIVE] the screen is the program's: press a key");
var key = Console.ReadKey(intercept: true);
Console.Write("\u001b[?1049l");
Console.WriteLine($"[INTERACTIVE] the screen went back after '{key.KeyChar}' on {RuntimeInformation.RuntimeIdentifier}");
return 0;
