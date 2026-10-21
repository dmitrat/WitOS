// An ordinary ASP.NET Core application (plan step W3.2, Implementation Strategy §67): the web SDK's minimal API on
// Kestrel, built by the stock SDK without a RID and run unchanged on WitOS through dotnet. It serves HTTP on port 8080,
// where the runner's requests arrive through QEMU's port forward, and HTTPS on port 8443 with a certificate it makes at
// start, which it requests itself through HttpClient once it listens (the ports come from the configuration, so that a
// host test runs it on free ones); a request of /stop ends it. Nothing here knows WitOS: the console logger writes
// without colours so that a log judged line by line stays plain.
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging.Console;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.ColorBehavior = LoggerColorBehavior.Disabled;
    options.SingleLine = true;
});
var httpPort = builder.Configuration.GetValue("HttpPort", 8080);
var httpsPort = builder.Configuration.GetValue("HttpsPort", 8443);
using var certificate = MakeCertificate();
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(httpPort);
    kestrel.ListenAnyIP(httpsPort, listen => listen.UseHttps(certificate));
});

var app = builder.Build();
var served = 0;
var selfCheck = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
app.Use(async (context, next) =>
{
    await next();
    Interlocked.Increment(ref served);
    Console.WriteLine($"[ASPNET] {context.Request.Method} {context.Request.Path} from {context.Connection.RemoteIpAddress} answered {context.Response.StatusCode}");
});
app.MapGet("/", () => "Hello from ASP.NET Core on WITOS");
app.MapGet("/json", () => Results.Json(new
{
    os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.OSArchitecture.ToString(),
    framework = RuntimeInformation.FrameworkDescription,
    processors = Environment.ProcessorCount
}));
app.MapPost("/echo", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    return await reader.ReadToEndAsync();
});
app.MapGet("/stop", async (IHostApplicationLifetime lifetime) =>
{
    // The self-check ends before the server does, so that its line is in the log whatever the order of the requests.
    await selfCheck.Task;
    lifetime.StopApplication();
    return "stopping";
});
app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
    Console.WriteLine($"[ASPNET] listening on {string.Join(" and ", addresses)}");
    var https = addresses.FirstOrDefault(address => address.StartsWith("https://", StringComparison.Ordinal));
    _ = SelfCheckAsync(https is null ? httpsPort : new Uri(https).Port);
});
await app.RunAsync();
Console.WriteLine($"[ASPNET] served {served} requests, exit 0");
return 0;

// Requests /json over HTTPS from the application itself, trusting its own certificate by thumbprint: the server's TLS
// and the client's in one process.
async Task SelfCheckAsync(int port)
{
    try
    {
        using var handler = new SocketsHttpHandler { UseProxy = false };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, remote, _, _) =>
            remote is not null && new X509Certificate2(remote).Thumbprint == certificate.Thumbprint;
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        var text = await client.GetStringAsync($"https://127.0.0.1:{port}/json");
        var ok = text.Contains("\"framework\"", StringComparison.Ordinal);
        Console.WriteLine($"[ASPNET] https self-check: {(ok ? "ok" : "unexpected answer")}, {text.Length} characters");
        selfCheck.TrySetResult(text);
    }
    catch (Exception exception)
    {
        Console.WriteLine($"[ASPNET] https self-check: failed, {exception.GetType().Name}: {exception.Message}");
        selfCheck.TrySetResult("");
    }
}

// A self-signed certificate for the loopback, made at start; through PKCS#12, so that the key is one every host's TLS
// serves with (Windows serves no ephemeral key).
static X509Certificate2 MakeCertificate()
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var request = new CertificateRequest("CN=WitOS WebApp", key, HashAlgorithmName.SHA256);
    var names = new SubjectAlternativeNameBuilder();
    names.AddIpAddress(System.Net.IPAddress.Loopback);
    names.AddDnsName("localhost");
    request.CertificateExtensions.Add(names.Build());
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
    using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
    return X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx, "webapp"), "webapp",
        X509KeyStorageFlags.Exportable | (OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.DefaultKeySet));
}
