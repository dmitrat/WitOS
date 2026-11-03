using System.Runtime.InteropServices;

// Built in the guest by the layer's .NET SDK (plan step E4.4), then run by dotnet from what the build wrote.
Console.WriteLine($"[GUEST-BUILD] built in the guest, running on {RuntimeInformation.RuntimeIdentifier}");
