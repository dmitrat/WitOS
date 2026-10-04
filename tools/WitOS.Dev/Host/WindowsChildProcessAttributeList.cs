using System.Runtime.InteropServices;
using WitOS.Dev.Host.Native;

namespace WitOS.Dev.Host;

/// <summary>
/// Two-entry PROC_THREAD_ATTRIBUTE_LIST that owns the handle arrays it references.
/// </summary>
internal sealed class WindowsChildProcessAttributeList : IDisposable
{
    #region Fields

    private readonly List<IntPtr> m_values = [];

    #endregion

    #region Constructors

    public WindowsChildProcessAttributeList()
    {
        nuint size = 0;
        Kernel32.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
        Pointer = Marshal.AllocHGlobal(checked((int)size));
        if (!Kernel32.InitializeProcThreadAttributeList(Pointer, 2, 0, ref size))
        {
            Marshal.FreeHGlobal(Pointer);
            throw Kernel32.LastError();
        }
    }

    #endregion

    #region Functions

    /// <summary>
    /// Adds a handle-array attribute; the copied array lives until disposal.
    /// </summary>
    /// <param name="key">PROC_THREAD_ATTRIBUTE_* key.</param>
    /// <param name="handles">Handles to store.</param>
    public void Add(nuint key, IntPtr[] handles)
    {
        var data = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
        m_values.Add(data);
        Marshal.Copy(handles, 0, data, handles.Length);
        if (!Kernel32.UpdateProcThreadAttribute(Pointer, 0, key, data, (nuint)(handles.Length * IntPtr.Size), IntPtr.Zero,
                IntPtr.Zero))
        {
            throw Kernel32.LastError();
        }
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Kernel32.DeleteProcThreadAttributeList(Pointer);
        foreach (var value in m_values)
        {
            Marshal.FreeHGlobal(value);
        }
        Marshal.FreeHGlobal(Pointer);
    }

    #endregion

    #region Properties

    /// <summary>
    /// Native attribute list passed to CreateProcessW.
    /// </summary>
    public IntPtr Pointer { get; }

    #endregion
}
