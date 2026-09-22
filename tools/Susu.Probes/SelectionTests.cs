using System.Diagnostics;
using System.Text.Json;
using Susu.Windows;

internal sealed record SelectionResult(string Text,string Reason);
internal static class SelectionTests
{
    private static Process Start(params string[] args)
    {
        var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in args)info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("Selection process did not start.");
    }

    internal static void Run()
    {
        foreach(string mode in new[]{"text","empty","password"})
        {
            using var target=Start("--selection-target",mode);
            try
            {
                using var startup=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                string? line=target.StandardOutput.ReadLineAsync(startup.Token).AsTask().GetAwaiter().GetResult();
                if(!long.TryParse(line,out long hwnd)||hwnd==0)throw new InvalidOperationException("Selection target did not create its edit control.");
                var deadline=Stopwatch.StartNew();
                using var helper=Start("--selection-child",hwnd.ToString(System.Globalization.CultureInfo.InvariantCulture));
                string output=ReadBounded(helper,Math.Max(1,500-(int)deadline.ElapsedMilliseconds));
                var result=JsonSerializer.Deserialize(output,ProbeJson.Default.SelectionResult) ?? throw new InvalidOperationException("Missing selection result.");
                string expected=mode switch{"text"=>"selected","empty"=>"empty",_=>"password"};
                if(result.Reason!=expected || (mode=="text" && result.Text!="selected text") || (mode!="text" && result.Text.Length!=0))
                    throw new InvalidOperationException($"Selection {mode}: unexpected result {result.Reason}.");
            }
            finally{if(!target.HasExited){target.Kill();target.WaitForExit(2000);}}
        }
        using var slow=Start("--selection-stall");
        var watch=Stopwatch.StartNew();
        try{ReadBounded(slow,500);throw new InvalidOperationException("Stalled helper unexpectedly returned.");}
        catch(TimeoutException){if(!slow.HasExited||watch.ElapsedMilliseconds>1500)throw new InvalidOperationException("Stalled helper cleanup was not bounded.");}
    }

    private static string ReadBounded(Process helper,int milliseconds)
    {
        using var timeout=new CancellationTokenSource(milliseconds);
        try
        {
            char[] buffer=new char[1024*1024+1];
            int total=0;
            while(total<buffer.Length)
            {
                int count=helper.StandardOutput.ReadAsync(buffer.AsMemory(total),timeout.Token).AsTask().GetAwaiter().GetResult();
                if(count==0)break;
                total+=count;
            }
            if(total==buffer.Length){if(!helper.HasExited)helper.Kill();helper.WaitForExit(1000);throw new InvalidOperationException("Selection frame exceeded limit.");}
            string output=new(buffer,0,total);
            helper.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            if(helper.ExitCode!=0)throw new InvalidOperationException($"Selection helper exit {helper.ExitCode}.");
            return output;
        }
        catch(OperationCanceledException)
        {
            if(!helper.HasExited)helper.Kill();
            helper.WaitForExit(1000);
            throw new TimeoutException("Selection helper exceeded its total deadline.");
        }
    }
}
