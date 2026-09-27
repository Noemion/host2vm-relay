param(
    [Parameter(Mandatory)][string]$Dotnet,
    [Parameter(Mandatory)][string]$Assembly,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
# Never switch the input desktop. The diagnostic owns and closes only its child.
Add-Type @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class IsolatedStartupDesktop {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct StartupInfo {
        public uint cb; public string reserved, desktop, title;
        public uint x,y,width,height,xchars,ychars,fill,flags;
        public ushort show,reservedSize; public IntPtr reservedPtr,input,output,error;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInfo { public IntPtr process,thread; public uint pid,tid; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr attributes);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern bool CreateProcess(string app,StringBuilder command,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr env,string cwd,ref StartupInfo startup,out ProcessInfo process);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process,out uint code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process,uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public static int Run(string command,string cwd) {
        string name="Host2VMRelay-Startup-"+Guid.NewGuid().ToString("N");
        IntPtr desktop=CreateDesktop(name,IntPtr.Zero,IntPtr.Zero,0,0x1ff,IntPtr.Zero);
        if(desktop==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            var startup=new StartupInfo {cb=(uint)Marshal.SizeOf<StartupInfo>(),desktop=name};
            ProcessInfo child;
            if(!CreateProcess(null,new StringBuilder(command),IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,cwd,ref startup,out child))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                if(WaitForSingleObject(child.process,20000)!=0) {
                    TerminateProcess(child.process,124); WaitForSingleObject(child.process,3000);
                    throw new TimeoutException("Isolated startup diagnostic exceeded 20 seconds.");
                }
                uint code; GetExitCodeProcess(child.process,out code); return (int)code;
            } finally {CloseHandle(child.thread);CloseHandle(child.process);}
        } finally {CloseDesktop(desktop);}
    }
}
'@
$runtimePath = (Resolve-Path -LiteralPath $Dotnet).Path
$assemblyPath = (Resolve-Path -LiteralPath $Assembly).Path
$outputPath = [IO.Path]::GetFullPath($Output)
if (($runtimePath + $assemblyPath + $outputPath).Contains('"')) { throw 'Quotes are not allowed in diagnostic paths.' }
$command = '"' + $runtimePath + '" exec "' + $assemblyPath + '" --startup-check "' + $outputPath + '"'
$code = [IsolatedStartupDesktop]::Run($command, $PWD.Path)
if (Test-Path -LiteralPath $outputPath) { Get-Content -LiteralPath $outputPath }
if ($code -ne 0) { throw "Startup diagnostic failed: $code" }
