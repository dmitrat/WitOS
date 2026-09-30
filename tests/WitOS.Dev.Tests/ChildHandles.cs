using System.Runtime.InteropServices;
internal static class ChildHandles
{
    internal static void MakeOutputNonInheritable()
    {
        foreach(int kind in new[]{-11,-12})if(!SetHandleInformation(GetStdHandle(kind),1,0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags);
    internal static void CloseOutput(){CloseHandle(GetStdHandle(-11));CloseHandle(GetStdHandle(-12));}
    internal static void ExportOutput(int receiver,string report)
    {
        // Receiver is the test coordinator that explicitly supplied its own PID.
        var target=OpenProcess(0x40,false,(uint)receiver);
        if(target==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try{
            var handles=new long[2];
            for(int i=0;i<2;++i){
                if(!DuplicateHandle(GetCurrentProcess(),GetStdHandle(-11-i),target,out var copy,0,false,2))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                handles[i]=copy.ToInt64();
            }
            File.WriteAllLines(report,handles.Select(h=>h.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }finally{CloseHandle(target);}
    }
    internal static void CloseExported(string report)
    {
        if(File.Exists(report))foreach(var value in File.ReadAllLines(report))
            CloseHandle(new IntPtr(long.Parse(value,System.Globalization.CultureInfo.InvariantCulture)));
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr OpenProcess(uint rights,bool inherit,uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DuplicateHandle(IntPtr sourceProcess,IntPtr source,IntPtr targetProcess,out IntPtr target,uint access,bool inherit,uint options);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
