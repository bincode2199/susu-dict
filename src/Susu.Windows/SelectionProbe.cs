using System.Runtime.InteropServices;

namespace Susu.Windows;

public static partial class SelectionProbe
{
    [LibraryImport("susu_windows_probe",EntryPoint="susu_read_selection")]
    private static unsafe partial int Read(nint window,char* text,uint capacity,out int reason);
    [LibraryImport("susu_windows_probe",EntryPoint="susu_read_ia2")]
    private static unsafe partial int ReadIa2(nint window,char* text,uint capacity,out int reason);
    [LibraryImport("susu_windows_probe",EntryPoint="susu_ia2_abi_probe")]
    private static partial int Ia2Abi();
    public static void VerifyIa2Abi() => Marshal.ThrowExceptionForHR(Ia2Abi());
    [LibraryImport("susu_windows_probe",EntryPoint="susu_selection_target")]
    public static partial void RunTarget(int password,int empty);

    public static unsafe (string Text,string Reason) ReadWindow(nint window,bool ia2Only=false)
    {
        char[] buffer=new char[65537];
        fixed(char* pointer=buffer)
        {
            int hr=ia2Only?ReadIa2(window,pointer,(uint)buffer.Length,out int reason):Read(window,pointer,(uint)buffer.Length,out reason);
            Marshal.ThrowExceptionForHR(hr);
            if(reason==2&&!ia2Only)Marshal.ThrowExceptionForHR(ReadIa2(window,pointer,(uint)buffer.Length,out reason));
            string status=reason switch {0=>"selected",1=>"password",2=>"unsupported",3=>"empty",4=>"focus-changed",_=>throw new InvalidOperationException("Unknown selection reason")};
            return (new string(buffer,0,Array.IndexOf(buffer,'\0')),status);
        }
    }
}
