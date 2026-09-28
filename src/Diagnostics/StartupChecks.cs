using Microsoft.Win32;

namespace Host2VMRelay;

internal static class StartupChecks
{
    public static void Run(Action<bool, string> check)
    {
        // Exercise the same registry implementation in a disposable, non-Run
        // key. Tests must never enable startup for the developer or CI account.
        string isolatedKey = @"Software\Host2VMRelay\Tests\" + Guid.NewGuid().ToString("N");
        var startup = new StartupRegistration(isolatedKey);
        const string first = @"C:\测试 空格\Host2VMRelay.exe", other = @"C:\Portable\Host2VMRelay.exe";
        try
        {
            startup.SetEnabled(true, first);
            check(startup.IsEnabled(first) && !startup.IsEnabled(other), "startup registration quotes Unicode paths and identifies its owner");
            startup.SetEnabled(false, other);
            check(startup.IsEnabled(first), "another portable copy cannot remove the registered startup entry");
            startup.SetEnabled(false, first);
            check(!startup.IsEnabled(first), "disabling startup removes the owned entry");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(isolatedKey, throwOnMissingSubKey: false); }
        check(!Settings.Deserialize("{}").SilentStart && Settings.Deserialize(new Settings { SilentStart = true }.Serialize()).SilentStart,
            "legacy settings show the window by default and preserve an explicit silent-start preference");
    }
}
