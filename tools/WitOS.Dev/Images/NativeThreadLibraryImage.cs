using WitOS.Dev.Host;

namespace WitOS.Dev.Images;

internal static class NativeThreadLibraryImage
{
    #region Functions

    internal static async Task<string> BuildAsync(string root, string output, string msvc)
    {
        Directory.CreateDirectory(output);
        var obj = Path.Combine(output, "thread-notification.obj");
        var machine = Path.Combine(output, "thread-notification-x64.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo", "/c", "/TC", "/std:c17", "/W4", "/WX", "/GS-", "/Zl", "/O2", "/Fo" + obj, Path.Combine(root, "tests/User.X64/library_thread_entry.c")], root);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + machine, Path.Combine(root, "tests/User.X64/library_entry_x64.asm")], root);
        var dll = Path.Combine(output, "threadnotify.dll");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"), ["/nologo", "/dll", "/entry:LibraryThreadEntry", "/nodefaultlib", "/machine:x64", "/subsystem:native", "/fixed:no", "/dynamicbase", "/incremental:no", "/Brepro", "/out:" + dll, obj, machine], root);
        return dll;
    }

    #endregion
}
