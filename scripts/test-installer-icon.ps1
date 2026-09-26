[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$ReferenceIcon = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\assets\Host2VMRelay.Setup.ico')
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$folder = Join-Path $repoRoot 'artifacts\checks\installer-icon'
New-Item -ItemType Directory -Force $folder | Out-Null
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$ReferenceIcon = (Resolve-Path -LiteralPath $ReferenceIcon).Path
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
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
    public static void Verify(string exe, string reference, string folder) {
        foreach(int pixels in new[]{16,20,24,32,40,48,64,128,256}) {
            using(var actual=Extract(exe,pixels)) using(var expected=Extract(reference,pixels)) {
                if(actual.Size!=expected.Size) throw new IOException("Icon dimensions differ at "+pixels);
                int differences=0, visible=0;
                for(int y=0;y<actual.Height;y++) for(int x=0;x<actual.Width;x++) {
                    Color a=actual.GetPixel(x,y), b=expected.GetPixel(x,y);
                    if(a.A>0) visible++;
                    if(a.ToArgb()!=b.ToArgb()) differences++;
                }
                if(visible<actual.Width*actual.Height/10 || differences>actual.Width*actual.Height/20)
                    throw new IOException("Setup icon is blank or differs from reference at "+pixels);
                actual.Save(Path.Combine(folder,"extracted-"+pixels+".png"),ImageFormat.Png);
            }
        }
        foreach(bool small in new[]{true,false}) {
            var info=new ShellFileInfo();
            if(SHGetFileInfo(exe,0,ref info,(uint)Marshal.SizeOf(typeof(ShellFileInfo)),0x100u|(small?1u:0u))==IntPtr.Zero || info.hIcon==IntPtr.Zero)
                throw new IOException("Shell did not return an installer icon");
            try {
                using(var icon=Icon.FromHandle(info.hIcon)) using(var bitmap=icon.ToBitmap()) using(var expected=Extract(reference,bitmap.Width)) {
                    if(bitmap.Size!=expected.Size) throw new IOException("Shell icon size differs from reference");
                    int different=0;
                    for(int y=0;y<bitmap.Height;y++) for(int x=0;x<bitmap.Width;x++)
                        if(bitmap.GetPixel(x,y).ToArgb()!=expected.GetPixel(x,y).ToArgb()) different++;
                    if(different>bitmap.Width*bitmap.Height/20) throw new IOException("Shell returned a generic or stale icon");
                    bitmap.Save(Path.Combine(folder,small?"shell-small.png":"shell-large.png"),ImageFormat.Png);
                }
            } finally { DestroyIcon(info.hIcon); }
        }
    }
}
'@
try {
    [H2VMSetupIconCheck]::Verify($Executable, $ReferenceIcon, $folder)
    'PASS Setup.exe native icon extraction at 16/20/24/32/40/48/64/128/256 and Shell small/large extraction.' |
        Set-Content (Join-Path $folder 'result.txt') -Encoding UTF8
    Get-Content (Join-Path $folder 'result.txt')
}
catch {
    "FAIL $($_.Exception.Message)" | Set-Content (Join-Path $folder 'result.txt') -Encoding UTF8
    throw
}
