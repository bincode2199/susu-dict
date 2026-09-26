# Helpers for attended interactive-desktop checks (screenshots, real SendInput chords, window lookup).
# Dot-source: . ./tools/interactive.ps1   Then: Snap <path>; Chord 0x12,0x44 (Alt+D); Fg; Windows 'susu'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
if (-not ('SusuInteractive.Desk' -as [type])) {
Add-Type -ReferencedAssemblies System.Drawing, System.Windows.Forms -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
namespace SusuInteractive {
public static class Desk {
  [StructLayout(LayoutKind.Sequential)] struct KI { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] struct MI { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Explicit)] struct U { [FieldOffset(0)] public KI k; [FieldOffset(0)] public MI m; }
  [StructLayout(LayoutKind.Sequential)] struct IN { public uint type; public U u; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, IN[] i, int cb);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  public static string Title(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
  public static string Class(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static uint Pid(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
  public static string[] Visible(uint pid) {
    var l = new List<string>();
    EnumWindows((h, _) => { if (IsWindowVisible(h) && (pid == 0 || Pid(h) == pid)) { RECT r; GetWindowRect(h, out r); l.Add(h + "|" + Class(h) + "|" + Title(h) + "|" + r.L + "," + r.T + "," + r.R + "," + r.B); } return true; }, IntPtr.Zero);
    return l.ToArray();
  }
  static IN Key(ushort vk, bool up) { var i = new IN { type = 1 }; i.u.k.vk = vk; i.u.k.flags = up ? 2u : 0u; return i; }
  public static void Chord(params ushort[] vks) {
    var l = new List<IN>(); foreach (var v in vks) l.Add(Key(v, false)); for (int i = vks.Length - 1; i >= 0; i--) l.Add(Key(vks[i], true));
    SendInput((uint)l.Count, l.ToArray(), Marshal.SizeOf(typeof(IN)));
  }
  public static void Type(string text) {
    var l = new List<IN>();
    foreach (char c in text) { foreach (bool up in new[] { false, true }) { var i = new IN { type = 1 }; i.u.k.scan = c; i.u.k.flags = 4u | (up ? 2u : 0u); l.Add(i); } }
    SendInput((uint)l.Count, l.ToArray(), Marshal.SizeOf(typeof(IN)));
  }
  public static void Click(int x, int y) {
    SetCursorPos(x, y);
    var d = new IN { type = 0 }; d.u.m.flags = 2; var u = new IN { type = 0 }; u.u.m.flags = 4;
    SendInput(2, new[] { d, u }, Marshal.SizeOf(typeof(IN)));
  }
  public static void Snap(string path) {
    var b = System.Windows.Forms.SystemInformation.VirtualScreen;
    using (var bmp = new System.Drawing.Bitmap(b.Width, b.Height))
    using (var g = System.Drawing.Graphics.FromImage(bmp)) { g.CopyFromScreen(b.Left, b.Top, 0, 0, bmp.Size); bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
  }
}}
'@
}
function Fg { [SusuInteractive.Desk]::GetForegroundWindow() }
function Snap([string]$Path) { [SusuInteractive.Desk]::Snap((Join-Path (Get-Location) $Path)) }
function Chord([int[]]$Keys) { [SusuInteractive.Desk]::Chord([uint16[]]$Keys) }
function Windows([string]$ProcessName) { foreach ($p in Get-Process $ProcessName -EA SilentlyContinue) { [SusuInteractive.Desk]::Visible([uint32]$p.Id) } }
