using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WitOS.Dev;

internal static class WindowsCommandLine
{
    public static string Quote(string argument)
    {
        if(argument.IndexOf('\0')>=0)throw new ArgumentException("NUL in process argument.");
        if(argument.Length>0&&!argument.Any(char.IsWhiteSpace)&&!argument.Contains('"'))return argument;
        var result=new StringBuilder("\"");int slashes=0;
        foreach(char c in argument){
            if(c=='\\'){++slashes;continue;}
            if(c=='"'){result.Append('\\',slashes*2+1).Append(c);slashes=0;continue;}
            result.Append('\\',slashes).Append(c);slashes=0;
        }
        return result.Append('\\',slashes*2).Append('"').ToString();
    }

    public static string[] Parse(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if(command.Contains('\0'))throw new ArgumentException("NUL in compiler command.");
        var memory=CommandLineToArgvW(command,out int count);
        if(memory==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
        try{return Enumerable.Range(0,count).Select(i=>Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory,i*IntPtr.Size))!).ToArray();}
        finally{LocalFree(memory);}
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern IntPtr CommandLineToArgvW(string command,out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
