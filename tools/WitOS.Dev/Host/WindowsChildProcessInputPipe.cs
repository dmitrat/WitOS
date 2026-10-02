using System.IO.Pipes;
using System.Security.Principal;

namespace WitOS.Dev.Host;

/// <summary>
/// Named pipe whose inheritable client end becomes a child's stdin; the parent writes the server end.
/// </summary>
internal sealed class WindowsChildProcessInputPipe : IDisposable
{
    #region Constructors

    public WindowsChildProcessInputPipe()
    {
        var name = "WitOS.Dev.Input." + Guid.NewGuid().ToString("N");
        Writer = new(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Reader = new(".", name, PipeDirection.In, PipeOptions.None, TokenImpersonationLevel.Identification,
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
        var connected = Writer.WaitForConnectionAsync(token);
        await Reader.ConnectAsync(token);
        await connected;
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Reader.Dispose();
        Writer.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// Parent end that writes the child input.
    /// </summary>
    public NamedPipeServerStream Writer { get; }

    /// <summary>
    /// Inheritable child end.
    /// </summary>
    public NamedPipeClientStream Reader { get; }

    #endregion
}
