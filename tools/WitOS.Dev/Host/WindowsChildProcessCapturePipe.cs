using System.IO.Pipes;
using System.Security.Principal;

namespace WitOS.Dev.Host;

/// <summary>
/// Named pipe whose inheritable client end becomes a child's stdout or stderr; the parent reads the server end.
/// </summary>
internal sealed class WindowsChildProcessCapturePipe : IDisposable
{
    #region Constructors

    public WindowsChildProcessCapturePipe()
    {
        var name = "WitOS.Dev." + Guid.NewGuid().ToString("N");
        Reader = new(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Writer = new(".", name, PipeDirection.Out, PipeOptions.None, TokenImpersonationLevel.Identification,
            HandleInheritability.Inheritable);
    }

    #endregion

    #region Functions

    /// <summary>
    /// Connects both pipe ends.
    /// </summary>
    /// <param name="token">Cancels the connection.</param>
    public async Task ConnectAsync(CancellationToken token)
    {
        var connected = Reader.WaitForConnectionAsync(token);
        await Writer.ConnectAsync(token);
        await connected;
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Writer.Dispose();
        Reader.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// Parent end that reads the child output.
    /// </summary>
    public NamedPipeServerStream Reader { get; }

    /// <summary>
    /// Inheritable child end.
    /// </summary>
    public NamedPipeClientStream Writer { get; }

    #endregion
}
