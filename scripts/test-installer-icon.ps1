[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$ReferenceIcon = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (!$ReferenceIcon) { $ReferenceIcon = Join-Path $repoRoot 'artifacts\assets\Host2VMRelay.Setup.ico' }
$folder = Join-Path $repoRoot 'artifacts\checks\installer-icon'
New-Item -ItemType Directory -Force $folder | Out-Null
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$ReferenceIcon = (Resolve-Path -LiteralPath $ReferenceIcon).Path
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
public static class H2VMSetupIconCheck {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct ShellFileInfo {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string displayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=80)] public string typeName;
    }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern uint PrivateExtractIcons(string file, int index, int width, int height,
        [Out] IntPtr[] icons, [Out] uint[] ids, uint count, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string file, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    private static Bitmap Extract(string file, int pixels) {
        var handles=new IntPtr[1]; var ids=new uint[1];
        uint count=PrivateExtractIcons(file,0,pixels,pixels,handles,ids,1,0);
        if(count!=1 || handles[0]==IntPtr.Zero) throw new IOException("Icon extraction failed: "+file+" @ "+pixels);
        try { using(var icon=Icon.FromHandle(handles[0])) return icon.ToBitmap(); }
        finally { DestroyIcon(handles[0]); }
    }
    private static double VisibleDifference(Color a, Color b, int background) {
        double aa=a.A/255.0, ba=b.A/255.0;
        double r=Math.Abs(a.R*aa+background*(1-aa)-b.R*ba-background*(1-ba));
        double g=Math.Abs(a.G*aa+background*(1-aa)-b.G*ba-background*(1-ba));
        double blue=Math.Abs(a.B*aa+background*(1-aa)-b.B*ba-background*(1-ba));
        return Math.Max(r,Math.Max(g,blue));
    }
    private static bool SameAppearance(Bitmap actual, Bitmap expected, out double mean, out int significant) {
        mean=255; significant=int.MaxValue;
        if(actual.Size!=expected.Size) return false;
        int count=actual.Width*actual.Height, visible=0; double total=0; significant=0;
        for(int y=0;y<actual.Height;y++) for(int x=0;x<actual.Width;x++) {
            Color a=actual.GetPixel(x,y), b=expected.GetPixel(x,y);
            if(a.A>0) visible++;
            // Shell alpha premultiplication can round RGB by 1-2 units. Compare
            // displayed appearance on both dark and light backgrounds, not raw ARGB equality.
            double delta=Math.Max(VisibleDifference(a,b,0),VisibleDifference(a,b,255));
            total+=delta;
            if(delta>8) significant++;
        }
        mean=total/count;
        return visible>=count/10 && mean<=1.0 && significant<=count/100;
    }
    private static void VerifyAppearance(Bitmap actual, Bitmap expected, string label, string folder) {
        double mean; int significant;
        bool equal=SameAppearance(actual,expected,out mean,out significant);
        File.AppendAllText(Path.Combine(folder,"appearance.csv"),label+","+actual.Width+","+
            mean.ToString("F6",CultureInfo.InvariantCulture)+","+significant+","+equal+Environment.NewLine);
        if(!equal) throw new IOException("Icon appearance differs from reference: "+label);
    }
    public static void Verify(string exe, string reference, string folder) {
        File.WriteAllText(Path.Combine(folder,"appearance.csv"),"source,pixels,max-channel-mean,significant-pixels,match"+Environment.NewLine);
        foreach(int pixels in new[]{16,20,24,32,40,48,64,128,256}) {
            using(var actual=Extract(exe,pixels)) using(var expected=Extract(reference,pixels)) {
                actual.Save(Path.Combine(folder,"extracted-"+pixels+".png"),ImageFormat.Png);
                VerifyAppearance(actual,expected,"exe-"+pixels,folder);
                double mean; int significant;
                using(var blank=new Bitmap(expected.Width,expected.Height))
                    if(SameAppearance(blank,expected,out mean,out significant)) throw new IOException("Blank icon was incorrectly accepted");
                using(var source=SystemIcons.Application.ToBitmap()) using(var generic=new Bitmap(source,expected.Size))
                    if(SameAppearance(generic,expected,out mean,out significant)) throw new IOException("Generic application icon was incorrectly accepted");
            }
        }
        foreach(bool small in new[]{true,false}) {
            var info=new ShellFileInfo();
            if(SHGetFileInfo(exe,0,ref info,(uint)Marshal.SizeOf(typeof(ShellFileInfo)),0x100u|(small?1u:0u))==IntPtr.Zero || info.hIcon==IntPtr.Zero)
                throw new IOException("Shell did not return an installer icon");
            try {
                using(var icon=Icon.FromHandle(info.hIcon)) using(var bitmap=icon.ToBitmap()) using(var expected=Extract(reference,bitmap.Width)) {
                    string label=small?"shell-small":"shell-large";
                    expected.Save(Path.Combine(folder,label+"-reference.png"),ImageFormat.Png);
                    bitmap.Save(Path.Combine(folder,label+".png"),ImageFormat.Png);
                    VerifyAppearance(bitmap,expected,label,folder);
                }
            } finally { DestroyIcon(info.hIcon); }
        }
    }
}
'@
try {
    [H2VMSetupIconCheck]::Verify($Executable, $ReferenceIcon, $folder)
    'PASS Setup.exe icon extraction at 16/20/24/32/40/48/64/128/256, Shell small/large appearance, and blank/generic negative controls.' |
        Set-Content (Join-Path $folder 'result.txt') -Encoding UTF8
    Get-Content (Join-Path $folder 'result.txt')
}
catch {
    "FAIL $($_.Exception.Message)" | Set-Content (Join-Path $folder 'result.txt') -Encoding UTF8
    throw
}
