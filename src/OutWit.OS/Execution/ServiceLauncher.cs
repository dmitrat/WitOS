using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OutWit.OS.Native;
using OutWit.OS.Resources;

namespace OutWit.OS.Execution;

/// <summary>
/// Starts a program of the boot package through the process manager, granting it a capability (witos/spawn.h's
/// witos_manager_spawn, plan step D1.2): how a service starts its helpers, the device manager its drivers.
/// </summary>
public static class ServiceLauncher
{
    #region Functions

    /// <summary>
    /// Starts the program at path with the arguments given after its path, in "/", with no environment, granting it the
    /// capability, which moves into its start message.
    /// </summary>
    /// <param name="path">A program of the boot package.</param>
    /// <param name="arguments">The arguments after argv[0], the path.</param>
    /// <param name="grant">The capability the program starts with, moved; null for none.</param>
    /// <returns>The new process's handle (WAIT, QUERY, KILL, DUPLICATE, TRANSFER).</returns>
    /// <exception cref="CapabilityException">The process manager refused the start; the grant is gone either way.</exception>
    public static unsafe KernelHandle Start(string path, IReadOnlyList<string> arguments, KernelHandle? grant)
    {
        var strings = new List<IntPtr>(arguments.Count + 2);
        try
        {
            strings.Add(Marshal.StringToCoTaskMemUTF8(path));
            foreach (var argument in arguments)
                strings.Add(Marshal.StringToCoTaskMemUTF8(argument));
            var argv = stackalloc byte*[strings.Count + 1];
            for (var i = 0; i < strings.Count; i++)
                argv[i] = (byte*)strings[i];
            argv[strings.Count] = null;
            byte* envp = null;
            ulong process = 0;
            var granted = grant?.Value ?? 0;
            var error = Libwitos.ManagerSpawn(argv[0], argv, &envp, granted, &process);
            grant?.Moved(); // moved into the start message, or closed by libwitos when the start failed
            CapabilityException.ThrowOnError($"the start of {path}", error);
            return new KernelHandle(process);
        }
        finally
        {
            foreach (var pointer in strings)
                Marshal.FreeCoTaskMem(pointer);
        }
    }

    #endregion
}
