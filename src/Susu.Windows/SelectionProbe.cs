using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Susu.Windows;

public static partial class SelectionProbe
{
    [LibraryImport("susu_windows_probe",EntryPoint="susu_read_selection_ex")]
    private static unsafe partial int Read(nint window,char* text,uint capacity,out int reason,double* rect);
    [LibraryImport("susu_windows_probe",EntryPoint="susu_read_ia2")]
    private static unsafe partial int ReadIa2(nint window,char* text,uint capacity,out int reason);
    [LibraryImport("susu_windows_probe",EntryPoint="susu_ia2_abi_probe")]
    private static partial int Ia2Abi();
    public static void VerifyIa2Abi() => Marshal.ThrowExceptionForHR(Ia2Abi());
    [LibraryImport("susu_windows_probe",EntryPoint="susu_selection_target")]
    public static partial void RunTarget(int password,int empty);

    /// <summary>Selection result: Source is "uia", "ia2" or "none"; Rect is physical screen pixels (UIA only).</summary>
    public sealed record Result(string Text,string Reason,string Source,double[] Rect,double UiaMs,double Ia2Ms);

    public static unsafe Result ReadWindow(nint window,bool ia2Only=false)
    {
        char[] buffer=new char[65537];
        double* rect=stackalloc double[4];
        rect[0]=rect[1]=rect[2]=rect[3]=0;
        double uiaMs=0,ia2Ms=0;
        string source="none";
        int reason;
        fixed(char* pointer=buffer)
        {
            var timer=Stopwatch.StartNew();
            if(!ia2Only)
            {
                Marshal.ThrowExceptionForHR(Read(window,pointer,(uint)buffer.Length,out reason,rect));
                uiaMs=timer.Elapsed.TotalMilliseconds;
                source="uia";
            }
            else reason=2;
            if(reason is 2 or 3)
            {
                // Second level: IA2 through MSAA's focus chain. Also tried when UIA reports an empty
                // selection: Firefox 156 exposes a TextPattern whose selection is empty while IA2 has it
                // (F00 SEL01). IA2 returns only a real selection, never the caret paragraph.
                int uiaReason=reason;
                timer.Restart();
                Marshal.ThrowExceptionForHR(ReadIa2(window,pointer,(uint)buffer.Length,out int ia2Reason));
                ia2Ms=timer.Elapsed.TotalMilliseconds;
                if(ia2Reason is 0 or 1){reason=ia2Reason;source="ia2";}
                else{reason=uiaReason==3?3:ia2Reason;source=uiaReason==3?"uia":"none";buffer[0]='\0';}
            }
            string status=reason switch {0=>"selected",1=>"password",2=>"unsupported",3=>"empty",4=>"focus-changed",_=>throw new InvalidOperationException("Unknown selection reason")};
            return new Result(new string(buffer,0,Array.IndexOf(buffer,'\0')),status,source,[rect[0],rect[1],rect[2],rect[3]],uiaMs,ia2Ms);
        }
    }
}
