using System.Runtime.InteropServices;
using Susu.Windows.Selection;

namespace Susu.Windows;

/// <summary>
/// F00 selection probe harness. Since F08.1 the reads go through the production helper code
/// (<see cref="SelectionHost"/>, susu_selection.dll); only the synthetic target window stays in the probe DLL.
/// </summary>
public static partial class SelectionProbe
{
    public static void VerifyIa2Abi() => SelectionHost.SelfTest();
    [LibraryImport("susu_windows_probe",EntryPoint="susu_selection_target")]
    public static partial void RunTarget(int password,int empty);
}
