using System.Runtime.InteropServices;

namespace Susu.Windows;

public static partial class SelectionProbe
{
    [LibraryImport("susu_windows_probe",EntryPoint="susu_read_selection")]
    private static unsafe partial int Read(nint window,char* text,uint capacity,out int reason);
    [LibraryImport("susu_windows_probe",EntryPoint="susu_selection_target")]
    public static partial void RunTarget(int password,int empty);

    public static unsafe (string Text,string Reason) ReadWindow(nint window)
    {
        char[] buffer=new char[65537];
        fixed(char* pointer=buffer)
        {
            Marshal.ThrowExceptionForHR(Read(window,pointer,(uint)buffer.Length,out int reason));
            string status=reason switch {0=>"selected",1=>"password",2=>"unsupported",3=>"empty",_=>throw new InvalidOperationException("Unknown selection reason")};
            return (new string(buffer,0,Array.IndexOf(buffer,'\0')),status);
        }
    }
}
